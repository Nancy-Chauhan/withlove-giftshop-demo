using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using WithLove.Workflows.Chat;

namespace WithLove.Web.Tests.Unit.Services;

public class ChatServiceTests : IDisposable
{
    private readonly IGiftShopChatWorkflowClient _workflowClient =
        A.Fake<IGiftShopChatWorkflowClient>();
    private readonly AuthenticationStateProvider _authentication =
        A.Fake<AuthenticationStateProvider>();
    private readonly ICartService _cart = A.Fake<ICartService>();
    private readonly Instrumentation _instrumentation = new();

    public ChatServiceTests()
    {
        A.CallTo(() => _authentication.GetAuthenticationStateAsync())
            .Returns(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        A.CallTo(() => _cart.Items).Returns(Array.Empty<CartItem>());
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_UsesOneOperationIdentityAndSequentialTypedState()
    {
        var service = CreateService();
        await service.InitializeAsync();
        DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>? capturedRequest = null;
        string? capturedWorkflowId = null;
        string? capturedUpdateId = null;
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Invokes((string workflowId, string updateId,
                DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request) =>
            {
                capturedWorkflowId = workflowId;
                capturedUpdateId = updateId;
                capturedRequest = request;
            })
            .Returns(FinalResult(
                "Added it for you.",
                new GiftShopChatTurnState(
                    [new CartSnapshot(7, "Keepsake", 25m, 1)],
                    [new CartAction(CartActionType.Add, 7, "Keepsake", "/7.jpg", 25m, "price_7")],
                    [new NavigationAction(NavigationTarget.Cart, "/cart")] )));

        var result = await service.SendMessageAsync("Add the keepsake");

        capturedWorkflowId.Should().StartWith("giftshop-chat-anon-");
        capturedUpdateId.Should().NotBeNullOrWhiteSpace();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestData.OperationId.Should().Be(capturedUpdateId);
        capturedRequest.CorrelationId.Should().Be(capturedUpdateId);
        capturedRequest.ConversationId.Should().Be(capturedWorkflowId);
        capturedRequest.Messages.Should().ContainSingle();
        capturedRequest.Messages[0].Role.Should().Be(ChatRole.User);
        capturedRequest.Messages[0].Text.Should().Be("Add the keepsake");
        capturedRequest.Options.DispatchMode.Should().Be(DurableToolDispatchMode.Sequential);
        capturedRequest.ChatOptions!.Tools.Should().BeNull();
        capturedRequest.InitialTurnState!.CartActions.Should().BeEmpty();
        capturedRequest.InitialTurnState.NavigationActions.Should().BeEmpty();
        result.AssistantMessage.Should().Be("Added it for you.");
        result.NavigationActions.Should().ContainSingle(action => action.Url == "/cart");
        A.CallTo(() => _cart.AddItemAsync(A<CartItem>.That.Matches(item => item.ProductId == 7)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_IterationLimitDisplaysSentinelAndAppliesNoCommands()
    {
        var service = CreateService();
        await service.InitializeAsync();
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Returns(new DurableTurnResult<GiftShopChatTurnState>
            {
                Response = new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    GiftShopChatResponseProjector.IterationLimitMessage)),
                CompletionReason = DurableTurnCompletionReason.IterationLimitReached,
                FinalTurnState = new GiftShopChatTurnState(
                    [],
                    [new CartAction(CartActionType.Clear)],
                    [new NavigationAction(NavigationTarget.Checkout, "/checkout")]),
            });

        var result = await service.SendMessageAsync("Keep trying");

        result.AssistantMessage.Should().Be(GiftShopChatResponseProjector.IterationLimitMessage);
        result.NavigationActions.Should().BeEmpty();
        A.CallTo(() => _cart.ClearAsync()).MustNotHaveHappened();
        A.CallTo(() => _cart.AddItemAsync(A<CartItem>._)).MustNotHaveHappened();
        A.CallTo(() => _cart.RemoveItemAsync(A<int>._)).MustNotHaveHappened();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task EmptyAssistantOutput_UsesSameFallbackImmediatelyAndAfterReconnect()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var timestamp = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var response = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, string.Empty),
        ]);
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Returns(new DurableTurnResult<GiftShopChatTurnState>
            {
                Response = response,
                CompletionReason = DurableTurnCompletionReason.FinalResponse,
                FinalTurnState = GiftShopChatTurnState.Create([]),
            });
        A.CallTo(() => _workflowClient.GetHistoryAsync(A<string>._))
            .Returns(
            [
                DurableSessionRequest.FromMessages(
                    [new ChatMessage(ChatRole.User, "Try this")],
                    "fallback-1",
                    timestamp),
                DurableSessionResponse.FromChatResponse("fallback-1", response, timestamp),
            ]);

        var immediate = await service.SendMessageAsync("Try this");
        await service.LoadHistoryAsync();

        immediate.AssistantMessage.Should().Be(GiftShopChatResponseProjector.AssistantFallback);
        service.Messages.Select(message => message.Text).Should().Equal(
            "Try this",
            GiftShopChatResponseProjector.AssistantFallback);
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void DisplayProjection_UsesIntermediateAssistantTextAndHidesToolProtocol()
    {
        var timestamp = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var messages = new ChatMessage[]
        {
            new(ChatRole.Assistant, "Let me check that for you."),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-1",
                    "search_products",
                    new Dictionary<string, object?> { ["query"] = "gift" }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "internal result")]),
            new(ChatRole.Assistant, string.Empty),
        };
        var history = new DurableSessionEntry[]
        {
            DurableSessionResponse.FromChatResponse(
                "projection-1",
                new ChatResponse(messages),
                timestamp),
        };

        GiftShopChatResponseProjector.GetDisplayAssistantText(messages)
            .Should().Be("Let me check that for you.");
        GiftShopChatResponseProjector.ProjectHistory(history)
            .Should().ContainSingle()
            .Which.Text.Should().Be("Let me check that for you.");
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_FinalResponseRecordsAlignedCompletionTelemetry()
    {
        var service = CreateService();
        await service.InitializeAsync();
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Returns(FinalResult("Done.", GiftShopChatTurnState.Create([])));
        using var telemetry = new ChatTurnTelemetryCapture(_instrumentation);

        await service.SendMessageAsync("Finish");

        telemetry.ShouldHaveRecorded("FinalResponse", ActivityStatusCode.Unset);
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_IterationLimitRecordsAlignedCompletionTelemetry()
    {
        var service = CreateService();
        await service.InitializeAsync();
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Returns(new DurableTurnResult<GiftShopChatTurnState>
            {
                Response = new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    GiftShopChatResponseProjector.IterationLimitMessage)),
                CompletionReason = DurableTurnCompletionReason.IterationLimitReached,
                FinalTurnState = GiftShopChatTurnState.Create([]),
            });
        using var telemetry = new ChatTurnTelemetryCapture(_instrumentation);

        await service.SendMessageAsync("Keep trying");

        telemetry.ShouldHaveRecorded("IterationLimitReached", ActivityStatusCode.Unset);
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_WorkflowFailureRecordsFailedTelemetryAndErrorStatus()
    {
        var service = CreateService();
        await service.InitializeAsync();
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .ThrowsAsync(new InvalidOperationException("workflow failed"));
        using var telemetry = new ChatTurnTelemetryCapture(_instrumentation);

        Func<Task> send = () => service.SendMessageAsync("Fail");

        await send.Should().ThrowAsync<InvalidOperationException>();
        telemetry.ShouldHaveRecorded("Failed", ActivityStatusCode.Error);
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task Initialize_AuthenticatedUserUsesStableGiftShopWorkflowPrefix()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "customer-42"),
            new Claim(ClaimTypes.GivenName, "Morgan"),
            new Claim(ClaimTypes.Email, "morgan@example.test"),
        ], "test");
        A.CallTo(() => _authentication.GetAuthenticationStateAsync())
            .Returns(new AuthenticationState(new ClaimsPrincipal(identity)));
        var service = CreateService();

        await service.InitializeAsync();
        await service.EnsureWorkflowStartedAsync();

        A.CallTo(() => _workflowClient.EnsureStartedAsync("giftshop-chat-customer-42"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task LoadHistory_ProjectsOnlyVisibleUserAndFinalAssistantText()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var timestamp = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        A.CallTo(() => _workflowClient.GetHistoryAsync(A<string>._))
            .Returns(
            [
                DurableSessionRequest.FromMessages(
                    [new ChatMessage(ChatRole.User, "Find a gift")],
                    "history-1",
                    timestamp),
                DurableSessionResponse.FromChatResponse(
                    "history-1",
                    new ChatResponse(
                    [
                        new ChatMessage(ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                "call-1",
                                "search_products",
                                new Dictionary<string, object?> { ["query"] = "gift" }),
                        ]),
                        new ChatMessage(ChatRole.Tool,
                        [
                            new FunctionResultContent("call-1", "internal result"),
                        ]),
                        new ChatMessage(ChatRole.Assistant, "This keepsake is a lovely choice."),
                    ]),
                    timestamp),
            ]);

        await service.LoadHistoryAsync();

        service.Messages.Select(message => message.Text).Should().Equal(
            "Find a gift",
            "This keepsake is a lovely choice.");
        service.Messages.Should().NotContain(message => message.Text.Contains("internal result"));
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task EndSession_SignalsShutdownAndResetsAnonymousSession()
    {
        var service = CreateService();
        await service.InitializeAsync();
        await service.EnsureWorkflowStartedAsync();
        var firstWorkflowId = Fake.GetCalls(_workflowClient)
            .Single(call => call.Method.Name == nameof(IGiftShopChatWorkflowClient.EnsureStartedAsync))
            .Arguments[0] as string;

        await service.EndSessionAsync();
        await service.InitializeAsync();
        await service.EnsureWorkflowStartedAsync();

        A.CallTo(() => _workflowClient.ShutdownAsync(firstWorkflowId!))
            .MustHaveHappenedOnceExactly();
        var workflowIds = Fake.GetCalls(_workflowClient)
            .Where(call => call.Method.Name == nameof(IGiftShopChatWorkflowClient.EnsureStartedAsync))
            .Select(call => call.Arguments[0] as string)
            .ToList();
        workflowIds.Should().HaveCount(2);
        workflowIds[1].Should().NotBe(firstWorkflowId);
    }

    public void Dispose() => _instrumentation.Dispose();

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_BoundsTheOutputBudgetAndLeavesSamplingUnset()
    {
        var service = CreateService();
        await service.InitializeAsync();
        ChatOptions? capturedOptions = null;
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Invokes((string _, string _,
                DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request) =>
                capturedOptions = request.ChatOptions)
            .Returns(FinalResult("Done.", GiftShopChatTurnState.Create([])));

        await service.SendMessageAsync("Hello");

        capturedOptions.Should().NotBeNull();

        // An unbounded step does not cost one runaway generation. A single turn runs up to the
        // workflow's 40-iteration tool cap, and Temporal retries each step three times, so the
        // worst case is 120 unbounded generations for one customer message.
        capturedOptions!.MaxOutputTokens.Should().Be(2000);

        // gpt-5-nano is a reasoning model: sampling parameters are rejected or ignored, so pinning
        // Temperature would advertise control the deployment does not actually have.
        capturedOptions.Temperature.Should().BeNull();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_WhenTheTurnFails_PropagatesAndStopsThinking()
    {
        var service = CreateService();
        await service.InitializeAsync();
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Throws(new InvalidOperationException("Workflow update failed"));
        service.IsThinking = true;

        var send = () => service.SendMessageAsync("Hello");

        // ChatService deliberately does not swallow this. There is no ErrorBoundary above ChatFab,
        // so its catch block is the only thing between a failed durable turn and a torn-down
        // circuit — this test is what makes that catch load-bearing rather than defensive noise.
        await send.Should().ThrowAsync<InvalidOperationException>();

        // The finally block still runs, so the thinking indicator does not stick on the failure
        // path. ChatFab clears it a second time because this only happens once the try is entered.
        service.IsThinking.Should().BeFalse();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void AssistantFallback_IsCustomerSafeProse() =>
        // Rendered verbatim to the customer by ChatFab's catch block, so it must never grow an
        // exception message, a workflow ID or a stack frame.
        GiftShopChatResponseProjector.AssistantFallback.Should().Be(
            "Hmm, something went sideways on my end. Mind trying that again?");

    private ChatService CreateService() =>
        new(_workflowClient, _authentication, _cart, _instrumentation);

    private static DurableTurnResult<GiftShopChatTurnState> FinalResult(
        string assistantMessage,
        GiftShopChatTurnState state) =>
        new()
        {
            Response = new ChatResponse(new ChatMessage(ChatRole.Assistant, assistantMessage)),
            CompletionReason = DurableTurnCompletionReason.FinalResponse,
            FinalTurnState = state,
        };

    private sealed class ChatTurnTelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;
        private readonly ConcurrentQueue<Activity> _activities = new();
        private readonly ConcurrentQueue<string> _histogramReasons = new();

        public ChatTurnTelemetryCapture(Instrumentation instrumentation)
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, instrumentation.ActivitySource),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _activities.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, instrumentation.Meter)
                        && instrument.Name == "chat.turn.duration_ms")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "completion_reason" && tag.Value is string reason)
                        _histogramReasons.Enqueue(reason);
                }
            });
            _meterListener.Start();
        }

        public void ShouldHaveRecorded(string completionReason, ActivityStatusCode status)
        {
            _activities.Should().ContainSingle();
            var activity = _activities.Single();
            activity.GetTagItem("chat.completion_reason").Should().Be(completionReason);
            activity.Status.Should().Be(status);
            _histogramReasons.Should().ContainSingle().Which.Should().Be(completionReason);
        }

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }
    }
}
