using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using WithLove.WorkflowServer.Services;

namespace WithLove.Workflows.Tests.Integration.Chat;

[Collection(GiftShopChatTemporalCollection.Name)]
public class MixedWorkflowSerializationIntegrationTests(GiftShopChatTemporalFixture fixture)
{
    [Fact]
    [Trait(TestTraits.Category, TestTraits.Integration)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SharedAiWorker_NonAiNestedResult_RoundTripsThroughDatabaseSetupClient()
    {
        var chatClient = new ScriptedGiftShopChatClient((_, _, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        await using var harness = await GiftShopChatWorkerHarness.StartAsync(
            fixture.Environment,
            chatClient);
        var targetHost = fixture.Environment.Client.Connection.Options.TargetHost
            ?? throw new InvalidOperationException("Temporal target host is unavailable.");
        var connectOptions = DatabaseSetupHostedService.ConfigureClientConnectOptions(
            new TemporalClientConnectOptions(targetHost)
            {
                Namespace = fixture.Environment.Client.Options.Namespace,
            });
        var caller = await TemporalClient.ConnectAsync(connectOptions);
        var handle = await caller.StartWorkflowAsync(
            (GiftShopSharedWorkerStatusWorkflow workflow) => workflow.RunAsync(),
            new WorkflowOptions(
                $"giftshop-mixed-converter-{Guid.NewGuid():N}",
                harness.TaskQueue));

        var result = await handle.GetResultAsync<GiftShopSharedWorkerResult>()
            .WaitAsync(TimeSpan.FromSeconds(15));

        result.Migration.Should().NotBeNull();
        result.Migration.AppliedCount.Should().Be(1);
        result.Migration.Message.Should().Be("schema-created");
        result.Seed.Should().NotBeNull();
        result.Seed.CategoriesSeeded.Should().Be(6);
        result.Seed.ProductsSeeded.Should().Be(20);
        result.Embedding.Should().NotBeNull();
        result.Embedding!.ProductsEmbedded.Should().Be(20);
    }
}

[Workflow("WithLove.Tests.GiftShopSharedWorkerStatusWorkflow")]
public sealed class GiftShopSharedWorkerStatusWorkflow
{
    [WorkflowRun]
    public Task<GiftShopSharedWorkerResult> RunAsync() => Task.FromResult(
        new GiftShopSharedWorkerResult(
            new GiftShopSharedWorkerMigrationResult(1, "schema-created"),
            new GiftShopSharedWorkerSeedResult(6, 20),
            new GiftShopSharedWorkerEmbeddingResult(20)));
}

public sealed record GiftShopSharedWorkerMigrationResult(int AppliedCount, string Message);

public sealed record GiftShopSharedWorkerSeedResult(int CategoriesSeeded, int ProductsSeeded);

public sealed record GiftShopSharedWorkerEmbeddingResult(int ProductsEmbedded);

public sealed record GiftShopSharedWorkerResult(
    GiftShopSharedWorkerMigrationResult Migration,
    GiftShopSharedWorkerSeedResult Seed,
    GiftShopSharedWorkerEmbeddingResult? Embedding);
