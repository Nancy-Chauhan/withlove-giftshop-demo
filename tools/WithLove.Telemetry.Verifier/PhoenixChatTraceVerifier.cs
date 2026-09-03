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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(forbiddenRawIdentifiers);

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
            root => HasCompleteChatStructure(root, traceId, chainSpanId),
            "a CHAIN-ancestry MEAI LLM span with a pseudonymous conversation.id",
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
        string chainSpanId)
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
        return llmSpans.Length > 0
            && llmSpans.All(IsMeaiLlmSpan)
            && llmSpans.All(span => IsDescendantOf(span, chainSpanId, spans))
            && conversationIds.Length > 0
            && conversationIds.All(value => value!.StartsWith("hmac-", StringComparison.Ordinal));
    }

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
