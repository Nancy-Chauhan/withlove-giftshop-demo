using WithLove.Telemetry.Verifier;

if (args.Length is not 5 and not 6)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/WithLove.Telemetry.Verifier -- " +
        "<phoenix-url> <project-name> <operation-id> <raw-user-id> <raw-workflow-id> " +
        "[capture-ai-content=true|false]");
    return 2;
}

var phoenixUrl = args[0];
var projectName = args[1];
var operationId = args[2];
var rawUserId = args[3];
var rawWorkflowId = args[4];
var captureAiContent = true;
if (args.Length == 6 && !bool.TryParse(args[5], out captureAiContent))
{
    Console.Error.WriteLine("capture-ai-content must be true or false.");
    return 2;
}

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var verifier = new PhoenixChatTraceVerifier(client);
var result = await verifier.VerifyAsync(
    new Uri(phoenixUrl.EndsWith('/') ? phoenixUrl : phoenixUrl + '/'),
    projectName,
    operationId,
    [rawUserId, rawWorkflowId],
    captureAiContent
        ? PhoenixChatTraceExpectation.ProductSearch
        : PhoenixChatTraceExpectation.RedactedProductSearch);
Console.WriteLine($"Verified operation {operationId} in trace {result.TraceId} ({result.SpanCount} spans).");
return 0;
