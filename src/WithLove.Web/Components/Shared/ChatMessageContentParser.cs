using System.Globalization;
using System.Text.RegularExpressions;
using WithLove.Web.Models;

namespace WithLove.Web.Components.Shared;

/// <summary>
/// Projects assistant product prose into display-safe chat segments.
/// </summary>
/// <remarks>
/// Product tools return IDs and other protocol fields so the model can perform follow-up calls.
/// Those fields are deliberately consumed here rather than rendered as chat markup. The parser is
/// intentionally small and tolerant of the model's usual bullet/price/image formatting because the
/// chat transcript is model-authored text, not an API response.
/// </remarks>
public static partial class ChatMessageContentParser
{
    private const int MaxDescriptionLength = 180;

    public enum SegmentType
    {
        Text,
        Image,
        Product,
    }

    public sealed record ProductCard(
        string Name,
        decimal Price,
        string Description,
        string? ImageUrl);

    public sealed record Segment(
        SegmentType Type,
        string Content,
        string? Url = null,
        string? Alt = null,
        ProductCard? Product = null);

    /// <summary>Replaces model-authored product metadata with the canonical catalog values.</summary>
    /// <remarks>
    /// The model's image URL is presentation input and may be truncated or mistyped. Matching the
    /// product name against the catalog lets the browser load the URL owned by the shop instead of
    /// trusting protocol text from the assistant response.
    /// </remarks>
    public static ProductCard Canonicalize(ProductCard parsed, Product? catalogProduct) =>
        catalogProduct is null
            ? parsed with { ImageUrl = null }
            : parsed with
            {
                Name = catalogProduct.Name,
                Price = catalogProduct.Price,
                Description = LimitDescription(
                    string.IsNullOrWhiteSpace(catalogProduct.Description)
                        ? parsed.Description
                        : catalogProduct.Description),
                ImageUrl = catalogProduct.ImageUrl,
            };

    /// <summary>Finds the catalog product represented by a model-authored product heading.</summary>
    public static Product? FindCatalogProduct(ProductCard parsed, IEnumerable<Product> catalog)
    {
        var parsedName = NormalizeName(parsed.Name);
        return catalog
            .OrderByDescending(item => NormalizeName(item.Name).Length)
            .FirstOrDefault(item =>
            {
                var catalogName = NormalizeName(item.Name);
                return catalogName == parsedName || parsedName.Contains(catalogName, StringComparison.Ordinal);
            });
    }

    [GeneratedRegex(
        "(?m)^[ \\t]*(?:[-*•]\\s+)?(?<name>[^$\\r\\n]+?)\\s*(?:\\(\\s*(?:product\\s*)?id\\s*:?\\s*\\d+\\s*\\))?\\s*(?:[—–-]|\\|)\\s*\\$(?<price>\\d+(?:\\.\\d{1,2})?)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProductHeadingRegex();

    [GeneratedRegex(
        "(?<markdown>!?\\[(?<alt>[^\\]]*)\\]\\((?<url>https?://[^)\\s]+)(?:\\s+[^)]*)?\\))",
        RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(
        "^(https?://\\S+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneUrlRegex();

    [GeneratedRegex(
        "\\s*\\((?:product\\s*)?id\\s*:?\\s*\\d+\\)\\s*",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProductIdRegex();

    [GeneratedRegex(
        "\\b(?:product\\s+)?id\\s*:?\\s*\\d+\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex InlineProductIdRegex();

    [GeneratedRegex(
        "^\\s*(?:product\\s+)?(?:name|price|collection|materials|story|description|product\\s+id)\\s*:\\s*.*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SystemDetailLineRegex();

    /// <summary>Parses assistant text into ordinary prose, images, and display-only product cards.</summary>
    public static IReadOnlyList<Segment> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [new Segment(SegmentType.Text, string.Empty)];

        var segments = new List<Segment>();
        var headings = ProductHeadingRegex().Matches(text);
        var lastIndex = 0;

        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            AddTextOrImageSegments(segments, text[lastIndex..heading.Index]);

            var end = index + 1 < headings.Count ? headings[index + 1].Index : text.Length;
            var block = text[heading.Index..end];
            var product = ParseProduct(heading, block);

            if (product is null)
            {
                AddTextOrImageSegments(segments, block);
            }
            else
            {
                segments.Add(new Segment(SegmentType.Product, string.Empty, Product: product));
            }

            lastIndex = end;
        }

        AddTextOrImageSegments(segments, text[lastIndex..]);
        return segments.Count == 0
            ? [new Segment(SegmentType.Text, text)]
            : segments;
    }

    private static ProductCard? ParseProduct(Match heading, string block)
    {
        if (!decimal.TryParse(
                heading.Groups["price"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var price))
        {
            return null;
        }

        var name = ProductIdRegex().Replace(heading.Groups["name"].Value, string.Empty).Trim();
        name = Regex.Replace(name, @"^\s*(?:product\s*:\s*)", string.Empty, RegexOptions.IgnoreCase);
        name = TrimDecorators(name);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var image = MarkdownImageRegex().Match(block);
        var imageUrl = image.Success ? CleanUrl(image.Groups["url"].Value) : null;
        var descriptionText = block[heading.Length..];
        if (image.Success)
            descriptionText = descriptionText.Replace(image.Groups["markdown"].Value, string.Empty);

        var description = CleanDescription(descriptionText);
        var alt = image.Success ? image.Groups["alt"].Value.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(alt)
            && !string.Equals(alt, name, StringComparison.OrdinalIgnoreCase))
        {
            description = LimitDescription(alt);
        }

        return new ProductCard(name, price, description, imageUrl);
    }

    private static void AddTextOrImageSegments(List<Segment> segments, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var matches = MarkdownImageRegex().Matches(text);
        var lastIndex = 0;
        foreach (Match match in matches)
        {
            AddTextSegment(segments, text[lastIndex..match.Index]);
            segments.Add(new Segment(
                SegmentType.Image,
                string.Empty,
                CleanUrl(match.Groups["url"].Value),
                match.Groups["alt"].Value));
            lastIndex = match.Index + match.Length;
        }

        AddTextSegment(segments, text[lastIndex..]);
    }

    private static void AddTextSegment(List<Segment> segments, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Bare URLs are protocol data, never visible chat content. Keep non-image URLs out of the
        // transcript as well; product images are handled by the dedicated card above.
        var lines = text.Split('\n')
            .Where(line => !StandaloneUrlRegex().IsMatch(line.Trim()))
            .Where(line => !SystemDetailLineRegex().IsMatch(line))
            .Select(line => InlineProductIdRegex().Replace(line, string.Empty))
            .ToArray();
        var cleaned = string.Join('\n', lines).Trim();
        if (!string.IsNullOrWhiteSpace(cleaned))
            segments.Add(new Segment(SegmentType.Text, cleaned));
    }

    private static string CleanDescription(string text)
    {
        var lines = text.Split('\n')
            .Select(line => MarkdownImageRegex().Replace(line, string.Empty))
            .Where(line => !SystemDetailLineRegex().IsMatch(line))
            .Select(line => Regex.Replace(line, @"^\s*image\s*:\s*|\s+image\s*:\s*$", string.Empty, RegexOptions.IgnoreCase))
            .Select(line => Regex.Replace(line, @"\bdescription\s*:\s*", string.Empty, RegexOptions.IgnoreCase))
            .Select(line => InlineProductIdRegex().Replace(line, string.Empty))
            .Select(line => Regex.Replace(line, @"https?://\S+", string.Empty, RegexOptions.IgnoreCase))
            .Select(line => Regex.Replace(line, @"[*_`#]", string.Empty))
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return LimitDescription(Regex.Replace(string.Join(' ', lines), @"\s+", " ").Trim());
    }

    private static string LimitDescription(string text)
    {
        if (text.Length <= MaxDescriptionLength)
            return text;

        var cutoff = text[..MaxDescriptionLength].LastIndexOf(' ');
        return $"{text[..(cutoff > 0 ? cutoff : MaxDescriptionLength)].TrimEnd()}…";
    }

    private static string CleanUrl(string value) => value.Trim().TrimEnd('.', ',', ';');

    private static string NormalizeName(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string TrimDecorators(string value) =>
        value.Trim().TrimStart('-', '*', '•').Trim();
}
