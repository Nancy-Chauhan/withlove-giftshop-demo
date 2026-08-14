using System.Linq.Expressions;
using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using WithLove.Workflows;
using WithLove.Workflows.Chat;
using WithLove.Workflows.Workflows;

namespace WithLove.Web.Tests.Unit.Services;

public class GiftShopChatWorkflowClientTests
{
    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task EnsureStarted_UsesConcreteWorkflowFactoryInputAndExplicitIdPolicies()
    {
        var temporalClient = A.Fake<ITemporalClient>();
        var inputFactory = A.Fake<IDurableChatWorkflowInputFactory>();
        var input = new DurableChatWorkflowInput();
        A.CallTo(() => inputFactory.Create()).Returns(input);
        Expression<Func<GiftShopChatWorkflow, Task>>? runCall = null;
        WorkflowOptions? options = null;
        A.CallTo(() => temporalClient.StartWorkflowAsync(
                A<Expression<Func<GiftShopChatWorkflow, Task>>>._,
                A<WorkflowOptions>._))
            .Invokes((Expression<Func<GiftShopChatWorkflow, Task>> expression,
                WorkflowOptions startOptions) =>
            {
                runCall = expression;
                options = startOptions;
            })
            .Returns(Task.FromResult<WorkflowHandle<GiftShopChatWorkflow>>(null!));
        var client = new GiftShopChatWorkflowClient(temporalClient, inputFactory);

        await client.EnsureStartedAsync("giftshop-chat-customer-42");

        runCall.Should().NotBeNull();
        ((MethodCallExpression)runCall!.Body).Method.DeclaringType
            .Should().Be(typeof(GiftShopChatWorkflow));
        A.CallTo(() => inputFactory.Create()).MustHaveHappenedOnceExactly();
        options.Should().NotBeNull();
        options!.Id.Should().Be("giftshop-chat-customer-42");
        options.TaskQueue.Should().Be(WorkflowConstants.DefaultTaskQueue);
        options.IdConflictPolicy.Should().Be(WorkflowIdConflictPolicy.UseExisting);
        options.IdReusePolicy.Should().Be(WorkflowIdReusePolicy.AllowDuplicate);
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_RejectsCallerSuppliedToolsBeforeTemporalDispatch()
    {
        var temporalClient = A.Fake<ITemporalClient>();
        var client = new GiftShopChatWorkflowClient(
            temporalClient,
            A.Fake<IDurableChatWorkflowInputFactory>());
        var request = new DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>
        {
            Messages = [new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User,
                "Hello")],
            RequestData = new GiftShopChatRequestData("caller-tools"),
            InitialTurnState = GiftShopChatTurnState.Create([]),
            CorrelationId = "caller-tools",
            ChatOptions = new Microsoft.Extensions.AI.ChatOptions
            {
                Tools = [GiftShopChatToolCatalog.CreateDeclarations()[0]],
            },
        };

        Func<Task> send = () => client.SendMessageAsync(
            "giftshop-chat-test",
            "caller-tools",
            request);

        await send.Should().ThrowAsync<DurableConfigurationException>()
            .WithMessage("ChatOptions.Tools cannot be used*");
        Fake.GetCalls(temporalClient).Should().BeEmpty();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SendMessage_RejectsNullOptionsBeforeTemporalDispatch()
    {
        var temporalClient = A.Fake<ITemporalClient>();
        var client = new GiftShopChatWorkflowClient(
            temporalClient,
            A.Fake<IDurableChatWorkflowInputFactory>());
        var request = new DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>
        {
            Messages = [new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User,
                "Hello")],
            RequestData = new GiftShopChatRequestData("null-options"),
            InitialTurnState = GiftShopChatTurnState.Create([]),
            CorrelationId = "null-options",
            Options = null!,
        };

        Func<Task> send = () => client.SendMessageAsync(
            "giftshop-chat-test",
            "null-options",
            request);

        await send.Should().ThrowAsync<DurableConfigurationException>()
            .WithMessage("DurableTurnRequest.Options cannot be null*");
        Fake.GetCalls(temporalClient).Should().BeEmpty();
    }
}
