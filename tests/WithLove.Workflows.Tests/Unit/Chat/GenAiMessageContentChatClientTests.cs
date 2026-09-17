using System.Diagnostics;
using Microsoft.Extensions.AI;
using WithLove.OpenInference;
using WithLove.WorkflowServer.Telemetry;

namespace WithLove.Workflows.Tests.Unit.Chat;

public class GenAiMessageContentChatClientTests
{
    [Fact]
    public async Task StreamingResponse_EnrichesTheExistingModelActivityWithoutCreatingAnotherSpan()
    {
        using var source = new ActivitySource("withlove-test-genai-message-content");
        var stopped = new List<Activity>();
        using var listener = Listen(source, stopped.Add);
        using var activity = source.StartActivity(
            "chat unknown",
            ActivityKind.Client,
            default(ActivityContext),
            [
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("conversation.id", "safe-session"),
            ]);
        var client = new GenAiMessageContentChatClient(
            new StreamingChatClient(
            [
                new ChatResponseUpdate(ChatRole.Assistant, "A lovely ")
                {
                    MessageId = "message-1",
                },
                new ChatResponseUpdate(null, "gift.")
                {
                    MessageId = "message-1",
                    FinishReason = ChatFinishReason.Stop,
                },
            ]),
            "test-model",
            OpenInferenceTraceConfig.Enabled);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Find a gift")],
                           new ChatOptions { Instructions = "Be helpful" }))
        {
            updates.Add(update);
        }

        activity.Should().NotBeNull();
        activity!.GetTagItem("gen_ai.system_instructions").Should().Be(
            "[{\"type\":\"text\",\"content\":\"Be helpful\"}]");
        activity.GetTagItem(OpenInferenceAttributes.SessionId).Should().Be("safe-session");
        activity.GetTagItem("gen_ai.input.messages").Should().Be(
            "[{\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"content\":\"Find a gift\"}]}]");
        activity.GetTagItem("gen_ai.output.messages").Should().Be(
            "[{\"role\":\"assistant\",\"parts\":[{\"type\":\"text\",\"content\":\"A lovely gift.\"}],\"finish_reason\":\"stop\"}]");
        activity.GetTagItem("gen_ai.request.model").Should().Be("test-model");
        activity.DisplayName.Should().Be("chat test-model");
        updates.Should().HaveCount(2);

        activity.Dispose();
        stopped.Should().ContainSingle().Which.Should().BeSameAs(activity);
    }

    [Fact]
    public async Task HiddenContent_DoesNotAttachMessageAttributes()
    {
        using var source = new ActivitySource("withlove-test-genai-message-content-hidden");
        using var listener = Listen(source, _ => { });
        using var activity = source.StartActivity(
            "chat unknown",
            ActivityKind.Client,
            default(ActivityContext),
            [new KeyValuePair<string, object?>("gen_ai.operation.name", "chat")]);
        var client = new GenAiMessageContentChatClient(
            new StreamingChatClient(
            [
                new ChatResponseUpdate(ChatRole.Assistant, "private response")
                {
                    MessageId = "message-1",
                    FinishReason = ChatFinishReason.Stop,
                },
            ]),
            "test-model",
            OpenInferenceTraceConfig.Disabled);

        await foreach (var _ in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "private request")]))
        {
        }

        activity.Should().NotBeNull();
        activity!.GetTagItem("gen_ai.system_instructions").Should().BeNull();
        activity.GetTagItem("gen_ai.input.messages").Should().BeNull();
        activity.GetTagItem("gen_ai.output.messages").Should().BeNull();
        activity.GetTagItem("gen_ai.request.model").Should().Be("test-model");
        activity.DisplayName.Should().Be("chat test-model");
    }

    private static ActivityListener Listen(ActivitySource source, Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class StreamingChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(updates.ToChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
