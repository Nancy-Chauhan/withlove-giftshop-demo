using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using WithLove.Web.Models;
using WithLove.Workflows.Chat;
using WithLove.Workflows.Workflows;

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

        // Built through GiftShopChatWorkflow.WorkflowIdFor so the session ID has exactly one
        // definition. The workflow's update validator rejects a turn whose UserContext.UserId does
        // not resolve to Workflow.Info.WorkflowId, so a second copy of this format string here
        // would break every authenticated chat the moment the two drifted.
        // An anonymous session has no user identity to bind, so it is keyed by an unguessable
        // GUID that intentionally cannot collide with any real user ID.
        _workflowId = userId is not null
            ? GiftShopChatWorkflow.WorkflowIdFor(userId)
            : GiftShopChatWorkflow.WorkflowIdFor($"anon-{Guid.NewGuid():N}");

        var name = auth.User.FindFirst(ClaimTypes.Name)?.Value
                   ?? auth.User.FindFirst(ClaimTypes.GivenName)?.Value;

        // The email claim is deliberately not read. UserContext is serialized into Temporal
        // workflow history on every model step and every tool call, and history is append-only —
        // so only data with a real server-side consumer is allowed to travel on it.
        if (name is not null || userId is not null)
            _userContext = new UserContext(name, userId);

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

        // Correlation only — this is a telemetry and log-stitching key, not an idempotency key.
        // A fresh GUID per call can never deduplicate anything, so it must not be used as a
        // Temporal Update ID; doing so advertises a safety guarantee that does not exist.
        var operationId = Guid.NewGuid().ToString("N");
        var completion = "Failed";
        UsageDetails? usage = null;
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

                    // Always bound the output budget. A single turn runs up to the workflow's
                    // 40-iteration tool cap, so an unbounded step does not cost one runaway
                    // generation — it costs forty, each retried up to three times by Temporal.
                    // 2,000 is sized for this workload: LA answers in 2-3 sentences and the
                    // largest legitimate output is a detailed product description. It is
                    // deliberately not tighter, because gpt-5-nano is a reasoning model whose
                    // reasoning tokens bill against this same budget, and a budget exhausted
                    // mid-reasoning yields an empty message rather than a truncated one.
                    //
                    // Temperature is intentionally left unset: reasoning models reject or ignore
                    // sampling parameters, so pinning it here would be misleading at best.
                    MaxOutputTokens = 2000,
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

            // Summed by the package across every model step in this turn, so it is the whole cost
            // of the turn and not just its last call. Recorded in the finally block so the
            // completion reason tag is the same one the duration histogram carries.
            usage = result.Response.Usage;

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
            var completionTag = new KeyValuePair<string, object?>("completion_reason", completion);
            instrumentation.ChatTurnDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                completionTag);
            RecordTokenUsage(usage, completionTag);
            IsThinking = false;
        }
    }

    /// <summary>
    /// Records the aggregated token cost of one durable turn.
    /// </summary>
    private void RecordTokenUsage(
        UsageDetails? usage,
        KeyValuePair<string, object?> completionTag)
    {
        if (usage is null)
        {
            instrumentation.ChatTurnsWithoutUsage.Add(1, completionTag);
            return;
        }

        var inputTokens = usage.InputTokenCount.GetValueOrDefault();
        var outputTokens = usage.OutputTokenCount.GetValueOrDefault();

        instrumentation.ChatTokensUsed.Add(
            inputTokens,
            new KeyValuePair<string, object?>("token_type", "input"),
            completionTag);
        instrumentation.ChatTokensUsed.Add(
            outputTokens,
            new KeyValuePair<string, object?>("token_type", "output"),
            completionTag);

        // The package sums InputTokenCount, OutputTokenCount and TotalTokenCount across steps and
        // nothing else, so a provider that reports no total leaves this at zero rather than null.
        // CachedInputTokenCount and ReasoningTokenCount survive only on the per-step gen_ai spans —
        // they are deliberately not broken out here, because a turn-level cached-token series would
        // read as a flat zero and imply this workload never hits the prompt cache.
        var totalTokens = usage.TotalTokenCount.GetValueOrDefault();
        if (totalTokens == 0)
            totalTokens = inputTokens + outputTokens;

        instrumentation.ChatTurnTokens.Record(totalTokens, completionTag);
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
