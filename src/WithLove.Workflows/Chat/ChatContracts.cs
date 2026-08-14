namespace WithLove.Workflows.Chat;

/// <summary>Trusted Web-created user context used for prompt personalization and tool reads.</summary>
public record UserContext(string? Name, string? Email, string? UserId = null);

/// <summary>Application data available to durable tool activities for one chat turn.</summary>
public sealed record GiftShopChatRequestData(string OperationId, UserContext? User = null);

/// <summary>Application-owned working state and structured output for one durable chat turn.</summary>
public sealed record GiftShopChatTurnState(
    IReadOnlyList<CartSnapshot> WorkingCart,
    IReadOnlyList<CartAction> CartActions,
    IReadOnlyList<NavigationAction> NavigationActions)
{
    public static GiftShopChatTurnState Create(IEnumerable<CartSnapshot> cart) =>
        new(cart.Select(item => item with { }).ToArray(), [], []);
}

/// <summary>Cart mutations collected by tools, applied client-side.</summary>
public record CartAction(
    CartActionType Type,
    int ProductId = 0,
    string ProductName = "",
    string ImageUrl = "",
    decimal Price = 0,
    string StripePriceId = "",
    int Quantity = 1);

public enum CartActionType { Add, Remove, Clear }

/// <summary>Lightweight snapshot of a cart item, passed from Web to workflow/activity.</summary>
public record CartSnapshot(int ProductId, string ProductName, decimal Price, int Quantity);

/// <summary>Visible user/final-assistant entry projected for the chat UI.</summary>
public record ChatHistoryEntry(bool IsUser, string Text, DateTime Timestamp);

/// <summary>Navigation request emitted by tools, executed client-side.</summary>
public record NavigationAction(NavigationTarget Target, string Url, string? Label = null);

public enum NavigationTarget { Product, Collection, Cart, Checkout }
