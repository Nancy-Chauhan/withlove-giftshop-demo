using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Aspire hosting extensions for Arize Phoenix and Arize AX trace destinations.</summary>
public static class ArizeResourceBuilderExtensions
{
    public const string PhoenixOtlpTracesEndpointEnvironmentVariable = "Phoenix__OtlpTracesEndpoint";
    public const string AxOtlpTracesEndpointEnvironmentVariable = "Arize__Tracing__Ax__Endpoint";
    public const string AxApiKeyEnvironmentVariable = "Arize__Tracing__Ax__ApiKey";
    public const string AxSpaceIdEnvironmentVariable = "Arize__Tracing__Ax__SpaceId";
    public const string AxProtocolEnvironmentVariable = "Arize__Tracing__Ax__Protocol";
    public const string DefaultImageTag = "19.18.0";

    /// <summary>
    /// Adds the open-source Arize Phoenix container. No storage volume is attached automatically;
    /// callers can opt into persistence with Aspire's <c>WithVolume</c> API and
    /// <see cref="PhoenixResource.DataMountPath"/>. Use <see cref="AddArizeAx"/> for Arize AX.
    /// </summary>
    public static IResourceBuilder<PhoenixResource> AddArize(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? imageTag = null,
        int? httpPort = null,
        int? grpcPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.AddResource(new PhoenixResource(name))
            .WithImage("arizephoenix/phoenix", imageTag ?? DefaultImageTag)
            .WithImageRegistry("docker.io")
            .WithHttpEndpoint(port: httpPort, targetPort: PhoenixResource.HttpTargetPort, name: PhoenixResource.HttpEndpointName)
            .WithEndpoint(port: grpcPort, targetPort: PhoenixResource.GrpcTargetPort, name: PhoenixResource.GrpcEndpointName)
            .WithEnvironment("PHOENIX_WORKING_DIR", PhoenixResource.DataMountPath)
            .WithHttpHealthCheck("/readyz", endpointName: PhoenixResource.HttpEndpointName)
            .WithUrlForEndpoint(PhoenixResource.HttpEndpointName, url => url.DisplayText = "Phoenix UI");
    }

    /// <summary>Adds Arize AX as an external, parameter-backed OTLP trace destination.</summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="endpointConfigurationKey">Configuration key containing the AX OTLP endpoint.</param>
    /// <param name="apiKeyConfigurationKey">Configuration key containing the AX API key.</param>
    /// <param name="spaceIdConfigurationKey">Configuration key containing the AX space identifier.</param>
    /// <param name="protocol">The OTLP transport expected by the configured endpoint.</param>
    /// <returns>The AX resource builder.</returns>
    /// <remarks>
    /// AX is external, so the resource is excluded from deployment manifests. Its parameter
    /// resources remain publishable inputs, and consuming applications receive deferred parameter
    /// references rather than credential values.
    /// </remarks>
    public static IResourceBuilder<ArizeAxResource> AddArizeAx(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string endpointConfigurationKey = "ARIZE_OTLP_ENDPOINT",
        string apiKeyConfigurationKey = "ARIZE_API_KEY",
        string spaceIdConfigurationKey = "ARIZE_SPACE_ID",
        ArizeOtlpProtocol protocol = ArizeOtlpProtocol.HttpProtobuf)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointConfigurationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyConfigurationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceIdConfigurationKey);

        if (!Enum.IsDefined(protocol))
            throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown OTLP protocol.");

        var endpoint = builder.AddParameterFromConfiguration($"{name}-otlp-endpoint", endpointConfigurationKey)
            .WithDescription("Arize AX OTLP trace endpoint.");
        var apiKey = builder.AddParameterFromConfiguration($"{name}-api-key", apiKeyConfigurationKey, secret: true)
            .WithDescription("Arize AX API key.");
        var spaceId = builder.AddParameterFromConfiguration($"{name}-space-id", spaceIdConfigurationKey, secret: true)
            .WithDescription("Arize AX space identifier.");

        return builder.AddResource(new ArizeAxResource(
                name,
                endpoint.Resource,
                apiKey.Resource,
                spaceId.Resource,
                protocol))
            .ExcludeFromManifest();
    }

    /// <summary>Injects only the Phoenix OTLP/HTTP trace endpoint into a consuming resource.</summary>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<PhoenixResource> source)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        return builder.WithEnvironment(
            PhoenixOtlpTracesEndpointEnvironmentVariable,
            ReferenceExpression.Create($"{source.Resource.HttpEndpoint.Property(EndpointProperty.Url)}/v1/traces"));
    }

    /// <summary>Configures a destination resource to export traces to Arize AX.</summary>
    /// <typeparam name="TDestination">The destination resource type.</typeparam>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="source">The AX resource builder.</param>
    /// <returns>The destination resource builder.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<ArizeAxResource> source)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        return builder
            .WithEnvironment(AxOtlpTracesEndpointEnvironmentVariable, source.Resource.EndpointParameter)
            .WithEnvironment(AxApiKeyEnvironmentVariable, source.Resource.ApiKeyParameter)
            .WithEnvironment(AxSpaceIdEnvironmentVariable, source.Resource.SpaceIdParameter)
            .WithEnvironment(AxProtocolEnvironmentVariable, source.Resource.Protocol.ToConfigurationValue())
            .WithReferenceRelationship(source);
    }
}
