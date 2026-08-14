using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using WithLove.Web.Models;
using WithLove.Workflows.Chat;

namespace WithLove.Web.Services;

/// <summary>Result from SendMessageAsync containing the response and any navigation requests.</summary>
public record ChatMessageResult(string AssistantMessage, List<NavigationAction> NavigationActions);

/// <summary>
/// Scoped service (one per SignalR circuit) that bridges Blazor UI with the Temporal chat workflow.
/// </summary>
public class ChatService(
    IGiftShopChatWorkflowClient workflowClient,
    AuthenticationStateProvider authStateProvider,
    ICartService cartService,
    Instrumentation instrumentation)
{
    private string? _workflowId;
    private bool _initialized;
    private UserContext? _userContext;

    /// <summary>Chat messages for UI rendering.</summary>
    public List<ChatHistoryEntry> Messages { get; } = [];

    /// <summary>Whether a message is currently being processed.</summary>
    public bool IsThinking { get; set; }

    /// <summary>Determines the session workflow ID based on authentication state.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        _workflowId = userId is not null
            ? $"giftshop-chat-{userId}"
            : $"giftshop-chat-anon-{Guid.NewGuid():N}";

        var name = auth.User.FindFirst(ClaimTypes.Name)?.Value
                   ?? auth.User.FindFirst(ClaimTypes.GivenName)?.Value;
        var email = auth.User.FindFirst(ClaimTypes.Email)?.Value;
        if (name is not null || email is not null || userId is not null)
            _userContext = new UserContext(name, email, userId);

        instrumentation.ChatSessionsStarted.Add(
            1,
            new KeyValuePair<string, object?>(
                "auth_type",
                userId is not null ? "authenticated" : "anonymous"));

        _initialized = true;
    }

    /// <summary>Starts the workflow, or returns the currently running session.</summary>
    public async Task EnsureWorkflowStartedAsync()
    {
        if (_workflowId is null)
            throw new InvalidOperationException("Call InitializeAsync first.");

        await workflowClient.EnsureStartedAsync(_workflowId);
    }

    /// <summary>Loads visible conversation history from Temporal for reconnect hydration.</summary>
    public async Task LoadHistoryAsync()
    {
        if (_workflowId is null)
            return;

        try
        {
            var history = await workflowClient.GetHistoryAsync(_workflowId);

            Messages.Clear();
            Messages.AddRange(GiftShopChatResponseProjector.ProjectHistory(history));
        }
        catch (Temporalio.Exceptions.RpcException exception)
            when (exception.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
        {
            // The workflow does not exist yet, so there is no history to hydrate.
        }
    }

    /// <summary>
    /// Sends one durable turn. The caller adds the user message to <see cref="Messages"/> before
    /// invoking this method so the UI can render it immediately.
    /// </summary>
    public async Task<ChatMessageResult> SendMessageAsync(string message)
    {
        if (_workflowId is null)
            throw new InvalidOperationException("Call InitializeAsync first.");

        var operationId = Guid.NewGuid().ToString("N");
        var completion = "Failed";
        var stopwatch = Stopwatch.StartNew();
        using var activity = instrumentation.ActivitySource.StartActivity("chat.turn");
        activity?.SetTag("chat.operation_id", operationId);

        try
        {
            await EnsureWorkflowStartedAsync();

            var cartSnapshot = cartService.Items
                .Select(item => new CartSnapshot(
                    item.ProductId,
                    item.ProductName,
                    item.Price,
                    item.Quantity))
                .ToArray();

            var request = new DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>
            {
                Messages = [new ChatMessage(ChatRole.User, message)],
                RequestData = new GiftShopChatRequestData(operationId, _userContext),
                InitialTurnState = GiftShopChatTurnState.Create(cartSnapshot),
                CorrelationId = operationId,
                ConversationId = _workflowId,
                ChatOptions = new ChatOptions
                {
                    Instructions = GiftShopChatPrompt.BuildInstructions(_userContext),
                },
                Options = new DurableTurnOptions
                {
                    DispatchMode = DurableToolDispatchMode.Sequential,
                },
            };

            var result = await workflowClient.SendMessageAsync(
                _workflowId,
                operationId,
                request);

            var assistantMessage = GiftShopChatResponseProjector.GetDisplayAssistantText(
                result.Response.Messages);

            Messages.Add(new ChatHistoryEntry(false, assistantMessage, DateTime.UtcNow));

            var navigationActions = new List<NavigationAction>();
            if (result.CompletionReason == DurableTurnCompletionReason.FinalResponse
                && result.FinalTurnState is { } finalState)
            {
                await ApplyCartActionsAsync(finalState.CartActions);
                navigationActions.AddRange(finalState.NavigationActions);
            }

            completion = result.CompletionReason.ToString();
            return new ChatMessageResult(assistantMessage, navigationActions);
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("chat.completion_reason", completion);
            if (completion == "Failed")
                activity?.SetStatus(ActivityStatusCode.Error);
            instrumentation.ChatTurnDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("completion_reason", completion));
            IsThinking = false;
        }
    }

    /// <summary>Ends the chat session by signaling the package workflow.</summary>
    public async Task EndSessionAsync()
    {
        if (_workflowId is null)
            return;

        try
        {
            await workflowClient.ShutdownAsync(_workflowId);
        }
        catch (Temporalio.Exceptions.RpcException)
        {
            // The workflow may already be completed.
        }

        Messages.Clear();
        _workflowId = null;
        _userContext = null;
        _initialized = false;
    }

    private async Task ApplyCartActionsAsync(IReadOnlyList<CartAction> actions)
    {
        foreach (var action in actions)
        {
            instrumentation.ChatCartActions.Add(
                1,
                new KeyValuePair<string, object?>("action", action.Type.ToString()));

            switch (action.Type)
            {
                case CartActionType.Add:
                    await cartService.AddItemAsync(new CartItem
                    {
                        ProductId = action.ProductId,
                        ProductName = action.ProductName,
                        ImageUrl = action.ImageUrl,
                        Price = action.Price,
                        StripePriceId = action.StripePriceId,
                        Quantity = action.Quantity,
                    });
                    break;

                case CartActionType.Remove:
                    await cartService.RemoveItemAsync(action.ProductId);
                    break;

                case CartActionType.Clear:
                    await cartService.ClearAsync();
                    break;
            }
        }
    }
}
