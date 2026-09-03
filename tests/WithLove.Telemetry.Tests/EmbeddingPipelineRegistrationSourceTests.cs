namespace WithLove.Telemetry.Tests;

public class EmbeddingPipelineRegistrationSourceTests
{
    public static TheoryData<string> DirectEmbeddingPrograms => new()
    {
        "src/WithLove.ProductsAPI/Program.cs",
        "src/WithLove.WorkflowServer/Program.cs",
    };

    [Theory]
    [MemberData(nameof(DirectEmbeddingPrograms))]
    public void DirectEmbeddingRegistration_UsesTheContentSafeMeaiTelemetryWrapper(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));

        source.Should().Contain(
            ".AsIEmbeddingGenerator()\n"
            + "        .WithOpenTelemetryInstrumentation(Instrumentation.ActivitySourceName)");
        source.Split(".WithOpenTelemetryInstrumentation(Instrumentation.ActivitySourceName)")
            .Should().HaveCount(2, "each direct embedding pipeline should be wrapped exactly once");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WithLoveShop.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate WithLoveShop.slnx.");
    }
}
