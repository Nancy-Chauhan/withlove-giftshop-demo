namespace Aspire.Hosting.ApplicationModel;

/// <summary>Represents the locally hosted Arize Phoenix container.</summary>
public sealed class PhoenixResource(string name) : ContainerResource(name)
{
    public const string HttpEndpointName = "http";
    public const string GrpcEndpointName = "grpc";
    public const int HttpTargetPort = 6006;
    public const int GrpcTargetPort = 4317;
    public const string DataMountPath = "/mnt/data";

    private EndpointReference? _httpEndpoint;
    private EndpointReference? _grpcEndpoint;

    public EndpointReference HttpEndpoint => _httpEndpoint ??= new(this, HttpEndpointName);
    public EndpointReference GrpcEndpoint => _grpcEndpoint ??= new(this, GrpcEndpointName);
}
