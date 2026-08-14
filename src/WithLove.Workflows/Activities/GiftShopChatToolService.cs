using System.Text.Json;
using Temporalio.Activities;
using WithLove.Workflows.Chat;
using WithLove.Workflows.Loyalty;
using WithLove.Workflows.Workflows;

namespace WithLove.Workflows.Activities;

internal sealed record AddToCartToolResult(string Message, CartAction? Action);

internal sealed class GiftShopChatToolService(IHttpClientFactory httpClientFactory)
{
    public async Task<string> SearchProductsAsync(string query, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("productsApi");
        var response = await http.GetAsync(
            $"/api/products/search?q={Uri.EscapeDataString(query)}&top=10",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            return "Sorry, I couldn't search for products right now.";

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return SummarizeProductList(json);
    }

    public async Task<string> GetProductDetailsAsync(int productId, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("productsApi");
        var response = await http.GetAsync($"/api/products/{productId}", cancellationToken);

        if (!response.IsSuccessStatusCode)
            return $"Sorry, I couldn't find product {productId}.";

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return SummarizeProduct(json);
    }

    public async Task<string> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("productsApi");
        var response = await http.GetAsync("/api/categories", cancellationToken);

        if (!response.IsSuccessStatusCode)
            return "Sorry, I couldn't load collections right now.";

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return SummarizeCategoryList(json);
    }

    public async Task<string> BrowseCategoryAsync(int categoryId, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("productsApi");
        var response = await http.GetAsync($"/api/products/category/{categoryId}", cancellationToken);

        if (!response.IsSuccessStatusCode)
            return "Sorry, I couldn't load that collection right now.";

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return SummarizeProductList(json);
    }

    public async Task<AddToCartToolResult> BuildAddToCartAsync(
        int productId,
        int quantity,
        CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("productsApi");
        var response = await http.GetAsync($"/api/products/{productId}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new AddToCartToolResult(
                $"Sorry, I couldn't find product {productId} to add to your cart. " +
                "Please verify the product ID from search results.",
                null);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var product = document.RootElement;

        var id = product.GetProperty("id").GetInt32();
        var name = product.GetProperty("name").GetString() ?? "Unknown";
        var imageUrl = product.TryGetProperty("imageUrl", out var image)
            ? image.GetString() ?? string.Empty
            : string.Empty;
        var price = product.GetProperty("price").GetDecimal();
        var stripePriceId = product.TryGetProperty("stripePriceId", out var stripePrice)
            ? stripePrice.GetString() ?? string.Empty
            : string.Empty;

        var action = new CartAction(
            CartActionType.Add,
            id,
            name,
            imageUrl,
            price,
            stripePriceId,
            quantity);
        return new AddToCartToolResult(
            $"Added {quantity}x {name} (ID: {id}, ${price:F2}) to the cart.",
            action);
    }

    public async Task<string> ViewLoyaltyPointsAsync(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return "Love Tokens are available to logged-in customers. " +
                   "Sign in to see your balance and start earning!";
        }

        var logger = ActivityExecutionContext.Current.Logger;

        try
        {
            var client = ActivityExecutionContext.Current.TemporalClient;
            var handle = client.GetWorkflowHandle<LoyaltyAccountWorkflow>($"loyalty-{userId}");
            var profile = await handle.QueryAsync(workflow => workflow.GetLoyaltyProfile());

            var nextTierMessage = profile.Tier == LoyaltyTier.Gold
                ? "You've reached the highest tier — Gold!"
                : $"Earn {profile.PointsToNextTier} more to reach {NextTierName(profile.Tier)}.";

            return $"You have {profile.Balance} Love Tokens ({profile.Tier} tier). " +
                   $"{nextTierMessage} (Lifetime earned: {profile.LifetimeEarned} pts. " +
                   "Redeem at checkout: 100 pts = $1 off.)";
        }
        catch (Temporalio.Exceptions.RpcException exception)
            when (exception.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
        {
            return "You don't have any Love Tokens yet. Complete a purchase to start earning — " +
                   "1 token per $1 spent!";
        }
        catch (Exception exception)
        {
            logger.UnableToLoadLoveTokens(exception, userId);
            return "I couldn't load your Love Tokens balance right now. Please try again in a moment.";
        }
    }

    private static string NextTierName(LoyaltyTier tier) => tier switch
    {
        LoyaltyTier.Bronze => "Silver",
        LoyaltyTier.Silver => "Gold",
        _ => "the next tier",
    };

    internal static string SummarizeProduct(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FormatProductSummary(document.RootElement, detailed: true);
    }

    internal static string SummarizeProductList(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("value", out var items) || items.GetArrayLength() == 0)
            return "No products found.";

        return string.Join(
            "\n",
            items.EnumerateArray().Select(item => FormatProductSummary(item, detailed: false)));
    }

    private static string FormatProductSummary(JsonElement product, bool detailed)
    {
        var id = product.GetProperty("id").GetInt32();
        var name = product.GetProperty("name").GetString() ?? "Unknown";
        var price = product.GetProperty("price").GetDecimal();
        var category = product.TryGetProperty("categoryName", out var categoryElement)
            ? categoryElement.GetString() ?? string.Empty
            : string.Empty;
        var imageUrl = product.TryGetProperty("imageUrl", out var image)
            ? image.GetString() ?? string.Empty
            : string.Empty;
        var subCategory = product.TryGetProperty("subCategory", out var subCategoryElement)
            ? subCategoryElement.GetString() ?? string.Empty
            : string.Empty;
        var description = product.TryGetProperty("description", out var descriptionElement)
            ? descriptionElement.GetString() ?? string.Empty
            : string.Empty;

        if (!detailed)
        {
            return $"- ID: {id} | {name} | ${price:F2} | {category}" +
                   (string.IsNullOrEmpty(subCategory) ? string.Empty : $" > {subCategory}") +
                   (string.IsNullOrEmpty(imageUrl) ? string.Empty : $" | Image: {imageUrl}");
        }

        var lines = new List<string>
        {
            $"Product ID: {id}",
            $"Name: {name}",
            $"Price: ${price:F2}",
            $"Collection: {category}" +
            (string.IsNullOrEmpty(subCategory) ? string.Empty : $" > {subCategory}"),
        };

        if (!string.IsNullOrEmpty(description))
            lines.Add($"Description: {description}");
        if (!string.IsNullOrEmpty(imageUrl))
            lines.Add($"Image: {imageUrl}");

        if (product.TryGetProperty("materials", out var materials) && materials.GetArrayLength() > 0)
        {
            var names = materials.EnumerateArray()
                .Select(material => material.TryGetProperty("name", out var value)
                    ? value.GetString()
                    : null)
                .Where(value => !string.IsNullOrEmpty(value));
            var joinedNames = string.Join(", ", names!);
            if (!string.IsNullOrEmpty(joinedNames))
                lines.Add($"Materials: {joinedNames}");
        }

        if (product.TryGetProperty("storyTitle", out var story)
            && story.GetString() is { Length: > 0 } storyTitle)
        {
            lines.Add($"Story: {storyTitle}");
        }

        return string.Join("\n", lines);
    }

    internal static string SummarizeCategoryList(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("value", out var items) || items.GetArrayLength() == 0)
            return "No collections found.";

        return string.Join("\n", items.EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetInt32();
            var name = item.GetProperty("name").GetString() ?? "Unknown";
            var description = item.TryGetProperty("description", out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
            return $"- ID: {id} | {name}" +
                   (string.IsNullOrEmpty(description) ? string.Empty : $" | {description}");
        }));
    }
}
