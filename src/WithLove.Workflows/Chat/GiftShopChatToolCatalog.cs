using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI;
using WithLove.Workflows.Activities;

namespace WithLove.Workflows.Chat;

public static class GiftShopChatToolCatalog
{
    /// <summary>Smallest quantity a single <c>add_to_cart</c> call may contribute.</summary>
    public const int MinAddToCartQuantity = 1;

    /// <summary>
    /// Largest quantity a single <c>add_to_cart</c> call may contribute.
    /// <para>
    /// The model can emit any <see cref="int"/> it likes, so this bound is enforced twice on
    /// purpose. The <see cref="RangeAttribute"/> on the parameter publishes it into the tool's
    /// JSON schema so the model is told the rule, and <see cref="Math.Clamp(int, int, int)"/>
    /// inside the implementation enforces it because a schema is advice, not a gate — nothing
    /// in Microsoft.Extensions.AI validates DataAnnotations at invocation time.
    /// </para>
    /// <para>
    /// The attribute must appear on the declaration <em>and</em> the worker implementation.
    /// Durable tools are fingerprinted on Name + JsonSchema + ReturnJsonSchema, and
    /// <c>[Range]</c> emits <c>minimum</c>/<c>maximum</c> into that schema, so changing one side
    /// alone produces a non-retryable "does not match frozen declaration" failure at run time.
    /// </para>
    /// </summary>
    public const int MaxAddToCartQuantity = 99;

    public static IReadOnlyList<AIFunctionDeclaration> CreateDeclarations() =>
    [
        Declaration(SearchProductsDeclarationAsync, "search_products",
            "Search for products by name or description. Use this to find gifts matching customer needs."),
        Declaration(GetProductDetailsDeclarationAsync, "get_product_details",
            "Get full details for a specific product including materials, features, and story."),
        Declaration(GetCategoriesDeclarationAsync, "get_categories",
            "Get all product collections (categories) available in the shop."),
        Declaration(BrowseCategoryDeclarationAsync, "browse_category",
            "Browse products in a specific collection (category) by ID."),
        Declaration(AddToCartDeclarationAsync, "add_to_cart",
            "Add a product to the customer's cart. Use the exact product ID from search or browse results."),
        Declaration(RemoveFromCartDeclarationAsync, "remove_from_cart",
            "Remove one or more products from the cart. Use exact product IDs from view_cart."),
        Declaration(ViewCartDeclarationAsync, "view_cart",
            "View the current contents of the customer's cart. Call this before answering questions about the cart."),
        Declaration(ClearCartDeclarationAsync, "clear_cart",
            "Empty the entire cart. Use this when the customer wants to start fresh or remove everything."),
        Declaration(NavigateToProductDeclarationAsync, "navigate_to_product",
            "Navigate the customer to a product detail page."),
        Declaration(NavigateToCollectionDeclarationAsync, "navigate_to_collection",
            "Navigate the customer to a product collection (category) page. " +
            "ALWAYS call get_categories first to find the numeric category ID — never guess it."),
        Declaration(NavigateToCartDeclarationAsync, "navigate_to_cart",
            "Navigate the customer to their cart page."),
        Declaration(NavigateToCheckoutDeclarationAsync, "navigate_to_checkout",
            "Navigate the customer to the checkout page."),
        Declaration(ViewLoyaltyPointsDeclarationAsync, "view_loyalty_points",
            "Get the current customer's Love Tokens balance, tier, and progress toward the next tier. " +
            "Use when the customer asks about their points, balance, rewards, or tier status."),
    ];

    internal static DurableToolActivation<GiftShopChatTurnState> CreateActivation(
        IServiceProvider services,
        DurableToolInvocationContext<GiftShopChatRequestData, GiftShopChatTurnState> context,
        AIFunctionDeclaration declaration)
    {
        services.GetRequiredService<ILogger<GiftShopChatToolService>>()
            .InvokingGiftShopTool(
                declaration.Name,
                context.Metadata.CorrelationId,
                context.Metadata.IdempotencyKey);
        var functions = new GiftShopChatToolFunctions(
            services.GetRequiredService<GiftShopChatToolService>(),
            context);

        var function = declaration.Name switch
        {
            "search_products" => Function(functions.SearchProductsAsync, declaration),
            "get_product_details" => Function(functions.GetProductDetailsAsync, declaration),
            "get_categories" => Function(functions.GetCategoriesAsync, declaration),
            "browse_category" => Function(functions.BrowseCategoryAsync, declaration),
            "add_to_cart" => Function(functions.AddToCartAsync, declaration),
            "remove_from_cart" => Function(functions.RemoveFromCartAsync, declaration),
            "view_cart" => Function(functions.ViewCartAsync, declaration),
            "clear_cart" => Function(functions.ClearCartAsync, declaration),
            "navigate_to_product" => Function(functions.NavigateToProductAsync, declaration),
            "navigate_to_collection" => Function(functions.NavigateToCollectionAsync, declaration),
            "navigate_to_cart" => Function(functions.NavigateToCartAsync, declaration),
            "navigate_to_checkout" => Function(functions.NavigateToCheckoutAsync, declaration),
            "view_loyalty_points" => Function(functions.ViewLoyaltyPointsAsync, declaration),
            _ => throw new InvalidOperationException($"Unknown GiftShop tool '{declaration.Name}'."),
        };

        var stateful = declaration.Name is
            "add_to_cart" or
            "remove_from_cart" or
            "clear_cart" or
            "navigate_to_product" or
            "navigate_to_collection" or
            "navigate_to_cart" or
            "navigate_to_checkout";

        return new DurableToolActivation<GiftShopChatTurnState>
        {
            Function = function,
            CompleteState = stateful
                ? (_, _) => ValueTask.FromResult(functions.CompletedState is { } state
                    ? DurableStateUpdate<GiftShopChatTurnState>.Replace(state)
                    : DurableStateUpdate<GiftShopChatTurnState>.Unchanged)
                : null,
        };
    }

    private static AIFunctionDeclaration Declaration(
        Delegate method,
        string name,
        string description) =>
        AIFunctionFactory.Create(method, name, description).AsDeclarationOnly();

    private static AIFunction Function(
        Delegate method,
        AIFunctionDeclaration declaration) =>
        AIFunctionFactory.Create(method, declaration.Name, declaration.Description);

    private static Task<string> SearchProductsDeclarationAsync(
        [Description("Search query for finding products")] string query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    private static Task<string> GetProductDetailsDeclarationAsync(
        [Description("The product ID to look up")] int productId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    private static Task<string> GetCategoriesDeclarationAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    private static Task<string> BrowseCategoryDeclarationAsync(
        [Description("The category ID to browse")] int categoryId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    private static Task<string> AddToCartDeclarationAsync(
        [Description("The exact product ID from search or browse results")] int productId,
        [Range(MinAddToCartQuantity, MaxAddToCartQuantity)]
        [Description("Quantity to add, from 1 to 99 (default 1)")] int quantity = 1,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    private static Task<string> RemoveFromCartDeclarationAsync(
        [Description("Comma-separated product IDs to remove from the cart (e.g. '3' or '3,7,12')")]
        string productIds) =>
        Task.FromResult(string.Empty);

    private static Task<string> ViewCartDeclarationAsync() => Task.FromResult(string.Empty);

    private static Task<string> ClearCartDeclarationAsync() => Task.FromResult(string.Empty);

    private static Task<string> NavigateToProductDeclarationAsync(
        [Description("The product ID to navigate to")] int productId) =>
        Task.FromResult(string.Empty);

    private static Task<string> NavigateToCollectionDeclarationAsync(
        [Description("Numeric category ID from get_categories results. Use 0 only to show all collections.")]
        int categoryId = 0) =>
        Task.FromResult(string.Empty);

    private static Task<string> NavigateToCartDeclarationAsync() => Task.FromResult(string.Empty);

    private static Task<string> NavigateToCheckoutDeclarationAsync() => Task.FromResult(string.Empty);

    private static Task<string> ViewLoyaltyPointsDeclarationAsync() => Task.FromResult(string.Empty);

    private sealed class GiftShopChatToolFunctions(
        GiftShopChatToolService service,
        DurableToolInvocationContext<GiftShopChatRequestData, GiftShopChatTurnState> context)
    {
        private GiftShopChatTurnState State =>
            context.TurnState ?? GiftShopChatTurnState.Create([]);

        public GiftShopChatTurnState? CompletedState { get; private set; }

        public Task<string> SearchProductsAsync(
            [Description("Search query for finding products")] string query,
            CancellationToken cancellationToken = default) =>
            service.SearchProductsAsync(query, cancellationToken);

        public Task<string> GetProductDetailsAsync(
            [Description("The product ID to look up")] int productId,
            CancellationToken cancellationToken = default) =>
            service.GetProductDetailsAsync(productId, cancellationToken);

        public Task<string> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
            service.GetCategoriesAsync(cancellationToken);

        public Task<string> BrowseCategoryAsync(
            [Description("The category ID to browse")] int categoryId,
            CancellationToken cancellationToken = default) =>
            service.BrowseCategoryAsync(categoryId, cancellationToken);

        public async Task<string> AddToCartAsync(
            [Description("The exact product ID from search or browse results")] int productId,
            [Range(MinAddToCartQuantity, MaxAddToCartQuantity)]
            [Description("Quantity to add, from 1 to 99 (default 1)")] int quantity = 1,
            CancellationToken cancellationToken = default)
        {
            // Clamp before the value reaches turn state. A model-supplied int is untrusted input:
            // negatives produce a negative subtotal and large values overflow the cart's running
            // total. See MaxAddToCartQuantity for why the [Range] attribute alone is not enough.
            quantity = Math.Clamp(quantity, MinAddToCartQuantity, MaxAddToCartQuantity);

            var result = await service.BuildAddToCartAsync(productId, quantity, cancellationToken);
            if (result.Action is not { } action)
                return result.Message;

            var workingCart = new WorkingCart(State.WorkingCart);
            workingCart.Add(action.ProductId, action.ProductName, action.Price, action.Quantity);
            CompletedState = State with
            {
                WorkingCart = [.. workingCart.Items],
                CartActions = [.. State.CartActions, action],
            };
            return result.Message;
        }

        public Task<string> RemoveFromCartAsync(
            [Description("Comma-separated product IDs to remove from the cart (e.g. '3' or '3,7,12')")]
            string productIds)
        {
            var workingCart = new WorkingCart(State.WorkingCart);
            var actions = State.CartActions.ToList();
            var removed = new List<string>();

            foreach (var raw in productIds.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(raw, out var productId))
                    continue;

                var item = workingCart.FindById(productId);
                actions.Add(new CartAction(CartActionType.Remove, ProductId: productId));
                removed.Add(item is null
                    ? $"ID: {productId}"
                    : $"{item.ProductName} (ID: {productId})");
                workingCart.Remove(productId);
            }

            if (removed.Count == 0)
            {
                return Task.FromResult(
                    "No valid product IDs provided. Check the cart with view_cart first.");
            }

            CompletedState = State with
            {
                WorkingCart = [.. workingCart.Items],
                CartActions = actions,
            };
            return Task.FromResult($"Removed from cart: {string.Join(", ", removed)}");
        }

        public Task<string> ViewCartAsync() =>
            Task.FromResult(new WorkingCart(State.WorkingCart).Summarize());

        public Task<string> ClearCartAsync()
        {
            CompletedState = State with
            {
                WorkingCart = [],
                CartActions = [.. State.CartActions, new CartAction(CartActionType.Clear)],
            };
            return Task.FromResult("Cart has been emptied.");
        }

        public Task<string> NavigateToProductAsync(
            [Description("The product ID to navigate to")] int productId) =>
            AddNavigationAsync(
                new NavigationAction(NavigationTarget.Product, $"/product/{productId}"),
                $"Navigating to product {productId} page.");

        public Task<string> NavigateToCollectionAsync(
            [Description("Numeric category ID from get_categories results. Use 0 only to show all collections.")]
            int categoryId = 0)
        {
            var url = categoryId > 0 ? $"/collections/{categoryId}" : "/collections";
            var message = categoryId > 0
                ? $"Navigating to collection {categoryId}."
                : "Navigating to all collections.";
            return AddNavigationAsync(
                new NavigationAction(NavigationTarget.Collection, url),
                message);
        }

        public Task<string> NavigateToCartAsync() =>
            AddNavigationAsync(
                new NavigationAction(NavigationTarget.Cart, "/cart"),
                "Navigating to your cart.");

        public Task<string> NavigateToCheckoutAsync() =>
            AddNavigationAsync(
                new NavigationAction(NavigationTarget.Checkout, "/checkout"),
                "Navigating to checkout.");

        public Task<string> ViewLoyaltyPointsAsync() =>
            service.ViewLoyaltyPointsAsync(context.RequestData.User?.UserId);

        private Task<string> AddNavigationAsync(NavigationAction action, string message)
        {
            CompletedState = State with
            {
                NavigationActions = [.. State.NavigationActions, action],
            };
            return Task.FromResult(message);
        }
    }
}
