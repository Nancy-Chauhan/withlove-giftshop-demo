using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using WithLove.OpenInference;

namespace WithLove.ServiceDefaults.Telemetry;

/// <summary>
/// Reparents the agent's AI spans onto the trace's root AI span so an AI-only export renders as a
/// connected tree instead of a flat list of orphans. Pairs with <see cref="AgentTraceExportFilter"/>
/// running in AI-only mode.
/// </summary>
/// <remarks>
/// In this app the AI spans (CHAIN/TOOL/RETRIEVER plus the OTel-GenAI LLM/EMBEDDING spans) are
/// children of Temporal activity and cross-service HTTP spans, spread across three processes. When
/// those infra spans are dropped from the AX export, the AI spans lose their parents, and Arize —
/// which does not reparent, it only surfaces orphans — renders each as its own top-level node.
///
/// The root AI span (<c>chat.turn</c>) publishes its span id into OpenTelemetry Baggage
/// (<c>arize.reparent.span_id</c>) at start. Baggage already propagates across the HTTP hops and the
/// Temporal worker boundary, so every process sees the same root id. At end, each AI span whose
/// in-process parent is not itself an AI span is repointed at that root; spans already nested under
/// an AI parent (e.g. EMBEDDING under RETRIEVER) are left alone, so real AI-to-AI nesting survives.
/// The result is one <c>chat.turn</c> root with the model/tool/retriever spans beneath it.
///
/// Reparenting rewrites <see cref="Activity.ParentSpanId"/>, which has no public setter, so it is
/// done by reflection over the runtime's private fields, guarded so a field-name change on a future
/// runtime degrades to a no-op (the span keeps its original parent) rather than throwing.
/// </remarks>
internal sealed class AiTraceReparentProcessor : BaseProcessor<Activity>
{
    internal const string ReparentBaggageKey = "arize.reparent.span_id";
    private const string GenAiAttributePrefix = "gen_ai.";

    // On .NET, Activity.ParentSpanId is derived from this private string field (a 16-char hex span
    // id): when it is non-null the getter returns `new ActivitySpanId(_parentSpanId)` directly.
    private static readonly FieldInfo? ParentSpanIdField =
        typeof(Activity).GetField("_parentSpanId", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <inheritdoc />
    public override void OnStart(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        // The first OpenInference-kinded span in the trace is the root (chat.turn). It publishes its
        // id as the reparent anchor exactly once; every later AI span, in any process, reads it.
        if (activity.GetTagItem(OpenInferenceAttributes.OpenInferenceSpanKind) is null)
            return;
        if (!string.IsNullOrEmpty(Baggage.GetBaggage(ReparentBaggageKey)))
            return;

        Baggage.SetBaggage(ReparentBaggageKey, activity.SpanId.ToHexString());
    }

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (!IsAiSpan(activity))
            return;

        var rootHex = Baggage.GetBaggage(ReparentBaggageKey);
        if (string.IsNullOrEmpty(rootHex))
            return;

        var root = ActivitySpanId.CreateFromString(rootHex.AsSpan());
        if (activity.SpanId == root)            // the root anchors itself
            return;
        if (activity.ParentSpanId == root)      // already correct
            return;
        if (ParentIsAiSpan(activity))           // keep genuine AI-to-AI nesting (e.g. EMBEDDING → RETRIEVER)
            return;

        TrySetParentSpanId(activity, root);
    }

    private static bool IsAiSpan(Activity activity)
    {
        if (activity.GetTagItem(OpenInferenceAttributes.OpenInferenceSpanKind) is not null)
            return true;

        foreach (var tag in activity.EnumerateTagObjects())
        {
            if (tag.Key.StartsWith(GenAiAttributePrefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ParentIsAiSpan(Activity activity) =>
        activity.Parent is { } parent && IsAiSpan(parent);

    private static void TrySetParentSpanId(Activity activity, ActivitySpanId parent)
    {
        // Arize reads OTLP parent_span_id from Activity.ParentSpanId, whose getter returns the private
        // string field _parentSpanId (hex) when it is non-null. Overwriting it repoints the exported
        // parent. Guarded by field name and type so a runtime change degrades to a no-op.
        if (ParentSpanIdField?.FieldType != typeof(string))
            return;

        try
        {
            ParentSpanIdField.SetValue(activity, parent.ToHexString());
        }
        catch (Exception)
        {
            // Runtime internals changed; leave the original parent rather than crash ingestion.
        }
    }
}
