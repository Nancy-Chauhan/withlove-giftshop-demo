using WithLove.Web.Models;

namespace WithLove.Web.Services;

/// <summary>
/// In-memory only cart service — fallback for testing or when Redis is unavailable.
/// Implements the new async interface with synchronous bodies (no actual async operations).
/// Cart state is lost on page reload.
/// </summary>
public class InMemoryCartService : ICartService
{
    private readonly List<CartItem> _items = [];
    private readonly List<GiftEnhancement> _enhancements = [];

    public IReadOnlyList<CartItem> Items => _items.AsReadOnly();
    public int ItemCount => CartQuantity.TotalItems(_items);
    public decimal Subtotal => _items.Sum(i => i.Price * i.Quantity);
    public decimal EnhancementsTotal => _enhancements.Sum(e => e.Price);
    public decimal Total => Subtotal + EnhancementsTotal;
    public List<GiftEnhancement> Enhancements => _enhancements;

    public event Action? OnChange;

    public Task InitializeAsync(string userId, string? anonymousCartId = null)
        => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Applies the same <see cref="CartQuantity"/> bounds as
    /// <see cref="FusionCacheCartService"/>; the two implementations must agree or a test double
    /// will not reproduce production behaviour.
    /// </remarks>
    public Task AddItemAsync(CartItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var quantity = CartQuantity.Clamp(item.Quantity);
        var existing = _items.FirstOrDefault(i => i.ProductId == item.ProductId);
        if (existing is not null)
        {
            existing.Quantity = CartQuantity.ClampedSum(existing.Quantity, quantity);
        }
        else
        {
            item.Quantity = quantity;
            _items.Add(item);
        }
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public Task RemoveItemAsync(int productId)
    {
        _items.RemoveAll(i => i.ProductId == productId);
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public Task UpdateQuantityAsync(int productId, int quantity)
    {
        var item = _items.FirstOrDefault(i => i.ProductId == productId);
        if (item is not null)
        {
            if (quantity <= 0)
                _items.Remove(item);
            else
                item.Quantity = CartQuantity.Clamp(quantity);
        }
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public Task ToggleEnhancementAsync(GiftEnhancement enhancement)
    {
        var existing = _enhancements.FirstOrDefault(e => e.Id == enhancement.Id);
        if (existing is not null)
            _enhancements.Remove(existing);
        else
            _enhancements.Add(enhancement);
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _items.Clear();
        _enhancements.Clear();
        OnChange?.Invoke();
        return Task.CompletedTask;
    }
}
