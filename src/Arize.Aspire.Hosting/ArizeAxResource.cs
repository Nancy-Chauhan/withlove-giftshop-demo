namespace Aspire.Hosting.ApplicationModel;

/// <summary>Represents Arize AX as an external OTLP trace destination.</summary>
public sealed class ArizeAxResource : Resource
{
    /// <summary>Initializes a new external Arize AX resource.</summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="endpointParameter">The parameter containing the AX OTLP endpoint.</param>
    /// <param name="apiKeyParameter">The secret parameter containing the AX API key.</param>
    /// <param name="spaceIdParameter">The secret parameter containing the AX space identifier.</param>
    /// <param name="protocol">The OTLP transport expected by the configured endpoint.</param>
    public ArizeAxResource(
        string name,
        ParameterResource endpointParameter,
        ParameterResource apiKeyParameter,
        ParameterResource spaceIdParameter,
        ArizeOtlpProtocol protocol)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(endpointParameter);
        ArgumentNullException.ThrowIfNull(apiKeyParameter);
        ArgumentNullException.ThrowIfNull(spaceIdParameter);

        if (!Enum.IsDefined(protocol))
            throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown OTLP protocol.");

        EndpointParameter = endpointParameter;
        ApiKeyParameter = apiKeyParameter;
        SpaceIdParameter = spaceIdParameter;
        Protocol = protocol;
    }

    /// <summary>Gets the parameter containing the AX OTLP endpoint.</summary>
    public ParameterResource EndpointParameter { get; }

    /// <summary>Gets the secret parameter containing the AX API key.</summary>
    public ParameterResource ApiKeyParameter { get; }

    /// <summary>Gets the secret parameter containing the AX space identifier.</summary>
    public ParameterResource SpaceIdParameter { get; }

    /// <summary>Gets the OTLP transport expected by the AX endpoint.</summary>
    public ArizeOtlpProtocol Protocol { get; }
}
