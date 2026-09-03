using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;
using WithLove.OpenInference;
using WithLove.Web.Telemetry;
using WithLove.Workflows.Chat;
using WithLove.Workflows.Workflows;

namespace WithLove.Web.Tests.Unit.Services;

public class ChatServiceTests : IDisposable
{
    private static readonly OpenInferenceTraceConfig VisibleContent = OpenInferenceTraceConfig.Create(
        new OpenInferenceOptions { HideInputs = false, HideOutputs = false },
        _ => null);
    private static readonly TelemetryIdentity TestTelemetryIdentity = TelemetryIdentity.Create(
        Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
        "test-v1");
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
        capturedRequest.ConversationId.Should().StartWith("hmac-test-v1-");
        capturedRequest.ConversationId.Should().NotContain(capturedWorkflowId!);
        capturedRequest.Messages.Should().ContainSingle();
        capturedRequest.Messages[0].Role.Should().Be(ChatRole.User);
        capturedRequest.Messages[0].Text.Should().Be("Add the keepsake");
        capturedRequest.Options.DispatchMode.Should().Be(DurableToolDispatchMode.Sequential);
        capturedRequest.ChatOptions!.Tools.Should().BeNull();
        capturedRequest.ChatOptions.AdditionalProperties.Should().ContainKey(
            $"{TemporalChatOptionsExtensions.ChatClientTagsKeyPrefix}chat.operation_id")
            .WhoseValue.Should().Be(capturedUpdateId);
        capturedRequest.InitialTurnState!.CartActions.Should().BeEmpty();
        capturedRequest.InitialTurnState.NavigationActions.Should().BeEmpty();
        result.AssistantMessage.Should().Be("Added it for you.");
        result.OperationId.Should().Be(capturedUpdateId);
        result.NavigationActions.Should().ContainSingle(action => action.Url == "/cart");
        A.CallTo(() => _cart.AddItemAsync(A<CartItem>.That.Matches(item => item.ProductId == 7)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_ExportsOnlyPseudonymousIdentityAndRoutesWithRawWorkflowId()
    {
        const string rawUserId = "raw-authenticated-user-123";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, rawUserId)],
            authenticationType: "test"));
        A.CallTo(() => _authentication.GetAuthenticationStateAsync())
            .Returns(new AuthenticationState(principal));
        string? routedWorkflowId = null;
        DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>? capturedRequest = null;
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Invokes((string workflowId, string _,
                DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request) =>
            {
                routedWorkflowId = workflowId;
                capturedRequest = request;
            })
            .Returns(FinalResult("Done.", GiftShopChatTurnState.Create([])));
        var service = CreateService();
        await service.InitializeAsync();
        using var telemetry = new ChatTurnTelemetryCapture(_instrumentation);

        var result = await service.SendMessageAsync("private prompt");

        routedWorkflowId.Should().Be(GiftShopChatWorkflow.WorkflowIdFor(rawUserId));
        capturedRequest!.ConversationId.Should().StartWith("hmac-test-v1-");
        capturedRequest.ConversationId.Should().NotContain(rawUserId);
        result.OperationId.Should().NotBeNullOrWhiteSpace();
        telemetry.ShouldContainOnlySafeIdentity(rawUserId, routedWorkflowId!);
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
    public async Task IncompleteResponse_UsesSameFallbackImmediatelyAndAfterReconnect()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var timestamp = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var response = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, string.Empty),
        ])
        {
            FinishReason = ChatFinishReason.Length,
        };
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Returns(new DurableTurnResult<GiftShopChatTurnState>
            {
                Response = response,
                CompletionReason = DurableTurnCompletionReason.IncompleteResponse,
                FinalTurnState = new GiftShopChatTurnState(
                    [],
                    [new CartAction(CartActionType.Clear)],
                    [new NavigationAction(NavigationTarget.Checkout, "/checkout")]),
            });
        A.CallTo(() => _workflowClient.GetHistoryAsync(A<string>._))
            .Returns(
            [
                DurableSessionRequest.FromMessages(
                    [new ChatMessage(ChatRole.User, "Try this")],
                    "fallback-1",
                    timestamp),
                DurableSessionResponse.FromChatResponse(
                    "fallback-1",
                    new ChatResponse(new ChatMessage(
                        ChatRole.Assistant,
                        "The model did not produce a complete final response."))
                    {
                        FinishReason = ChatFinishReason.Length,
                    },
                    timestamp,
                    DurableTurnCompletionReason.IncompleteResponse),
            ]);

        var immediate = await service.SendMessageAsync("Try this");
        await service.LoadHistoryAsync();

        immediate.AssistantMessage.Should().Be(GiftShopChatResponseProjector.AssistantFallback);
        immediate.NavigationActions.Should().BeEmpty();
        A.CallTo(() => _cart.ClearAsync()).MustNotHaveHappened();
        service.Messages.Select(message => message.Text).Should().Equal(
            "Try this",
            GiftShopChatResponseProjector.AssistantFallback);
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task IncompleteResponse_HidesPartialProviderText()
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
                    "This answer was cut off before it was complete"))
                {
                    FinishReason = ChatFinishReason.Length,
                },
                CompletionReason = DurableTurnCompletionReason.IncompleteResponse,
                FinalTurnState = GiftShopChatTurnState.Create([]),
            });

        var result = await service.SendMessageAsync("Try this");

        result.AssistantMessage.Should().Be(GiftShopChatResponseProjector.AssistantFallback);
        service.Messages.Last().Text.Should().Be(GiftShopChatResponseProjector.AssistantFallback);
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

        telemetry.ShouldHaveRecorded(
            "FinalResponse",
            ActivityStatusCode.Ok,
            expectedInput: "Finish",
            expectedOutput: "Done.");
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

        telemetry.ShouldHaveRecorded(
            "IterationLimitReached",
            ActivityStatusCode.Ok,
            expectedInput: "Keep trying",
            expectedOutput: GiftShopChatResponseProjector.IterationLimitMessage);
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_IncompleteResponseRecordsAlignedCompletionTelemetry()
    {
        var service = CreateService();
        await service.InitializeAsync();
        A.CallTo(() => _workflowClient.SendMessageAsync(
                A<string>._,
                A<string>._,
                A<DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>>._))
            .Returns(new DurableTurnResult<GiftShopChatTurnState>
            {
                Response = new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty))
                {
                    FinishReason = ChatFinishReason.Length,
                },
                CompletionReason = DurableTurnCompletionReason.IncompleteResponse,
                FinalTurnState = GiftShopChatTurnState.Create([]),
            });
        using var telemetry = new ChatTurnTelemetryCapture(_instrumentation);

        await service.SendMessageAsync("Try again");

        telemetry.ShouldHaveRecorded(
            "IncompleteResponse",
            ActivityStatusCode.Ok,
            expectedInput: "Try again",
            expectedOutput: GiftShopChatResponseProjector.AssistantFallback);
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
        telemetry.ShouldHaveRecorded(
            "Failed",
            ActivityStatusCode.Error,
            expectedInput: "Fail");
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
    public async Task SendMessage_UsesLargerOutputBudgetAndLowReasoningEffort()
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
        capturedOptions!.MaxOutputTokens.Should().Be(4000);
        capturedOptions.Reasoning.Should().NotBeNull();
        capturedOptions.Reasoning!.Effort.Should().Be(ReasoningEffort.Low);
        capturedOptions.Reasoning.Output.Should().Be(ReasoningOutput.None);

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
        new(
            _workflowClient,
            _authentication,
            _cart,
            _instrumentation,
            TestTelemetryIdentity,
            VisibleContent);

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

        public void ShouldHaveRecorded(
            string completionReason,
            ActivityStatusCode status,
            string? expectedInput = null,
            string? expectedOutput = null)
        {
            _activities.Should().ContainSingle();
            var activity = _activities.Single();
            activity.GetTagItem("chat.completion_reason").Should().Be(completionReason);
            activity.Status.Should().Be(status);
            if (expectedInput is not null)
            {
                activity.GetTagItem(OpenInferenceAttributes.InputValue).Should().Be(expectedInput);
                activity.GetTagItem(OpenInferenceAttributes.InputMimeType).Should().Be("text/plain");
            }
            if (expectedOutput is not null)
            {
                activity.GetTagItem(OpenInferenceAttributes.OutputValue).Should().Be(expectedOutput);
                activity.GetTagItem(OpenInferenceAttributes.OutputMimeType).Should().Be("text/plain");
            }
            else
            {
                activity.GetTagItem(OpenInferenceAttributes.OutputValue).Should().BeNull();
            }
            _histogramReasons.Should().ContainSingle().Which.Should().Be(completionReason);
        }

        public void ShouldContainOnlySafeIdentity(params string[] rawIdentifiers)
        {
            var activity = _activities.Should().ContainSingle().Subject;
            activity.GetTagItem(OpenInferenceAttributes.OpenInferenceSpanKind).Should().Be("CHAIN");
            activity.GetTagItem(OpenInferenceAttributes.SessionId).Should().BeOfType<string>()
                .Which.Should().StartWith("hmac-test-v1-");
            activity.GetTagItem(OpenInferenceAttributes.UserId).Should().BeOfType<string>()
                .Which.Should().StartWith("hmac-test-v1-");
            var exportedText = string.Join('\n', activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
            foreach (var rawIdentifier in rawIdentifiers)
                exportedText.Should().NotContain(rawIdentifier);
            activity.TagObjects.Should().NotContain(tag => tag.Key == "temporalWorkflowID");
        }

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }
    }
}
