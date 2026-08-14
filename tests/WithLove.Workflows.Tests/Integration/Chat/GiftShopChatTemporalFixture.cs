using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.Hosting;

namespace WithLove.Workflows.Tests.Integration.Chat;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GiftShopChatTemporalCollection : ICollectionFixture<GiftShopChatTemporalFixture>
{
    public const string Name = "GiftShop durable chat";
}

public sealed class GiftShopChatTemporalFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await WorkflowEnvironment.StartLocalAsync(new()
        {
            DevServerOptions = new() { DownloadVersion = "v1.7.2" },
        });
        Environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;
    }

    public async Task DisposeAsync() => await Environment.DisposeAsync();
}

internal sealed class GiftShopChatWorkerHarness : IAsyncDisposable
{
    private readonly IHost _host;

    private GiftShopChatWorkerHarness(
        IHost host,
        string taskQueue,
        DurableChatWorkflowInput input)
    {
        _host = host;
        TaskQueue = taskQueue;
        WorkflowInput = input;
    }

    public string TaskQueue { get; }
    public DurableChatWorkflowInput WorkflowInput { get; }

    public static async Task<GiftShopChatWorkerHarness> StartAsync(
        WorkflowEnvironment environment,
        ScriptedGiftShopChatClient chatClient,
        Func<DurableChatWorkflowInput, DurableChatWorkflowInput>? transformInput = null)
    {
        var targetHost = environment.Client.Connection.Options.TargetHost
            ?? throw new InvalidOperationException("Temporal target host is unavailable.");
        var taskQueue = $"giftshop-chat-test-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging();
        builder.Services.AddChatClient(chatClient).Build();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new NoopEmbeddingGenerator());
        builder.Services.AddHttpClient("productsApi", client =>
                client.BaseAddress = new Uri("http://products.test"))
            .ConfigurePrimaryHttpMessageHandler(() => new GiftShopProductsHandler());

        var worker = builder.Services
            .AddHostedTemporalWorker(
                targetHost,
                environment.Client.Options.Namespace,
                taskQueue)
            .AddGiftShopChatWorker()
            .AddWorkflow<GiftShopSharedWorkerStatusWorkflow>()
            .AddWorkflow<LoyaltyAccountWorkflow>();

        var host = builder.Build();
        await host.StartAsync();

        var clientServices = new ServiceCollection();
        clientServices.AddLogging();
        clientServices.AddDurableChatWorkflowInputFactory(
            taskQueue,
            GiftShopChatRegistrationExtensions.ConfigureDurableExecution);
        foreach (var declaration in GiftShopChatToolCatalog.CreateDeclarations())
            clientServices.AddDurableToolDeclaration(declaration);
        await using var provider = clientServices.BuildServiceProvider();
        var input = provider.GetRequiredService<IDurableChatWorkflowInputFactory>().Create();
        input = transformInput?.Invoke(input) ?? input;

        return new GiftShopChatWorkerHarness(host, taskQueue, input);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

internal sealed class ScriptedGiftShopChatClient : IChatClient
{
    private readonly Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, Task<ChatResponse>> _next;
    private int _callCount;

    public ScriptedGiftShopChatClient(
        Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> next)
        : this((call, messages, options) => Task.FromResult(next(call, messages, options)))
    {
    }

    public ScriptedGiftShopChatClient(
        Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, Task<ChatResponse>> next) =>
        _next = next;

    public int CallCount => Volatile.Read(ref _callCount);
    public ConcurrentQueue<IReadOnlyList<ChatMessage>> Requests { get; } = new();
    public ChatClientMetadata Metadata { get; } = new("giftshop-scripted");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages.ToList();
        Requests.Enqueue(materialized);
        var call = Interlocked.Increment(ref _callCount);
        return await _next(call, materialized, options);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class NoopEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("noop", null, null, 1);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(_ => new Embedding<float>(new[] { 0f })).ToList()));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class GiftShopProductsHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;
        var json = path switch
        {
            "/api/products/7" => ProductJson,
            var value when value.StartsWith("/api/products/search", StringComparison.Ordinal) =>
                $$"""{"value":[{{ProductJson}}]}""",
            "/api/categories" => """{"value":[{"id":3,"name":"Comfort","description":"Warm gifts"}]}""",
            "/api/products/category/3" => $$"""{"value":[{{ProductJson}}]}""",
            _ => string.Empty,
        };

        var response = new HttpResponseMessage(
            string.IsNullOrEmpty(json) ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }

    private const string ProductJson =
        """
        {"id":7,"name":"Keepsake Box","price":25.00,"categoryName":"Comfort","subCategory":"Keepsakes","description":"A handcrafted box.","imageUrl":"/images/7.jpg","stripePriceId":"price_7","materials":[{"name":"Wood"}],"storyTitle":"Made with care"}
        """;
}
