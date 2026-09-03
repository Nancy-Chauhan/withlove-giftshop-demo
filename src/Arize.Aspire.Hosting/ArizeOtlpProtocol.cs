namespace Aspire.Hosting.ApplicationModel;

/// <summary>Identifies the OTLP transport used by an Arize AX endpoint.</summary>
public enum ArizeOtlpProtocol
{
    /// <summary>OTLP over gRPC.</summary>
    Grpc,

    /// <summary>OTLP over HTTP using protobuf payloads.</summary>
    HttpProtobuf,
}

internal static class ArizeOtlpProtocolExtensions
{
    internal static string ToConfigurationValue(this ArizeOtlpProtocol protocol) => protocol switch
    {
        ArizeOtlpProtocol.Grpc => "grpc",
        ArizeOtlpProtocol.HttpProtobuf => "http/protobuf",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown OTLP protocol."),
    };
}
