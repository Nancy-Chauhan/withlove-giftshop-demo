using WithLove.Web.Models;

namespace WithLove.Web.Services;

/// <summary>
/// Bounds and saturating arithmetic for cart line quantities.
/// </summary>
/// <remarks>
/// <para>
/// Cart quantities are not fully trusted input. Two of the three writers are outside the UI's
/// control: the chat assistant supplies quantities chosen by a language model, and
/// <see cref="FusionCacheCartService"/> rehydrates carts from a cache entry with a 30-day TTL,
/// which may have been written by an older build.
/// </para>
/// <para>
/// Unbounded <c>+=</c> on an <see cref="int"/> quantity wraps silently, so a large value followed
/// by a small one yields a negative quantity, a negative subtotal, and — because LINQ's
/// <c>Sum</c> over <c>int</c> is <c>checked</c> — an <see cref="OverflowException"/> from a
/// property that is read during render. Every mutation
/// therefore goes through <see cref="Clamp"/> or <see cref="ClampedSum"/>, and reads go through
/// <see cref="TotalItems"/>, which cannot throw.
/// </para>
/// <para>
/// <see cref="Max"/> deliberately matches
/// <c>GiftShopChatToolCatalog.MaxAddToCartQuantity</c>, so a bound the model is told about is the
/// same bound the cart enforces. It is also far below Stripe's documented per-line maximum, so a
/// valid cart always produces a valid checkout session.
/// </para>
/// </remarks>
public static class CartQuantity
{
    /// <summary>Smallest quantity a cart line may hold. Below this the line is removed.</summary>
    public const int Min = 1;

    /// <summary>Largest quantity a single cart line may hold.</summary>
    public const int Max = 99;

    /// <summary>Constrains a single quantity to <see cref="Min"/>..<see cref="Max"/>.</summary>
    public static int Clamp(int quantity) => Math.Clamp(quantity, Min, Max);

    /// <summary>
    /// Adds <paramref name="delta"/> to <paramref name="current"/> without wrapping. The addition
    /// is widened to <see cref="long"/> first so the intermediate value cannot overflow before it
    /// is clamped.
    /// </summary>
    public static int ClampedSum(int current, int delta) =>
        (int)Math.Clamp((long)current + delta, Min, Max);

    /// <summary>
    /// Returns a normalized copy of <paramref name="items"/>: out-of-range quantities are clamped
    /// and duplicate product lines are merged. Applied when loading persisted cart state so a cart
    /// poisoned before these bounds existed heals on its next read instead of throwing.
    /// </summary>
    public static List<CartItem> Sanitize(IEnumerable<CartItem>? items)
    {
        var sanitized = new List<CartItem>();
        if (items is null)
            return sanitized;

        foreach (var item in items)
        {
            if (item is null)
                continue;

            var existing = sanitized.Find(i => i.ProductId == item.ProductId);
            if (existing is not null)
            {
                existing.Quantity = ClampedSum(existing.Quantity, Clamp(item.Quantity));
                continue;
            }

            item.Quantity = Clamp(item.Quantity);
            sanitized.Add(item);
        }

        return sanitized;
    }

    /// <summary>
    /// Total number of units across every cart line. Accumulates in <see cref="long"/> and clamps,
    /// so this can never throw — it is read on every page render and on the checkout path, both
    /// outside any exception handler.
    /// </summary>
    public static int TotalItems(IEnumerable<CartItem> items) =>
        (int)Math.Clamp(items.Sum(item => (long)item.Quantity), 0L, int.MaxValue);
}
