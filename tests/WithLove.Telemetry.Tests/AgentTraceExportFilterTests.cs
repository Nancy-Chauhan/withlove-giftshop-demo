using System.Diagnostics;
using WithLove.ServiceDefaults.Telemetry;

namespace WithLove.Telemetry.Tests;

public class AgentTraceExportFilterTests
{
    [Fact]
    public void KeepsAiSpans()
    {
        Kept("chat.turn", Tag("openinference.span.kind", "CHAIN")).Should().BeTrue();
        Kept("chat gpt-5-nano", Tag("gen_ai.operation.name", "chat")).Should().BeTrue();
    }

    [Fact]
    public void KeepsConnectiveSpansThatParentAiSpans()
    {
        // The Temporal chat-activity spans and the cross-service ProductsAPI hop parent the AI
        // spans, so they must survive or the RETRIEVER/LLM spans orphan.
        Kept("RunActivity:TemporalCommunity.Extensions.AI.GetChatStep").Should().BeTrue();
        Kept("GET", Tag("url.full", "https://productsapi.internal.example/api/products/search?q=x"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("db.system.name", "postgresql")]
    [InlineData("db.system", "postgresql")]
    public void DropsDatabaseSpans(string key, string value) =>
        Kept("query", Tag(key, value)).Should().BeFalse();

    [Theory]
    [InlineData("url.path", "/_blazor")]
    [InlineData("url.path", "/")]
    [InlineData("url.full", "http://localhost:12356/msi/token?resource=x")]
    [InlineData("url.full", "http://169.254.169.254/metadata/identity/oauth2/token")]
    [InlineData("url.full", "https://api.openai.com/v1/chat/completions")]
    [InlineData("url.full", "https://res.openai.azure.com/openai/deployments/x/chat/completions")]
    public void DropsLeafAndNonAgentHttpSpans(string key, string value) =>
        Kept("GET", Tag(key, value)).Should().BeFalse();

    [Theory]
    [InlineData("StartWorkflow:DatabaseSetupWorkflow")]
    [InlineData("RunActivity:ApplyMigrations")]
    [InlineData("StartActivity:SeedDatabase")]
    [InlineData("RunActivity:ApplySchemaUpgrades")]
    public void DropsStartupDatabaseSetupSpans(string name) =>
        Kept(name).Should().BeFalse();

    [Fact]
    public void AiOnlyMode_KeepsAiSpansAndDropsEverythingElse()
    {
        // With the reparent processor repointing AI spans onto the root, the connective Temporal and
        // HTTP spans can be dropped too — so AI-only mode keeps only the AI spans.
        KeptAiOnly("chat.turn", Tag("openinference.span.kind", "CHAIN")).Should().BeTrue();
        KeptAiOnly("chat", Tag("gen_ai.operation.name", "chat")).Should().BeTrue();
        KeptAiOnly("RunActivity:TemporalCommunity.Extensions.AI.GetChatStep").Should().BeFalse();
        KeptAiOnly("GET", Tag("url.full", "https://productsapi.internal.example/api/products/search"))
            .Should().BeFalse();
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private static bool Kept(string name, params KeyValuePair<string, object?>[] tags) =>
        RunFilter(new AgentTraceExportFilter(), name, tags);

    private static bool KeptAiOnly(string name, params KeyValuePair<string, object?>[] tags) =>
        RunFilter(new AgentTraceExportFilter(aiOnly: true), name, tags);

    private static bool RunFilter(
        AgentTraceExportFilter filter,
        string name,
        KeyValuePair<string, object?>[] tags)
    {
        using var source = new ActivitySource("withlove-test-agent-filter");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity(
            name, ActivityKind.Internal, default(ActivityContext), tags);
        activity.Should().NotBeNull();

        filter.OnEnd(activity!);
        return activity!.Recorded;
    }
}
