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
    public async Task VerifyAsync_ProductSearchWaitsForToolRetrieverAndCompleteModelTelemetry()
    {
        var responses = new Queue<string>(
        [
            OperationResponseJson,
            CompleteTraceResponseJson,
            CompleteProductSearchTraceResponseJson,
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
            ["raw-user", "raw-workflow"],
            PhoenixChatTraceExpectation.ProductSearch);

        result.TraceId.Should().Be("trace-1");
        result.SpanCount.Should().Be(6);
        handler.RequestUris.Should().HaveCount(3);
    }

    [Fact]
    public async Task VerifyAsync_RedactedProductSearchAcceptsMetadataWithoutPayloads()
    {
        var responses = new Queue<string>(
        [
            OperationResponseWithoutUserJson,
            RedactedProductSearchTraceResponseJson.Replace(
                ",\"user.id\":\"hmac-v1-user\"",
                string.Empty,
                StringComparison.Ordinal),
        ]);
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

        var result = await verifier.VerifyAsync(
            new Uri("http://phoenix/"),
            "withlove-giftshop",
            "operation-123",
            [],
            PhoenixChatTraceExpectation.RedactedProductSearch);

        result.TraceId.Should().Be("trace-1");
        result.SpanCount.Should().Be(6);
    }

    public static TheoryData<string> UnsafeRedactedProductSearchCases => new()
    {
        RedactedProductSearchTraceResponseJson.Replace(
            "\"input.value\":\"__REDACTED__\",\"output.value\":\"__REDACTED__\"",
            "\"input.value\":\"secret prompt\",\"output.value\":\"__REDACTED__\"",
            StringComparison.Ordinal),
        RedactedProductSearchTraceResponseJson.Replace(
            "\"gen_ai.operation.name\":\"chat\",\"gen_ai.request.model\":\"gpt\"",
            "\"gen_ai.operation.name\":\"chat\",\"gen_ai.request.model\":\"gpt\",\"gen_ai.input.messages\":\"secret prompt\"",
            StringComparison.Ordinal),
        RedactedProductSearchTraceResponseJson.Replace(
            "\"gen_ai.operation.name\":\"chat\",\"gen_ai.request.model\":\"gpt\"",
            "\"gen_ai.operation.name\":\"chat\",\"gen_ai.request.model\":\"gpt\",\"gen_ai.output.messages\":\"secret response\"",
            StringComparison.Ordinal),
    };

    [Theory]
    [MemberData(nameof(UnsafeRedactedProductSearchCases))]
    public async Task VerifyAsync_RedactedProductSearchRejectsCapturedPayloads(string traceResponse)
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
            new Uri("http://phoenix/"),
            "withlove-giftshop",
            "operation-123",
            [],
            PhoenixChatTraceExpectation.RedactedProductSearch);

        await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*redacted product-search trace*");
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

    public static TheoryData<string> IncompleteProductSearchTraceCases => new()
    {
        CompleteProductSearchTraceResponseJson.Replace(
            "\"span_kind\":\"LLM\",\"context\":{\"trace_id\":\"trace-1\",\"span_id\":\"llm-1\"}",
            "\"span_kind\":\"INTERNAL\",\"context\":{\"trace_id\":\"trace-1\",\"span_id\":\"llm-1\"}",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "\"tool.name\":\"search_products\"",
            "\"tool.name\":\"search_catalog\"",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "\"tool.id\":\"call-1\"",
            "\"tool.missing_id\":\"call-1\"",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "\"input.value\":\"{query}\"",
            "\"input.missing_value\":\"{query}\"",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "\"output.value\":\"results\"",
            "\"output.missing_value\":\"results\"",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "retrieval.documents.0.document.id",
            "retrieval.documents.0.document.missing_id",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "\"span_id\":\"retriever-1\"},\"parent_id\":\"tool-1\"",
            "\"span_id\":\"retriever-1\"},\"parent_id\":\"durable-1\"",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "gen_ai.request.model",
            "gen_ai.request.missing_model",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "gen_ai.usage.input_tokens",
            "gen_ai.usage.missing_input_tokens",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "gen_ai.input.messages",
            "gen_ai.missing.input_messages",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "gen_ai.output.messages",
            "gen_ai.missing.output_messages",
            StringComparison.Ordinal),
        CompleteProductSearchTraceResponseJson.Replace(
            "gen_ai.usage.output_tokens",
            "gen_ai.usage.missing_output_tokens",
            StringComparison.Ordinal),
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

    [Theory]
    [MemberData(nameof(IncompleteProductSearchTraceCases))]
    public async Task VerifyAsync_ProductSearchRejectsIncompleteScenarioTelemetry(string traceResponse)
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
            new Uri("http://phoenix/"),
            "withlove-giftshop",
            "operation-123",
            [],
            PhoenixChatTraceExpectation.ProductSearch);

        await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*complete product-search trace*");
        handler.RequestUris.Should().HaveCount(2);
    }

    private const string OperationResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}}
        ]}
        """;

    private const string OperationResponseWithoutUserJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session"}}
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

    private const string CompleteProductSearchTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user"}},
          {"name":"durable.turn","span_kind":"INTERNAL","context":{"trace_id":"trace-1","span_id":"durable-1"},"parent_id":"chain-1",
           "attributes":{"conversation.id":"hmac-v1-session"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-1"},"parent_id":"durable-1",
           "attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt","gen_ai.usage.input_tokens":12,"gen_ai.usage.output_tokens":4,
                         "gen_ai.input.messages":"[{user}]","gen_ai.output.messages":"[{assistant-tool-call}]}"}},
          {"name":"execute_tool search_products","span_kind":"TOOL","context":{"trace_id":"trace-1","span_id":"tool-1"},"parent_id":"durable-1",
           "attributes":{"tool.name":"search_products","tool.id":"call-1","input.value":"{query}","output.value":"results"}},
          {"name":"product.search","span_kind":"RETRIEVER","context":{"trace_id":"trace-1","span_id":"retriever-1"},"parent_id":"tool-1",
           "attributes":{"retrieval.documents.0.document.id":"42"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-2"},"parent_id":"durable-1",
           "attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"gpt","gen_ai.usage.input_tokens":20,"gen_ai.usage.output_tokens":8,
                         "gen_ai.input.messages":"[{tool-result}]","gen_ai.output.messages":"[{assistant-final}]}"}}
        ]}
        """;

    private const string RedactedProductSearchTraceResponseJson = """
        {"data":[
          {"name":"chat.turn","span_kind":"CHAIN","context":{"trace_id":"trace-1","span_id":"chain-1"},"parent_id":null,
           "attributes":{"chat.operation_id":"operation-123","session.id":"hmac-v1-session","user.id":"hmac-v1-user","input.value":"__REDACTED__","output.value":"__REDACTED__"}},
          {"name":"durable.turn","span_kind":"INTERNAL","context":{"trace_id":"trace-1","span_id":"durable-1"},"parent_id":"chain-1",
           "attributes":{"conversation.id":"hmac-v1-session"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-1"},"parent_id":"durable-1",
           "attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt","gen_ai.usage.input_tokens":12,"gen_ai.usage.output_tokens":4}},
          {"name":"execute_tool search_products","span_kind":"TOOL","context":{"trace_id":"trace-1","span_id":"tool-1"},"parent_id":"durable-1",
           "attributes":{"tool.name":"search_products","tool.id":"call-1","input.value":"__REDACTED__","output.value":"__REDACTED__"}},
          {"name":"product.search","span_kind":"RETRIEVER","context":{"trace_id":"trace-1","span_id":"retriever-1"},"parent_id":"tool-1",
           "attributes":{"input.value":"__REDACTED__","retrieval.documents.0.document.id":"42"}},
          {"name":"openai.chat","span_kind":"LLM","context":{"trace_id":"trace-1","span_id":"llm-2"},"parent_id":"durable-1",
           "attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"gpt","gen_ai.usage.input_tokens":20,"gen_ai.usage.output_tokens":8}}
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
