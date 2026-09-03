using System.Net;
using System.Text;
using WithLove.Telemetry.Verifier;

namespace WithLove.Telemetry.Tests;

public class PhoenixChatTraceVerifierTests
{
    [Fact]
    public async Task VerifyAsync_PerformsOperationThenTraceLookupAndValidatesTrace()
    {
        var responses = new Queue<string>(
        [
            OperationResponseJson,
            PartialTraceResponseJson,
            CompleteTraceResponseJson,
        ]);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var verifier = new PhoenixChatTraceVerifier(client, new()
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.Zero,
            Deadline = TimeSpan.FromSeconds(1),
        });

        var result = await verifier.VerifyAsync(
            new Uri("http://phoenix/"),
            "withlove-giftshop",
            "operation-123",
            ["raw-user", "raw-workflow"]);

        result.TraceId.Should().Be("trace-1");
        result.SpanCount.Should().Be(3);
        handler.RequestUris.Should().HaveCount(3);
        handler.RequestUris[0].Query.Should().Contain("attribute=chat.operation_id%3Aoperation-123");
        handler.RequestUris[1].Query.Should().Be("?trace_id=trace-1");
        handler.RequestUris[2].Query.Should().Be("?trace_id=trace-1");
    }

    [Fact]
    public async Task VerifyAsync_StopsAtConfiguredAttemptCount()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var verifier = new PhoenixChatTraceVerifier(client, new()
        {
            MaxAttempts = 2,
            RetryDelay = TimeSpan.Zero,
            Deadline = TimeSpan.FromSeconds(1),
        });

        var action = () => verifier.VerifyAsync(
            new Uri("http://phoenix/"), "withlove-giftshop", "operation-123", []);

        await action.Should().ThrowAsync<TimeoutException>().WithMessage("*2 attempts*");
        handler.RequestUris.Should().HaveCount(2);
    }

    [Fact]
    public async Task VerifyAsync_StopsAtDeadlineWhileWaitingBetweenAttempts()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var verifier = new PhoenixChatTraceVerifier(client, new()
        {
            MaxAttempts = 20,
            RetryDelay = TimeSpan.FromSeconds(1),
            Deadline = TimeSpan.FromMilliseconds(20),
        });

        var action = () => verifier.VerifyAsync(
            new Uri("http://phoenix/"), "withlove-giftshop", "operation-123", []);

        await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("Phoenix polling exceeded its deadline.");
        handler.RequestUris.Should().ContainSingle();
    }

    [Fact]
    public async Task VerifyAsync_KeepsPollingWhenLlmIsOutsideTheChainHierarchy()
    {
        var responses = new Queue<string>(
        [
            OperationResponseJson,
            WrongHierarchyTraceResponseJson,
            WrongHierarchyTraceResponseJson,
        ]);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var verifier = new PhoenixChatTraceVerifier(client, new()
        {
            MaxAttempts = 2,
            RetryDelay = TimeSpan.Zero,
            Deadline = TimeSpan.FromSeconds(1),
        });

        var action = () => verifier.VerifyAsync(
            new Uri("http://phoenix/"), "withlove-giftshop", "operation-123", []);

        await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*CHAIN-ancestry MEAI LLM span*");
        handler.RequestUris.Should().HaveCount(3);
    }

    public static TheoryData<string> IncompleteTraceCases => new()
    {
        RawConversationTraceResponseJson,
        NonMeaiLlmTraceResponseJson,
        DuplicateSpanIdTraceResponseJson,
    };

    [Theory]
    [MemberData(nameof(IncompleteTraceCases))]
    public async Task VerifyAsync_DoesNotAcceptUnsafeOrDuplicatePartialTrace(string traceResponse)
    {
        var responses = new Queue<string>([OperationResponseJson, traceResponse]);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var verifier = new PhoenixChatTraceVerifier(client, new()
        {
            MaxAttempts = 1,
            RetryDelay = TimeSpan.Zero,
            Deadline = TimeSpan.FromSeconds(1),
        });

        var action = () => verifier.VerifyAsync(
            new Uri("http://phoenix/"), "withlove-giftshop", "operation-123", []);

        await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*pseudonymous conversation.id*");
        handler.RequestUris.Should().HaveCount(2);
    }

    private const string OperationResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}}
        ]}
        """;

    private const string PartialTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}}
        ]}
        """;

    private const string CompleteTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}},
          {"name":"durable.turn","span_kind":"INTERNAL","context":{"trace_id":"trace-1","span_id":"durable-1"},"parent_id":"chain-1",
           "attributes":{"conversation.id":"hmac-v1-session"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-1"},"parent_id":"durable-1",
           "attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt"}}
        ]}
        """;

    private const string WrongHierarchyTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}},
          {"name":"durable.turn","span_kind":"INTERNAL","context":{"trace_id":"trace-1","span_id":"durable-1"},"parent_id":"chain-1",
           "attributes":{"conversation.id":"hmac-v1-session"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-1"},"parent_id":"unrelated-1",
           "attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt"}}
        ]}
        """;

    private const string RawConversationTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}},
          {"name":"durable.turn","span_kind":"INTERNAL","context":{"trace_id":"trace-1","span_id":"durable-1"},"parent_id":"chain-1",
           "attributes":{"conversation.id":"raw-workflow"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-1"},"parent_id":"durable-1",
           "attributes":{"gen_ai.operation.name":"chat"}}
        ]}
        """;

    private const string NonMeaiLlmTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}},
          {"name":"durable.turn","span_kind":"INTERNAL","context":{"trace_id":"trace-1","span_id":"durable-1"},"parent_id":"chain-1",
           "attributes":{"conversation.id":"hmac-v1-session"}},
          {"name":"custom.llm","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-1"},"parent_id":"durable-1",
           "attributes":{"llm.system":"openai"}}
        ]}
        """;

    private const string DuplicateSpanIdTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}},
          {"name":"durable.turn","span_kind":"INTERNAL","context":{"trace_id":"trace-1","span_id":"duplicate-1"},"parent_id":"chain-1",
           "attributes":{"conversation.id":"hmac-v1-session"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"duplicate-1"},"parent_id":"chain-1",
           "attributes":{"gen_ai.operation.name":"chat"}}
        ]}
        """;

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        internal List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
