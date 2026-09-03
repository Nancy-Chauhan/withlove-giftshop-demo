# Telemetry architecture

WithLove uses two complementary semantic layers on one OpenTelemetry trace:

- The durable-chat integration owns provider/model spans and token usage; an app-local chat-client
  decorator adds the standard MEAI GenAI message attributes to that same span.
- `WithLove.OpenInference` owns application spans and context: `CHAIN` for a complete chat turn,
  `RETRIEVER` for an actual product-search backend retrieval, plus pseudonymous session/user IDs.

Both direct embedding pipelines (ProductsAPI query embeddings and WorkflowServer seed embeddings)
are wrapped with MEAI OpenTelemetry instrumentation. They use each service's already-registered
activity source and force sensitive-content capture off, so embedding operations are visible without
exporting input strings or vectors.

The application does not create another OpenInference or MEAI `LLM` span around a model call. The
decorator only enriches the durable package's existing model span, which keeps one model latency,
token, and cost record.

Chat message content is disabled when no capture setting is present. Setting
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` enables `input.value`/`output.value` on
the application-owned `chat.turn` CHAIN and `gen_ai.input.messages`,
`gen_ai.output.messages`, and `gen_ai.system_instructions` on the existing durable model span.
`OPENINFERENCE_HIDE_INPUTS` and `OPENINFERENCE_HIDE_OUTPUTS` override that opt-in per direction.
The per-turn `chat.operation_id` is carried onto both spans so AX can correlate them even though the
Temporal durable boundary does not preserve one physical trace tree for the entire workflow life.

## Destinations

| Environment | Traces | Metrics | Logs |
|---|---|---|---|
| Local AppHost with Phoenix | Arize Phoenix | Aspire dashboard | Aspire dashboard and console |
| Local AppHost with AX selected | Arize AX | Aspire dashboard | Aspire dashboard and console |
| Local process without an Arize backend | Aspire dashboard when its OTLP endpoint is present | Aspire dashboard | Aspire dashboard and console |
| Azure deployment | Arize AX by default | Aspire dashboard | Aspire dashboard, console, and the platform log pipeline |

The Aspire integration exposes two explicit backends:

- `AddArize` runs the open-source Phoenix container and gates consumers on `/readyz`.
- `AddArizeAx` models AX as an external parameter-backed resource. Consumers reference it but do
  not `WaitFor` it because AX is not an AppHost-managed process.

Phoenix uses the container filesystem by default and does not attach persistent storage. An
AppHost can explicitly opt in when persistence is wanted:

```csharp
var arize = builder.AddArize("arize")
    .WithVolume("arize-data", PhoenixResource.DataMountPath);
```

The AppHost reads `Arize:TraceDestination`. Local runs default to `Phoenix`; publish mode defaults
to `Ax`. To use AX locally, store the values in the AppHost's secret store and select it when the
AppHost starts:

```bash
aspire secret set ARIZE_OTLP_ENDPOINT "<endpoint-from-the-AX-connect-page>"
aspire secret set ARIZE_API_KEY "<your-AX-api-key>"
aspire secret set ARIZE_SPACE_ID "<your-AX-space-id>"
Arize__TraceDestination=Ax aspire start
```

No collector region is assumed. `ARIZE_OTLP_ENDPOINT` must be the endpoint supplied for the AX
space. OTLP/HTTP is the default protocol; an endpoint ending in `/v1` is normalized to the
signal-specific `/v1/traces` path.

ServiceDefaults never registers two trace exporters. Phoenix and AX are mutually exclusive. When
either Arize backend is referenced, it receives traces while `OTEL_EXPORTER_OTLP_ENDPOINT` still
carries logs and metrics to Aspire. Without an Arize backend, the Aspire endpoint receives all
three signals. With no endpoint, no telemetry is exported and a startup warning explains the
missing trace destination. AX authentication headers are configured only on the named AX trace
exporter, so they cannot leak to Aspire's logs or metrics exporters.

All three services use `openinference.project.name=withlove-giftshop`. Their distinct
`service.name` values remain intact for filtering inside that project.

## Trace privacy contract

No exported trace, whether routed to Phoenix or the Aspire dashboard, may contain the raw
authenticated-user claim ID or raw Temporal workflow ID. Logs are explicitly outside this
guarantee and require their own privacy audit.

The Web app derives versioned, domain-separated HMAC-SHA256 pseudonyms. The helper is owned by
`WithLove.Web`; it remains outside the reusable `WithLove.OpenInference` conventions project.

When the Web environment is Development, the application uses a committed demo-only key and the
`demo-v1` key version. This removes setup friction and prevents raw identifiers from appearing
directly in traces, but it is not production-grade pseudonymization: the key is public, so someone
with candidate identifiers can reproduce their HMAC values.

Published deployments require private configuration:

```text
TelemetryIdentity:Key        Base64 for at least 32 random bytes
TelemetryIdentity:KeyVersion v1
```

Every non-Development startup fails if either value is missing or malformed. There is no generated
fallback because a new key on every launch would silently destroy stable grouping. Only Web
receives the deployment secret.
WorkflowServer receives the safe session value as durable `ConversationId`; the raw workflow ID
is used only as the Temporal routing key. Temporal OpenTelemetry interceptors set
`TagNameWorkflowId=null` so they cannot emit `temporalWorkflowID`.

Rotating the deployment key intentionally starts new session/user groups. Change the key and key
version together. Never copy the committed local demo key into a deployment.

## Browser verification handoff

`TelemetryVerification:ExposeOperationId` is false by default and is wired only on local runs.
When explicitly enabled, the chat component clears the previous value before each turn and renders
the completed turn's operation ID in a hidden `data-operation-id` attribute. This is a correlation
identifier, not a user or workflow identity. It is not present in the Azure publish configuration.

After browser automation reads that value, verify the stored trace with:

```bash
dotnet run --project tools/WithLove.Telemetry.Verifier -- \
  http://localhost:<phoenix-port> withlove-giftshop <operation-id> <raw-user-id> <raw-workflow-id>
```

The verifier polls with both an attempt limit and a deadline. It first finds the one CHAIN using
`chat.operation_id`, extracts its trace ID, and keeps polling the trace query while Phoenix has only
partially ingested it. A successful result requires each MEAI LLM span to descend from the CHAIN,
at least one pseudonymous durable `conversation.id`, unique span IDs, pseudonymous CHAIN identity
attributes, and the absence of both raw identifiers and `temporalWorkflowID`.
