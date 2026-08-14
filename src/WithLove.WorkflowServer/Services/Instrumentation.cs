using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Trace;
using TemporalCommunity.Extensions.AI;

namespace WithLove.WorkflowServer.Services;

public class Instrumentation : IDisposable
{
    internal const string ActivitySourceName = "workflowServer";
    internal const string ActivitySourceVersion = "1.0.0";
    public ActivitySource ActivitySource { get; } = new(ActivitySourceName, ActivitySourceVersion);

    public Meter Meter { get; } = new(ActivitySourceName, ActivitySourceVersion);

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}

public static class WorkflowServerTracingExtensions
{
    public static TracerProviderBuilder AddWorkflowServerTracingSources(
        this TracerProviderBuilder builder) =>
        builder.AddSource(
            Instrumentation.ActivitySourceName,
            DurableChatTelemetry.ActivitySourceName);
}
