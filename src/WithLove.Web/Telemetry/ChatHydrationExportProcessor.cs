using System.Diagnostics;
using OpenTelemetry;

namespace WithLove.Web.Telemetry;

/// <summary>
/// Prevents the expected Temporal chat-history probe from reaching trace exporters.
/// </summary>
/// <remarks>
/// Temporal records a missing workflow as an exception event whose message contains the workflow
/// ID. That outcome is expected during chat hydration, so exporting it is both noisy and contrary
/// to the trace privacy contract. This processor must be registered before export processors. It
/// leaves the configured sampler and every unrelated Activity unchanged.
/// </remarks>
internal sealed class ChatHydrationExportProcessor : BaseProcessor<Activity>
{
    internal const string HistoryQueryActivityName = "QueryWorkflow:GetHistory";

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.Equals(
            activity.OperationName,
            HistoryQueryActivityName,
            StringComparison.Ordinal))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
