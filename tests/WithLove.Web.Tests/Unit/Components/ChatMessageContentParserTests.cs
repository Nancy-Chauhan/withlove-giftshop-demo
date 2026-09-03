using FluentAssertions;
using WithLove.Web.Components.Shared;
using WithLove.Web.Models;

namespace WithLove.Web.Tests.Unit.Components;

public class ChatMessageContentParserTests
{
    [Fact]
    public void Parse_ProductBlock_ProjectsOnlyDisplayFields()
    {
        const string response = """
            Here are two lovely choices:
            - Velvet Crimson (ID 9) — $89
            Large, lush bouquet with dramatic red tones and premium blooms. Image:
            ![Velvet Crimson](https://lh3.googleusercontent.com/aida-public/abc123)
            """;

        var segments = ChatMessageContentParser.Parse(response);

        var productSegment = segments.Should().ContainSingle(
            segment => segment.Type == ChatMessageContentParser.SegmentType.Product).Which;
        productSegment.Product.Should().NotBeNull();
        var product = productSegment.Product!;
        product!.Name.Should().Be("Velvet Crimson");
        product.Price.Should().Be(89m);
        product.ImageUrl.Should().Be("https://lh3.googleusercontent.com/aida-public/abc123");
        product.Description.Should().Be(
            "Large, lush bouquet with dramatic red tones and premium blooms.");
        product.Name.Should().NotContain("ID");
        product.Description.Should().NotContain("Image:");
        segments.Select(segment => segment.Content).Should().NotContain(value =>
            value.Contains("ID", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Image:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("![", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_MultipleProductBlocks_CreatesOneCardPerProduct()
    {
        const string response = """
            - Velvet Crimson (ID 9) — $89
            Rich red blooms. Image: ![Velvet Crimson](https://images.test/velvet)
            - Blush Peony (ID 10) — $78
            Soft pink peonies. Image: ![Blush Peony](https://images.test/peony)
            """;

        var products = ChatMessageContentParser.Parse(response)
            .Where(segment => segment.Type == ChatMessageContentParser.SegmentType.Product)
            .Select(segment => segment.Product)
            .ToArray();

        products.Should().HaveCount(2);
        products.Select(product => product!.Name).Should().Equal("Velvet Crimson", "Blush Peony");
        products.Select(product => product!.Price).Should().Equal(89m, 78m);
        products.Should().OnlyContain(product => product!.Description.Length <= 180);
    }

    [Fact]
    public void Parse_ProductWithoutBullet_StillRemovesInternalId()
    {
        const string response = "Velvet Crimson (ID 9) — $89\nA signature red bouquet.";

        var product = ChatMessageContentParser.Parse(response)
            .Single(segment => segment.Type == ChatMessageContentParser.SegmentType.Product)
            .Product;

        product!.Name.Should().Be("Velvet Crimson");
        product.Description.Should().Be("A signature red bouquet.");
    }

    [Fact]
    public void Parse_ImageMarkdownWithoutFileExtension_RendersAsImageSegment()
    {
        const string response = "Take a look:\n![Bouquet](https://lh3.googleusercontent.com/aida-public/opaque-token)";

        var image = ChatMessageContentParser.Parse(response)
            .Should().ContainSingle(segment => segment.Type == ChatMessageContentParser.SegmentType.Image)
            .Which;

        image.Url.Should().Be("https://lh3.googleusercontent.com/aida-public/opaque-token");
        image.Alt.Should().Be("Bouquet");
    }

    [Fact]
    public void Canonicalize_ReplacesModelImageAndMetadataWithCatalogValues()
    {
        var parsed = new ChatMessageContentParser.ProductCard(
            "Velvet Crimson",
            1m,
            string.Empty,
            "https://model.example/wrong-image");
        var catalog = new Product
        {
            Name = "Velvet Crimson",
            Price = 89m,
            Description = "For moments that require no words.",
            ImageUrl = "https://catalog.example/velvet.jpg",
        };

        var product = ChatMessageContentParser.Canonicalize(parsed, catalog);

        product.Name.Should().Be("Velvet Crimson");
        product.Price.Should().Be(89m);
        product.Description.Should().Be("For moments that require no words.");
        product.ImageUrl.Should().Be("https://catalog.example/velvet.jpg");
    }

    [Fact]
    public void FindCatalogProduct_MatchesProductNameEmbeddedInModelLeadIn()
    {
        var parsed = new ChatMessageContentParser.ProductCard(
            "Oh, what a romantic pick! Velvet Crimson",
            89m,
            string.Empty,
            "https://model.example/wrong-image");
        var catalog = new[]
        {
            new Product { Name = "Velvet Crimson", Price = 89m },
            new Product { Name = "Crimson", Price = 50m },
        };

        var match = ChatMessageContentParser.FindCatalogProduct(parsed, catalog);

        match.Should().NotBeNull();
        match!.Name.Should().Be("Velvet Crimson");
    }
}
