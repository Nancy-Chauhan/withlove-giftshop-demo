using System.Text.Json;

namespace WithLove.Telemetry.Verifier;

public sealed class PhoenixChatTraceVerifier(
    HttpClient httpClient,
    PhoenixPollingOptions? options = null)
{
    private readonly PhoenixPollingOptions _options = options ?? new();

    public async Task<PhoenixVerificationResult> VerifyAsync(
        Uri phoenixBaseUri,
        string projectName,
        string operationId,
        IReadOnlyCollection<string> forbiddenRawIdentifiers,
        PhoenixChatTraceExpectation? expectation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(forbiddenRawIdentifiers);
        var resolvedExpectation = expectation ?? PhoenixChatTraceExpectation.GenericChat;

        using var operationSpans = await PollAsync(
            phoenixBaseUri,
            $"v1/projects/{Uri.EscapeDataString(projectName)}/spans?attribute=" +
            Uri.EscapeDataString($"chat.operation_id:{operationId}"),
            static root => root.GetProperty("data").GetArrayLength() > 0,
            "an operation span",
            cancellationToken);
        var chain = operationSpans.RootElement.GetProperty("data").EnumerateArray()
            .Where(span => span.GetProperty("span_kind").GetString() == "CHAIN")
            .ToArray();
        if (chain.Length != 1)
            throw new InvalidOperationException($"Expected exactly one CHAIN for operation {operationId}; found {chain.Length}.");

        var traceId = chain[0].GetProperty("context").GetProperty("trace_id").GetString();
        if (string.IsNullOrWhiteSpace(traceId))
            throw new InvalidOperationException("The operation CHAIN did not contain context.trace_id.");
        var chainSpanId = chain[0].GetProperty("context").GetProperty("span_id").GetString();
        if (string.IsNullOrWhiteSpace(chainSpanId))
            throw new InvalidOperationException("The operation CHAIN did not contain context.span_id.");

        using var traceSpans = await PollAsync(
            phoenixBaseUri,
            $"v1/projects/{Uri.EscapeDataString(projectName)}/spans?trace_id={Uri.EscapeDataString(traceId)}",
            root => HasCompleteChatStructure(
                root,
                traceId,
                chainSpanId,
                resolvedExpectation),
            resolvedExpectation.PollingDescription,
            cancellationToken);
        var spans = traceSpans.RootElement.GetProperty("data").EnumerateArray().ToArray();
        if (spans.Length == 0) throw new InvalidOperationException("Phoenix returned an empty trace.");
        if (spans.Any(span => span.GetProperty("context").GetProperty("trace_id").GetString() != traceId))
            throw new InvalidOperationException("Phoenix returned spans from more than one trace.");
        var spanIds = spans.Select(GetSpanId).ToArray();
        if (spanIds.Distinct(StringComparer.Ordinal).Count() != spanIds.Length)
            throw new InvalidOperationException("The trace contained duplicate span IDs.");

        var llmSpans = spans.Where(IsLlmSpan).ToArray();
        if (llmSpans.Length == 0)
            throw new InvalidOperationException("The chat trace contained no MEAI LLM span.");
        if (llmSpans.Any(span => !IsMeaiLlmSpan(span)))
            throw new InvalidOperationException(
                "The chat trace contained an LLM span not owned by MEAI; this indicates duplicate model instrumentation.");
        if (llmSpans.Any(span => !IsDescendantOf(span, chainSpanId, spans)))
            throw new InvalidOperationException("A MEAI LLM span was not a descendant of the operation CHAIN.");

        var conversationIds = spans
            .Select(span => TryGetStringAttribute(span, "conversation.id"))
            .Where(value => value is not null)
            .ToArray();
        if (conversationIds.Length == 0
            || conversationIds.Any(value => !value!.StartsWith("hmac-", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The trace contained no conversation.id or contained a non-pseudonymous conversation.id.");
        }

        var serialized = traceSpans.RootElement.GetRawText();
        foreach (var forbidden in forbiddenRawIdentifiers.Where(value => !string.IsNullOrEmpty(value)))
        {
            if (serialized.Contains(forbidden, StringComparison.Ordinal))
                throw new InvalidOperationException("An exported span contained a forbidden raw identity.");
        }
        if (serialized.Contains("temporalWorkflowID", StringComparison.Ordinal))
            throw new InvalidOperationException("An exported span contained temporalWorkflowID.");

        var attributes = chain[0].GetProperty("attributes");
        RequireSafeAttribute(attributes, "session.id");
        RequireSafeAttribute(attributes, "user.id");
        if (attributes.GetProperty("chat.operation_id").GetString() != operationId)
            throw new InvalidOperationException("The CHAIN operation ID did not match the browser handoff.");

        return new PhoenixVerificationResult(traceId, spans.Length);
    }

    private async Task<JsonDocument> PollAsync(
        Uri baseUri,
        string path,
        Func<JsonElement, bool> expected,
        string expectation,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_options.Deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(new Uri(baseUri, path), linked.Token);
                if (response.IsSuccessStatusCode)
                {
                    var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(linked.Token));
                    if (expected(document.RootElement)) return document;
                    document.Dispose();
                    lastFailure = new InvalidOperationException(
                        $"Phoenix had not yet ingested {expectation}.");
                }
                else lastFailure = new HttpRequestException($"Phoenix returned {(int)response.StatusCode}.");
            }
            catch (HttpRequestException exception) when (!linked.IsCancellationRequested)
            {
                lastFailure = exception;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Phoenix polling exceeded its deadline.", exception);
            }
            if (attempt < _options.MaxAttempts)
            {
                try
                {
                    await Task.Delay(_options.RetryDelay, linked.Token);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Phoenix polling exceeded its deadline.", exception);
                }
            }
        }
        throw new TimeoutException(
            $"Phoenix did not return {expectation} after {_options.MaxAttempts} attempts and {_options.Deadline}.",
            lastFailure);
    }

    private static bool HasCompleteChatStructure(
        JsonElement root,
        string traceId,
        string chainSpanId,
        PhoenixChatTraceExpectation expectation)
    {
        var spans = root.GetProperty("data").EnumerateArray().ToArray();
        if (spans.Length == 0
            || spans.Any(span => GetTraceId(span) != traceId)
            || HasDuplicateSpanIds(spans))
        {
            return false;
        }

        var llmSpans = spans.Where(IsLlmSpan).ToArray();
        var conversationIds = spans
            .Select(span => TryGetStringAttribute(span, "conversation.id"))
            .Where(value => value is not null)
            .ToArray();
        return llmSpans.Length >= expectation.MinimumLlmSpanCount
            && llmSpans.All(IsMeaiLlmSpan)
            && llmSpans.All(span => IsDescendantOf(span, chainSpanId, spans))
            && conversationIds.Length > 0
            && conversationIds.All(value => value!.StartsWith("hmac-", StringComparison.Ordinal))
            && MeetsScenarioExpectation(spans, llmSpans, chainSpanId, expectation);
    }

    private static bool MeetsScenarioExpectation(
        IReadOnlyCollection<JsonElement> spans,
        IReadOnlyCollection<JsonElement> llmSpans,
        string chainSpanId,
        PhoenixChatTraceExpectation expectation)
    {
        if (expectation.RequireModelAndTokenAttributes
            && llmSpans.Any(span => !HasModelAndTokenAttributes(span)))
        {
            return false;
        }

        if (expectation.RequireCapturedModelMessages
            && llmSpans.Any(span => !HasCapturedModelExchange(span)))
        {
            return false;
        }

        if (expectation.ExpectedToolName is { } expectedToolName)
        {
            var matchingTools = spans
                .Where(span => IsSpanKind(span, "TOOL"))
                .Where(span => TryGetStringAttribute(span, "tool.name") == expectedToolName)
                .ToArray();
            if (matchingTools.Length == 0
                || matchingTools.Any(span => !IsDescendantOf(span, chainSpanId, spans))
                || matchingTools.Any(span => !HasNonEmptyStringAttribute(span, "tool.id"))
                || matchingTools.Any(span => !HasNonEmptyStringAttribute(span, "input.value"))
                || matchingTools.Any(span => !HasNonEmptyStringAttribute(span, "output.value")))
            {
                return false;
            }
        }

        if (expectation.RequireRetriever)
        {
            var matchingTools = expectation.ExpectedToolName is { } retrieverToolName
                ? spans
                    .Where(span => IsSpanKind(span, "TOOL"))
                    .Where(span => TryGetStringAttribute(span, "tool.name") == retrieverToolName)
                    .ToArray()
                : [];
            var retrievers = spans
                .Where(span => IsSpanKind(span, "RETRIEVER"))
                .Where(span => IsDescendantOf(span, chainSpanId, spans))
                .ToArray();
            if (retrievers.Length == 0
                || retrievers.All(span => !HasRetrievalDocumentId(span))
                || matchingTools.Length > 0
                && retrievers.All(retriever => matchingTools.All(tool =>
                    !IsDescendantOf(retriever, GetSpanId(tool), spans))))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasModelAndTokenAttributes(JsonElement span) =>
        HasAnyAttribute(span, "gen_ai.request.model", "gen_ai.response.model", "llm.model_name")
        && HasAnyNumericAttribute(span, "gen_ai.usage.input_tokens", "llm.token_count.prompt")
        && HasAnyNumericAttribute(span, "gen_ai.usage.output_tokens", "llm.token_count.completion");

    private static bool HasCapturedModelExchange(JsonElement span) =>
        HasAttributeOrPrefix(span, "gen_ai.input.messages", "llm.input_messages.")
        && HasAttributeOrPrefix(span, "gen_ai.output.messages", "llm.output_messages.");

    private static bool HasRetrievalDocumentId(JsonElement span) =>
        span.TryGetProperty("attributes", out var attributes)
        && attributes.EnumerateObject().Any(attribute =>
            attribute.Name.StartsWith("retrieval.documents.", StringComparison.Ordinal)
            && attribute.Name.EndsWith(".document.id", StringComparison.Ordinal)
            && attribute.Value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(attribute.Value.GetString()));

    private static bool HasAnyAttribute(JsonElement span, params string[] names) =>
        span.TryGetProperty("attributes", out var attributes)
        && names.Any(name => attributes.TryGetProperty(name, out var value)
            && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);

    private static bool HasAnyNumericAttribute(JsonElement span, params string[] names) =>
        span.TryGetProperty("attributes", out var attributes)
        && names.Any(name => attributes.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number);

    private static bool HasAttributeOrPrefix(
        JsonElement span,
        string literalName,
        string flattenedPrefix) =>
        span.TryGetProperty("attributes", out var attributes)
        && (attributes.TryGetProperty(literalName, out var literal)
            && literal.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(literal.GetString())
            || attributes.EnumerateObject().Any(attribute =>
                attribute.Name.StartsWith(flattenedPrefix, StringComparison.Ordinal)));

    private static bool HasNonEmptyStringAttribute(JsonElement span, string name) =>
        TryGetStringAttribute(span, name) is { Length: > 0 };

    private static bool IsSpanKind(JsonElement span, string kind) =>
        string.Equals(
            span.GetProperty("span_kind").GetString(),
            kind,
            StringComparison.Ordinal);

    private static bool IsLlmSpan(JsonElement span) =>
        span.GetProperty("span_kind").GetString() == "LLM";

    private static bool IsMeaiLlmSpan(JsonElement span) =>
        IsLlmSpan(span)
        && TryGetStringAttribute(span, "gen_ai.operation.name") is not null;

    private static bool IsDescendantOf(
        JsonElement span,
        string ancestorSpanId,
        IReadOnlyCollection<JsonElement> spans)
    {
        var bySpanId = spans.ToDictionary(GetSpanId, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var parentId = GetParentId(span);
        while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
        {
            if (parentId == ancestorSpanId) return true;
            if (!bySpanId.TryGetValue(parentId, out var parent)) return false;
            parentId = GetParentId(parent);
        }

        return false;
    }

    private static bool HasDuplicateSpanIds(IReadOnlyCollection<JsonElement> spans) =>
        spans.Select(GetSpanId).Distinct(StringComparer.Ordinal).Count() != spans.Count;

    private static string GetTraceId(JsonElement span) =>
        span.GetProperty("context").GetProperty("trace_id").GetString() ?? string.Empty;

    private static string GetSpanId(JsonElement span) =>
        span.GetProperty("context").GetProperty("span_id").GetString() ?? string.Empty;

    private static string? GetParentId(JsonElement span) =>
        span.TryGetProperty("parent_id", out var parent)
            && parent.ValueKind == JsonValueKind.String
                ? parent.GetString()
                : null;

    private static string? TryGetStringAttribute(JsonElement span, string name) =>
        span.TryGetProperty("attributes", out var attributes)
        && attributes.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void RequireSafeAttribute(JsonElement attributes, string name)
    {
        if (!attributes.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || !value.GetString()!.StartsWith("hmac-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"CHAIN attribute {name} was missing or not pseudonymous.");
        }
    }
}

public sealed record PhoenixPollingOptions
{
    public int MaxAttempts { get; init; } = 20;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record PhoenixVerificationResult(string TraceId, int SpanCount);

/// <summary>Describes the telemetry shape that must be present before verification succeeds.</summary>
public sealed record PhoenixChatTraceExpectation
{
    /// <summary>Accepts a connected chat trace containing at least one MEAI-owned LLM span.</summary>
    public static PhoenixChatTraceExpectation GenericChat { get; } = new();

    /// <summary>
    /// Requires the deterministic product-search path, including model exchanges, tool execution,
    /// and backend retrieval.
    /// </summary>
    public static PhoenixChatTraceExpectation ProductSearch { get; } = new()
    {
        MinimumLlmSpanCount = 2,
        ExpectedToolName = "search_products",
        RequireRetriever = true,
        RequireModelAndTokenAttributes = true,
        RequireCapturedModelMessages = true,
        PollingDescription = "a complete product-search trace with model, TOOL, and RETRIEVER telemetry",
    };

    /// <summary>Gets the minimum number of MEAI-owned LLM spans required in the trace.</summary>
    public int MinimumLlmSpanCount { get; init; } = 1;

    /// <summary>Gets the exact OpenInference tool name that must appear, if any.</summary>
    public string? ExpectedToolName { get; init; }

    /// <summary>Gets whether a descendant RETRIEVER with at least one document ID is required.</summary>
    public bool RequireRetriever { get; init; }

    /// <summary>Gets whether every LLM span must contain model and input/output token attributes.</summary>
    public bool RequireModelAndTokenAttributes { get; init; }

    /// <summary>Gets whether every LLM span must contain captured input and output messages.</summary>
    public bool RequireCapturedModelMessages { get; init; }

    /// <summary>Gets the description included in polling timeout diagnostics.</summary>
    public string PollingDescription { get; init; } =
        "a CHAIN-ancestry MEAI LLM span with a pseudonymous conversation.id";
}
