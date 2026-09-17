using System.Diagnostics;
using OpenTelemetry;
using WithLove.OpenInference;

namespace WithLove.ServiceDefaults.Telemetry;

/// <summary>
/// Trims the Arize AX trace export to the agent's meaningful spans: the AI spans plus the
/// Temporal workflow/activity and HTTP spans that connect them, dropping only leaf plumbing
/// (database queries and Blazor SignalR traffic).
/// </summary>
/// <remarks>
/// Arize's LLM-observability views categorize spans by <c>openinference.span.kind</c> (CHAIN,
/// TOOL, RETRIEVER) and by OTel GenAI (<c>gen_ai.*</c>, for LLM/EMBEDDING). A naive "AI-spans-only"
/// filter is wrong: the LLM/TOOL/RETRIEVER spans are children of the Temporal activity spans and
/// cross-service HTTP spans, which carry neither convention. Dropping those parents leaves the AI
/// spans <b>orphaned</b> — their <c>parent_span_id</c> points at a span Arize never received — so
/// the trace tree shatters into a flat list.
///
/// This filter therefore drops only spans that are (a) not AI spans and (b) leaf infrastructure or
/// their own non-agent traces that never parent an AI span: database/EF queries (<c>db.system</c>),
/// Blazor SignalR traffic (<c>/_blazor</c>), storefront page loads, cloud managed-identity/OAuth
/// token fetches (credential-adjacent URLs that must not reach the AX project), the one-time
/// database-setup workflow, and the provider (OpenAI) HTTP call beneath an LLM span. Everything else
/// — AI spans, Temporal workflow/activity spans, and the cross-service ProductsAPI HTTP hops that
/// parent the RETRIEVER — is kept so the tree stays connected; removing those would orphan the AI
/// spans and requires reparenting instead. Clearing the Recorded flag removes a span from export,
/// exactly as <c>ChatHydrationExportProcessor</c> does in Web. Registered before the exporter, and
/// only on the Arize AX destination, so the Aspire dashboard and Phoenix paths keep full traces.
/// </remarks>
internal sealed class AgentTraceExportFilter(bool aiOnly = false) : BaseProcessor<Activity>
{
    private const string GenAiAttributePrefix = "gen_ai.";
    private const string BlazorPathSegment = "/_blazor";

    // When true, export AI spans only and drop every infra span — used together with
    // AiTraceReparentProcessor, which repoints the AI spans onto the root so they don't orphan.
    // When false (default), keep the connective Temporal/HTTP spans so the tree stays connected
    // without reparenting.
    private readonly bool aiOnly = aiOnly;

    // The one-time database-setup workflow and its activities run at startup, in their own traces,
    // and never parent an AI span. Unlike the chat workflow's activity spans — which connect the
    // AI spans and are kept — these are pure noise in the AX view. Matched on the operation name
    // after the Temporal prefix (e.g. "RunActivity:ApplyMigrations", "StartWorkflow:DatabaseSetupWorkflow").
    private static readonly HashSet<string> DatabaseSetupOperations = new(StringComparer.Ordinal)
    {
        "DatabaseSetupWorkflow",
        "ApplyMigrations",
        "ApplySchemaUpgrades",
        "SeedDatabase",
    };

    // OpenTelemetry HTTP-span URL attributes, newest semconv first. A span is matched if any is set.
    private static readonly string[] HttpUrlKeys = ["url.path", "http.route", "http.target", "url.full"];

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!ShouldExport(activity))
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }

    private bool ShouldExport(Activity activity)
    {
        // Always keep AI spans: app-owned OpenInference kinds (CHAIN/TOOL/RETRIEVER) and the
        // model/embedding spans emitted with OTel GenAI conventions.
        if (IsAiSpan(activity))
            return true;

        // AI-only mode: everything else goes. The reparent processor has already repointed the AI
        // spans onto the root, so dropping their infra parents no longer orphans them.
        if (aiOnly)
            return false;

        // Keep-connective mode: drop only the leaf plumbing that never parents an AI span; keep the
        // Temporal and HTTP spans so the trace tree stays connected without reparenting.
        return !IsDroppableNoise(activity);
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

    private static bool IsDroppableNoise(Activity activity)
    {
        // Database / EF Core query spans: leaves, and the bulk of the noise. Match both the current
        // OTel database semconv (db.system.name) and the older key (db.system).
        if (activity.GetTagItem("db.system.name") is not null
            || activity.GetTagItem("db.system") is not null)
            return true;

        // Startup database-setup workflow/activity spans (migrations, schema upgrades, seeding).
        if (DatabaseSetupOperations.Contains(LocalOperationName(activity.OperationName)))
            return true;

        // HTTP spans that are leaves or their own non-agent traces, matched by URL — dropping these
        // never orphans an AI span. Not matched (kept): the cross-service ProductsAPI calls, whose
        // paths are "/api/..."; they parent the RETRIEVER span, so removing them needs reparenting.
        foreach (var key in HttpUrlKeys)
        {
            if (activity.GetTagItem(key) is not string url)
                continue;

            if (url.Contains(BlazorPathSegment, StringComparison.Ordinal)              // Blazor SignalR
                || url.Equals("/", StringComparison.Ordinal)                           // storefront page load
                || url.Contains("/msi/token", StringComparison.OrdinalIgnoreCase)      // Azure managed identity
                || url.Contains("169.254.169.254", StringComparison.Ordinal)           // IMDS token endpoint
                || url.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase)        // OAuth token fetch
                || url.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)  // provider call under LLM
                || url.Contains("openai.azure.com", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string LocalOperationName(string operationName)
    {
        var separator = operationName.LastIndexOf(':');
        return separator >= 0 ? operationName[(separator + 1)..] : operationName;
    }
}
