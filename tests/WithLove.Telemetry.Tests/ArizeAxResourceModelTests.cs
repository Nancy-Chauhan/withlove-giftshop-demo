using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace WithLove.Telemetry.Tests;

public class ArizeAxResourceModelTests
{
    [Fact]
    public void AddArizeAx_CreatesExternalResourceWithDeferredSecretParameters()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        builder.AddArizeAx("arize-ax", protocol: ArizeOtlpProtocol.Grpc);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.OfType<ArizeAxResource>().Should().ContainSingle().Subject;

        resource.Should().NotBeOfType<ContainerResource>();
        resource.Protocol.Should().Be(ArizeOtlpProtocol.Grpc);
        resource.EndpointParameter.Name.Should().Be("arize-ax-otlp-endpoint");
        resource.EndpointParameter.Secret.Should().BeFalse();
        resource.ApiKeyParameter.Name.Should().Be("arize-ax-api-key");
        resource.ApiKeyParameter.Secret.Should().BeTrue();
        resource.SpaceIdParameter.Name.Should().Be("arize-ax-space-id");
        resource.SpaceIdParameter.Secret.Should().BeTrue();
        resource.Annotations.Should().Contain(annotation => annotation is ManifestPublishingCallbackAnnotation);
    }

    [Theory]
    [InlineData(ArizeOtlpProtocol.HttpProtobuf, "http/protobuf")]
    [InlineData(ArizeOtlpProtocol.Grpc, "grpc")]
    public async Task WithReference_AxInjectsOnlyNamespacedDeferredTraceConfiguration(
        ArizeOtlpProtocol protocol,
        string expectedProtocol)
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var ax = builder.AddArizeAx("arize-ax", protocol: protocol);
        builder.AddContainer("api", "example/api").WithReference(ax);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var api = model.Resources.Single(resource => resource.Name == "api");
        var environment = await ResolveEnvironmentAsync(api, builder.ExecutionContext);

        AssertParameterExpression(environment, "Arize__Tracing__Ax__Endpoint", "arize-ax-otlp-endpoint");
        AssertParameterExpression(environment, "Arize__Tracing__Ax__ApiKey", "arize-ax-api-key");
        AssertParameterExpression(environment, "Arize__Tracing__Ax__SpaceId", "arize-ax-space-id");
        environment["Arize__Tracing__Ax__Protocol"].Should().Be(expectedProtocol);
        environment.Keys.Should().NotContain("OTEL_EXPORTER_OTLP_ENDPOINT");
        environment.Keys.Should().NotContain("OTEL_EXPORTER_OTLP_HEADERS");
        environment.Keys.Should().NotContain("OTEL_EXPORTER_OTLP_TRACES_HEADERS");
        api.Annotations.OfType<ResourceRelationshipAnnotation>().Should().ContainSingle(relationship =>
            ReferenceEquals(relationship.Resource, ax.Resource)
            && relationship.Type == "Reference");
    }

    private static void AssertParameterExpression(
        IReadOnlyDictionary<string, object> environment,
        string key,
        string parameterName) =>
        environment[key].Should().BeAssignableTo<IManifestExpressionProvider>()
            .Which.ValueExpression.Should().Be($"{{{parameterName}.value}}");

    private static async Task<Dictionary<string, object>> ResolveEnvironmentAsync(
        IResource resource,
        DistributedApplicationExecutionContext executionContext)
    {
        resource.TryGetEnvironmentVariables(out var callbacks).Should().BeTrue();
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(executionContext, resource, environment);
        foreach (var callback in callbacks!) await callback.Callback(context);
        return environment;
    }
}
