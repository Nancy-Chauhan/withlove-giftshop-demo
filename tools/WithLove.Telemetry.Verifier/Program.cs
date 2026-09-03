using WithLove.Telemetry.Verifier;

if (args is not [var phoenixUrl, var projectName, var operationId, var rawUserId, var rawWorkflowId])
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/WithLove.Telemetry.Verifier -- " +
        "<phoenix-url> <project-name> <operation-id> <raw-user-id> <raw-workflow-id>");
    return 2;
}

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var verifier = new PhoenixChatTraceVerifier(client);
var result = await verifier.VerifyAsync(
    new Uri(phoenixUrl.EndsWith('/') ? phoenixUrl : phoenixUrl + '/'),
    projectName,
    operationId,
    [rawUserId, rawWorkflowId]);
Console.WriteLine($"Verified operation {operationId} in trace {result.TraceId} ({result.SpanCount} spans).");
return 0;
