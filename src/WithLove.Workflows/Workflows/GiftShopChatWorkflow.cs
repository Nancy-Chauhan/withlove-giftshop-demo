using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using Temporalio.Workflows;
using WithLove.Workflows.Chat;

namespace WithLove.Workflows.Workflows;

[Workflow("WithLove.GiftShopChatWorkflow")]
public sealed class GiftShopChatWorkflow
    : DurableToolWorkflowBase<GiftShopChatRequestData, GiftShopChatTurnState>
{
    /// <summary>
    /// Prefix every chat session workflow ID carries. An authenticated session is
    /// <c>giftshop-chat-{userId}</c>; an anonymous session is <c>giftshop-chat-anon-{guid}</c>.
    /// The ID is therefore the authoritative record of who owns the session.
    /// </summary>
    public const string WorkflowIdPrefix = "giftshop-chat-";

    /// <summary>Builds the session workflow ID that belongs to <paramref name="userId"/>.</summary>
    public static string WorkflowIdFor(string userId) => WorkflowIdPrefix + userId;

    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdateValidator(nameof(SendMessageAsync))]
    public void ValidateSendMessage(
        DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (request.RequestData is null)
            throw new ArgumentException("Request data is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RequestData.OperationId))
            throw new ArgumentException("Operation ID is required.", nameof(request));
        if (!string.Equals(
                request.CorrelationId,
                request.RequestData.OperationId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Correlation ID must match the request operation ID.",
                nameof(request));
        }

        if (request.InitialTurnState is null)
            throw new ArgumentException("Initial turn state is required.", nameof(request));
        if (request.InitialTurnState.WorkingCart is null
            || request.InitialTurnState.CartActions is null
            || request.InitialTurnState.NavigationActions is null)
        {
            throw new ArgumentException("Turn-state collections are required.", nameof(request));
        }
        if (request.InitialTurnState.CartActions.Count != 0
            || request.InitialTurnState.NavigationActions.Count != 0)
        {
            throw new ArgumentException(
                "Initial turn state cannot contain pre-populated actions.",
                nameof(request));
        }

        if (request.Messages is not { Count: 1 } || request.Messages[0] is not { } message)
            throw new ArgumentException("Exactly one user message is required.", nameof(request));
        if (message.Role != ChatRole.User)
            throw new ArgumentException("The turn message must have the user role.", nameof(request));
        if (message.Contents is not { Count: 1 }
            || message.Contents[0] is not TextContent { Text: { } text }
            || string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "The turn must contain one non-empty user text item.",
                nameof(request));
        }

        if (request.Options is null
            || request.Options.DispatchMode != DurableToolDispatchMode.Sequential)
        {
            throw new ArgumentException("GiftShop tools must run sequentially.", nameof(request));
        }
        if (request.ChatOptions?.Tools is not null)
            throw new ArgumentException("Caller-supplied tools are not supported.", nameof(request));

        // Defence in depth on session ownership. UserId is populated server-side from
        // ClaimTypes.NameIdentifier and is not reachable from the model, but the workflow ID is
        // the only durable statement of who owns this session and the workflow is the only place
        // that can check the pairing. Doing it in the validator rather than the update handler
        // means a mismatched request is rejected without being written to history at all.
        // Anonymous sessions carry no UserId and are keyed by an unguessable GUID instead.
        if (request.RequestData.User?.UserId is { Length: > 0 } userId
            && !string.Equals(
                Workflow.Info.WorkflowId,
                WorkflowIdFor(userId),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The request user does not own this chat session.",
                nameof(request));
        }
    }

    [WorkflowUpdate("SendMessage")]
    public Task<DurableTurnResult<GiftShopChatTurnState>> SendMessageAsync(
        DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request) =>
        RunDurableTurnAsync(request);
}
