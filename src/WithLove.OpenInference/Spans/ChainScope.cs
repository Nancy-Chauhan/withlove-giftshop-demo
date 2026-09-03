using System.Diagnostics;

namespace WithLove.OpenInference.Spans;

/// <summary>
/// A typed scope over an OpenInference CHAIN span.
/// </summary>
/// <remarks>
/// A chain has no kind-specific result model, so it carries the base verbs only: the
/// <see cref="OpenInferenceScope.Complete(string)"/> and
/// <see cref="OpenInferenceScope.Fail"/>, and <see cref="OpenInferenceScope.Dispose"/>. Obtain one
/// from <see cref="OpenInferenceSpanExtensions.StartChain"/>.
/// </remarks>
public sealed class ChainScope : OpenInferenceScope
{
    internal ChainScope(Activity? activity, OpenInferenceTraceConfig traceConfig)
        : base(activity, traceConfig)
    {
    }
}
