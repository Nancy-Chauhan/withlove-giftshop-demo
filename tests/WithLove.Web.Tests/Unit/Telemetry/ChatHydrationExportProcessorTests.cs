using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using WithLove.Web.Telemetry;

namespace WithLove.Web.Tests.Unit.Telemetry;

public class ChatHydrationExportProcessorTests
{
    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void ExportPipeline_DropsOnlyGetHistoryAndPreservesConfiguredSampling()
    {
        using var source = new ActivitySource(nameof(ChatHydrationExportProcessorTests));
        var exporter = new CapturingActivityExporter();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new ChatHydrationExportProcessor())
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();

        using (source.StartActivity(ChatHydrationExportProcessor.HistoryQueryActivityName))
        {
        }
        using (source.StartActivity("UpdateWorkflow:RunTurn"))
        {
        }

        exporter.ExportedOperationNames.Should().Equal("UpdateWorkflow:RunTurn");
    }

    private sealed class CapturingActivityExporter : BaseExporter<Activity>
    {
        internal List<string> ExportedOperationNames { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                ExportedOperationNames.Add(activity.OperationName);
            return ExportResult.Success;
        }
    }
}
