using System.Net;
using System.Text;
using FakeItEasy;
using WithLove.Workflows.Activities;

namespace WithLove.Workflows.Tests.Unit.Chat;

public class GiftShopChatToolServiceTests
{
    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public async Task SearchProducts_ReturnsOnlyTopFourMatches()
    {
        var handler = new CapturingHandler();
        var httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient("productsApi"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://products.test") });
        var service = new GiftShopChatToolService(httpClientFactory);

        var result = await service.SearchProductsAsync("keepsake", CancellationToken.None);

        handler.PathAndQuery.Should().Be("/api/products/search?q=keepsake&top=4");
        result.Split('\n').Should().HaveCount(GiftShopChatToolService.MaxSearchProductMatches);
        result.Should().Contain("Product 4");
        result.Should().NotContain("Product 5");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? PathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PathAndQuery = request.RequestUri?.PathAndQuery;
            var products = string.Join(
                ",",
                Enumerable.Range(1, 5).Select(id =>
                    $$"""{"id":{{id}},"name":"Product {{id}}","price":{{id}}.00}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"value":[{{products}}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
