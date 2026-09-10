using System.Diagnostics;

namespace WithLove.OpenInference.Spans;

/// <summary>
/// Starters that create typed OpenInference scopes from an application-owned
/// <see cref="ActivitySource"/>.
/// </summary>
/// <remarks>
/// <para>Starters never return <see langword="null"/>, even when the source has no listener. That
/// is the single largest source of null-conditional noise removed from calling code: the returned
/// scope is always usable, its verbs no-op for emission, and its
/// <see cref="OpenInferenceScope.TraceId"/> still reports the ambient trace.</para>
/// </remarks>
public static class OpenInferenceSpanExtensions
{
    /// <summary>Starts an OpenInference CHAIN scope.</summary>
    /// <remarks>
    /// When <paramref name="input"/> is supplied it is emitted as <c>input.value</c> with a derived
    /// <c>input.mime_type</c> of <c>text/plain</c>, before the span is created, so both are visible
    /// to a sampler and are subject to the resolved privacy configuration.
    /// </remarks>
    /// <param name="source">The application-owned activity source that creates the span. The caller retains ownership.</param>
    /// <param name="name">The operation name for the span.</param>
    /// <param name="input">The optional textual input of the operation.</param>
    /// <param name="traceConfig">
    /// Privacy configuration, resolved once here and retained by the scope. When omitted,
    /// <see cref="OpenInferenceTraceConfig.Default"/> is used.
    /// </param>
    /// <returns>A usable scope, including when <paramref name="source"/> has no listener.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or consists only of white-space characters.</exception>
    public static ChainScope StartChain(
        this ActivitySource source,
        string name,
        string? input = null,
        OpenInferenceTraceConfig? traceConfig = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Everything the span needs is validated and materialized before StartActivity is reached,
        // so a rejected call leaves no span behind to be half-applied - and so a sampler sees the
        // required attributes.
        var initialTags = TextInputTags(input);

        // Resolve once per scope so every attribute uses one immutable privacy decision.
        var config = traceConfig ?? OpenInferenceTraceConfig.Default;
        var activity = source.StartOpenInferenceActivity(
            name,
            OpenInferenceSpanKind.Chain,
            initialTags,
            config);
        return new ChainScope(activity, config);
    }

    /// <summary>Starts an OpenInference RETRIEVER scope.</summary>
    /// <remarks>
    /// When <paramref name="query"/> is supplied it is emitted as <c>input.value</c> with a derived
    /// <c>input.mime_type</c> of <c>text/plain</c>, before the span is created, so both are visible
    /// to a sampler and are subject to the resolved privacy configuration. The retrieved documents
    /// are supplied later, to <see cref="RetrieverScope.Record"/> or
    /// <see cref="RetrieverScope.Complete(IReadOnlyList{RetrievedDocument})"/>; they are a result
    /// rather than a sampling input, so they are carried by a verb and never by the start call.
    /// </remarks>
    /// <param name="source">The application-owned activity source that creates the span. The caller retains ownership.</param>
    /// <param name="name">The operation name for the span.</param>
    /// <param name="query">The optional textual query the retrieval was performed with.</param>
    /// <param name="traceConfig">
    /// Privacy configuration, resolved once here and retained by the scope. When omitted,
    /// <see cref="OpenInferenceTraceConfig.Default"/> is used.
    /// </param>
    /// <returns>A usable scope, including when <paramref name="source"/> has no listener.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or consists only of white-space characters.</exception>
    public static RetrieverScope StartRetriever(
        this ActivitySource source,
        string name,
        string? query = null,
        OpenInferenceTraceConfig? traceConfig = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var initialTags = TextInputTags(query);
        var config = traceConfig ?? OpenInferenceTraceConfig.Default;
        var activity = source.StartOpenInferenceActivity(
            name,
            OpenInferenceSpanKind.Retriever,
            initialTags,
            config);
        return new RetrieverScope(activity, config);
    }

    /// <summary>
    /// Builds the initial tags for a plain-text input, or none when there is no input.
    /// </summary>
    /// <remarks>
    /// Shared by every starter that takes optional text, so the input projection cannot drift
    /// between kinds: one place derives the MIME type, so one place is all that has to be right.
    /// </remarks>
    private static List<KeyValuePair<string, object?>>? TextInputTags(string? input) =>
        input is null
            ? null
            : [
                KeyValuePair.Create<string, object?>(OpenInferenceAttributes.InputValue, input),
                KeyValuePair.Create<string, object?>(
                    OpenInferenceAttributes.InputMimeType,
                    OpenInferenceMimeTypes.PlainText)
            ];
}
