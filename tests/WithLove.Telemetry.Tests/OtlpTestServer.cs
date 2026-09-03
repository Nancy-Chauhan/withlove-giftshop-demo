using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WithLove.Telemetry.Tests;

internal sealed class OtlpTestServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<OtlpRequest> _requests;

    private OtlpTestServer(WebApplication application, Uri baseUri, ConcurrentQueue<OtlpRequest> requests)
    {
        _application = application;
        BaseUri = baseUri;
        _requests = requests;
    }

    internal Uri BaseUri { get; }
    internal IReadOnlyCollection<OtlpRequest> Requests => _requests;

    internal static async Task<OtlpTestServer> StartAsync()
    {
        var requests = new ConcurrentQueue<OtlpRequest>();
        var builder = WebApplication.CreateSlimBuilder();
        // This receiver is intentionally isolated from application configuration copied through
        // project references. In particular, Web's production Kestrel endpoint must not add a
        // second listener alongside the ephemeral test port below.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var application = builder.Build();
        application.MapPost("/{**path}", async context =>
        {
            await using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            var headers = context.Request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
            requests.Enqueue(new OtlpRequest(context.Request.Path.Value ?? string.Empty, buffer.ToArray(), headers));
            context.Response.StatusCode = StatusCodes.Status200OK;
        });
        await application.StartAsync();
        var address = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new OtlpTestServer(application, new Uri(address), requests);
    }

    internal async Task<OtlpRequest> WaitForAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var match = _requests.LastOrDefault(request => request.Path == path);
            if (match is not null) return match;
            try { await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
        throw new TimeoutException($"No OTLP request for {path}. Actual: {string.Join(", ", _requests.Select(r => r.Path))}");
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}

internal sealed record OtlpRequest(
    string Path,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);
