using Temporalio.Exceptions;
using WithLove.Web.Middleware;
using WithLove.Workflows.Workflows;

namespace WithLove.Web.Services;

/// <summary>
/// Rotates the anonymous chat identity at an authentication boundary and shuts down the session it
/// replaces.
/// </summary>
/// <remarks>
/// <para>
/// The invariant this exists to hold is one sentence: <em>an anonymous chat identity never spans an
/// authentication boundary.</em> Logout alone would prevent leakage across a shared browser, but
/// rotating at login too means a reader does not have to work out which boundary is load-bearing.
/// </para>
/// <para>
/// Called from exactly two places, both of which are already full HTTP round-trips and can
/// therefore write a <c>Set-Cookie</c> header: <c>Login.razor</c> on a successful sign-in, and the
/// <c>/logout</c> endpoint. End Chat deliberately does <em>not</em> rotate — it runs inside a
/// SignalR circuit where there is no <c>HttpResponse</c> to write an <c>HttpOnly</c> cookie to, and
/// it does not need to: shutting the run down is enough once the history query refuses to read a
/// closed run.
/// </para>
/// </remarks>
public sealed class ChatIdentityRotator(
    IGiftShopChatWorkflowClient workflowClient,
    ILogger<ChatIdentityRotator> logger)
{
    /// <summary>
    /// Upper bound on how long shutting down the replaced session may delay the user.
    /// </summary>
    /// <remarks>
    /// A logout must never fail, or noticeably stall, because the AI chat backend is unavailable.
    /// An orphaned workflow that reaps itself within 24 hours is a far better outcome than a
    /// customer who cannot sign out.
    /// </remarks>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Issues a fresh <c>wl-chat-id</c> and asks the session it replaced to shut down.
    /// </summary>
    public async Task RotateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A cookie can only be written while the response headers are still ours to change. Both
        // call sites satisfy that today — a minimal API handler and a static-SSR form post — but
        // adding [StreamRendering] to the login page would quietly stop satisfying it, and the
        // resulting failure would be an anonymous chat identity surviving an authentication
        // boundary. Loud, and still not fatal to the sign-in or sign-out the user asked for.
        if (context.Response.HasStarted)
        {
            logger.ChatIdentityRotationSkipped();
            return;
        }

        // Read before overwriting. Reversed, the previous identity — and with it the only handle on
        // the workflow that needs shutting down — is unrecoverable.
        var previousChatId = ChatIdentityCookie.TryRead(context);
        ChatIdentityCookie.Mint(context);

        if (previousChatId is null)
            return;

        var workflowId = GiftShopChatWorkflow.WorkflowIdFor($"anon-{previousChatId}");

        try
        {
            using var budget = new CancellationTokenSource(ShutdownBudget);
            await workflowClient.ShutdownAsync(workflowId, budget.Token);
        }
        catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
        {
            // Expected and common: the workflow starts lazily on the first message, so any visitor
            // who never chatted has no run to shut down. Not a warning.
            logger.ChatSessionNotStartedAtRotation(workflowId);
        }
        catch (Exception exception)
        {
            // Deliberately broad. Everything reachable here — Temporal unreachable, the shutdown
            // budget expiring, a transport fault — is a reason to log and carry on, never a reason
            // to fail the authentication transition the user actually asked for.
            logger.ChatSessionShutdownFailedAtRotation(exception, workflowId);
        }
    }
}

internal static partial class ChatIdentityRotatorLogging
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The response had already started, so the anonymous chat identity was not "
                  + "rotated. It will survive this authentication boundary.")]
    internal static partial void ChatIdentityRotationSkipped(this ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No chat session to shut down for {WorkflowId} while rotating the chat identity.")]
    internal static partial void ChatSessionNotStartedAtRotation(
        this ILogger logger,
        string workflowId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not shut down chat session {WorkflowId} while rotating the chat identity. "
                  + "The session is orphaned and will reap itself; continuing.")]
    internal static partial void ChatSessionShutdownFailedAtRotation(
        this ILogger logger,
        Exception exception,
        string workflowId);
}
