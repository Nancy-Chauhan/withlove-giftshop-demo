namespace WithLove.Workflows.Chat;

/// <summary>Trusted Web-created user context used for prompt personalization and tool reads.</summary>
/// <remarks>
/// Keep this record as small as it can possibly be. It rides along on the Update payload and is
/// re-serialized into Temporal workflow history on every model step and every tool invocation — a
/// single turn can persist it dozens of times. Workflow history is append-only, so anything added
/// here is effectively undeletable for the life of the retention period.
/// <para>
/// Customer email was deliberately removed: its only reader was the system prompt, and personal
/// data that exists purely to be interpolated into a prompt does not belong in durable history.
/// Add a field here only when a server-side consumer genuinely needs it.
/// </para>
/// </remarks>
public record UserContext(string? Name, string? UserId = null);

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
