using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace WithLove.Telemetry.Tests;

public class ArizeResourceModelTests
{
    [Fact]
    public async Task AddArize_ConfiguresPinnedPhoenixEndpointsWorkingDirectoryAndReadinessWithoutStorage()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        builder.AddArize("arize");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.OfType<PhoenixResource>().Should().ContainSingle().Subject;
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().Should().ContainSingle().Subject;
        image.Image.Should().Be("arizephoenix/phoenix");
        image.Tag.Should().Be("20.10.0");
        resource.Annotations.OfType<EndpointAnnotation>().Should().Contain(endpoint =>
            endpoint.Name == "http" && endpoint.TargetPort == 6006);
        resource.Annotations.OfType<EndpointAnnotation>().Should().Contain(endpoint =>
            endpoint.Name == "grpc" && endpoint.TargetPort == 4317);
        resource.Annotations.OfType<ContainerMountAnnotation>().Should().BeEmpty();
        resource.Annotations.OfType<HealthCheckAnnotation>().Should().ContainSingle(check =>
            check.Key.Contains("/readyz", StringComparison.Ordinal));
        resource.TryGetEnvironmentVariables(out var callbacks).Should().BeTrue();
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, environment);
        foreach (var callback in callbacks!) await callback.Callback(context);
        environment["PHOENIX_WORKING_DIR"].Should().Be("/mnt/data");
    }

    [Fact]
    public void WithVolume_OptsIntoPhoenixPersistenceAtTheDocumentedDataPath()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        builder.AddArize("arize")
            .WithVolume("phoenix-data", PhoenixResource.DataMountPath);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.OfType<PhoenixResource>().Should().ContainSingle().Subject;

        resource.Annotations.OfType<ContainerMountAnnotation>().Should().ContainSingle(mount =>
            mount.Source == "phoenix-data"
            && mount.Target == PhoenixResource.DataMountPath
            && mount.Type == ContainerMountType.Volume);
    }

    [Fact]
    public async Task WithReference_InjectsOnlyPhoenixTraceEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var arize = builder.AddArize("arize");
        builder.AddContainer("api", "example/api").WithReference(arize);
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var api = model.Resources.Single(resource => resource.Name == "api");
        api.TryGetEnvironmentVariables(out var callbacks).Should().BeTrue();
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, environment);
        foreach (var callback in callbacks!) await callback.Callback(context);

        environment.Keys.Should().ContainSingle(key => key == "Phoenix__OtlpTracesEndpoint");
        environment.Keys.Should().NotContain("OTEL_EXPORTER_OTLP_ENDPOINT");
        environment["Phoenix__OtlpTracesEndpoint"].Should().BeOfType<ReferenceExpression>()
            .Which.ValueExpression.Should().Be("{arize.bindings.http.url}/v1/traces");
        var arizeResource = model.Resources.OfType<PhoenixResource>().Should().ContainSingle().Subject;
        api.Annotations.OfType<ResourceRelationshipAnnotation>().Should().ContainSingle(relationship =>
            ReferenceEquals(relationship.Resource, arizeResource)
            && relationship.Type == "Reference");
    }
}
