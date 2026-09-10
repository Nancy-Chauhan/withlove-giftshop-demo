namespace WithLove.Web.Middleware;

/// <summary>
/// The single definition of the <c>wl-chat-id</c> cookie: its name, its shape, and the only two
/// operations anything is allowed to perform on it.
/// </summary>
/// <remarks>
/// <para>
/// Three call sites write this cookie — <see cref="AnonymousChatMiddleware"/> mints it when absent,
/// and login and logout rotate it. Centralising the <see cref="CookieOptions"/> here means those
/// three sites cannot drift into three subtly different cookies.
/// </para>
/// <para>
/// The value is deliberately the raw identity, not a hash of one. It is concatenated into
/// <c>giftshop-chat-anon-{chatId}</c>, and keeping that derivation reversible by eye is what lets a
/// developer follow one browser session straight to its execution in the Temporal UI. Hashing would
/// add no guessing resistance — the value is already 122 bits from the platform CSPRNG — and knowing
/// the workflow ID is in any case sufficient to read the transcript via a Temporal query, so a hash
/// would protect the cookie and not the thing the cookie is for.
/// </para>
/// </remarks>
public static class ChatIdentityCookie
{
    /// <summary>Name of the cookie carrying the anonymous chat identity.</summary>
    public const string CookieName = "wl-chat-id";

    /// <summary>
    /// How long a returning visitor keeps the same anonymous chat identity.
    /// </summary>
    /// <remarks>
    /// Two hours, chosen over the workflow's own 24-hour run lifetime to keep the shared-machine
    /// window tight: on a borrowed or public browser, the next person inherits a chat identity for
    /// at most one sitting. Every realistic resume — F5, a second tab, a dropped SignalR circuit,
    /// reopening a tab — happens within minutes, so the shorter bound costs the feature nothing.
    /// <para>
    /// This is a hygiene bound on identifier lifetime and <em>not</em> a correctness mechanism. A
    /// cookie that outlives its workflow is the normal case, not an error: <c>EnsureStartedAsync</c>
    /// uses <c>IdConflictPolicy.UseExisting</c> with <c>IdReusePolicy.AllowDuplicate</c>, so a stale
    /// pointer simply starts a new run under the same ID. What keeps that honest is the
    /// <c>QueryRejectCondition.NotOpen</c> on the history query, which stops a closed run's
    /// transcript from being rendered — not this expiry.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>Length of a <c>Guid.ToString("N")</c> value.</summary>
    private const int ChatIdLength = 32;

    /// <summary>
    /// Returns the chat identity carried by the request, or <see langword="null"/> when the cookie
    /// is absent or does not have the exact shape this application mints.
    /// </summary>
    /// <remarks>
    /// A cookie is attacker-controlled input and this value is concatenated into a workflow ID, so
    /// anything that is not precisely 32 lowercase hex characters is treated as absent. That turns
    /// "attacker-controlled string spliced into a workflow ID" into "attacker-controlled opaque
    /// 128-bit token". It does <em>not</em> defend against an attacker who has learned another
    /// visitor's cookie value and replays it — that is ordinary session fixation, and entropy is the
    /// only defence against it.
    /// <para>
    /// This validation is a deliberate divergence from <see cref="AnonymousCartMiddleware"/>, which
    /// performs none. That is a defect in the cart path rather than a pattern worth mirroring.
    /// </para>
    /// </remarks>
    public static string? TryRead(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var chatId = context.Request.Cookies[CookieName];
        return IsWellFormed(chatId) ? chatId : null;
    }

    /// <summary>
    /// Writes a freshly minted chat identity to the response and returns it.
    /// </summary>
    /// <remarks>
    /// Rotation is a single <c>Append</c> that overwrites, never a <c>Delete</c> followed by an
    /// <c>Append</c>: two <c>Set-Cookie</c> headers for one name is browser-dependent, one overwrite
    /// is deterministic.
    /// </remarks>
    public static string Mint(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var chatId = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(CookieName, chatId, new CookieOptions
        {
            // Nothing client-side reads this. The workflow ID is derived server-side only, so
            // HttpOnly is free XSS reduction — at the cost that the cookie can be rotated only at
            // an HTTP boundary, which is why End Chat does not rotate it and login/logout do.
            HttpOnly = true,

            // Must not be Strict. Under Strict a visitor arriving from an email link or a search
            // result sends no cookie on the top-level navigation, the middleware sees "absent" and
            // mints a fresh one — silently destroying the session on every inbound link.
            SameSite = SameSiteMode.Lax,

            // This cookie is a bearer capability for an anonymous transcript, so it must never be
            // sent over plaintext HTTP. The application already redirects to HTTPS before this
            // middleware runs, making the protection free for local and hosted environments.
            Secure = true,
            Expires = DateTimeOffset.UtcNow.Add(Lifetime),

            // Honestly false: remove chat and the shop still works, so this cookie is not essential
            // to a functioning storefront the way wl-cart-id is. UseCookiePolicy is not registered,
            // so the flag is inert today; if a consent gate is ever added, this cookie stops being
            // written and ChatService.InitializeAsync throws immediately rather than silently
            // degrading back to a per-circuit identity.
            IsEssential = false,

        });

        return chatId;
    }

    private static bool IsWellFormed(string? chatId)
    {
        if (chatId is not { Length: ChatIdLength })
            return false;

        foreach (var character in chatId)
        {
            var isLowerHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowerHex)
                return false;
        }

        return true;
    }
}
