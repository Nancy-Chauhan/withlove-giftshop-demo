namespace WithLove.ProductsAPI.Tests.Traits;

/// <summary>
/// xUnit Trait constants for categorizing tests.
/// Usage: [Trait(TestTraits.Category, TestTraits.Integration)]
/// </summary>
public static class TestTraits
{
    public const string Category = "Category";
    public const string Integration = "Integration";
    public const string Unit = "Unit";

    /// <summary>
    /// Marks a test that cannot run without developer secrets, and therefore cannot run in CI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied at class level to the suites that boot the Aspire AppHost. The AppHost wires
    /// <c>Parameters:openai-api-key</c> into ProductsAPI, and ProductsAPI's startup constructs an
    /// <c>EmbeddingClient</c> eagerly — an empty key throws before the host ever becomes ready, so
    /// every test in those classes fails at fixture initialization rather than on an assertion.
    /// </para>
    /// <para>
    /// The trait name and value are a contract with <c>.github/workflows/build.yml</c>, which
    /// excludes them with <c>--filter "RequiresSecrets!=true"</c>. Renaming either side silently
    /// re-enables the secret-dependent tests, which will then fail the CI build. See the Testing section of
    /// <c>CLAUDE.md</c>.
    /// </para>
    /// </remarks>
    public const string RequiresSecrets = "RequiresSecrets";

    /// <summary>Value paired with <see cref="RequiresSecrets"/>; matched literally by CI.</summary>
    public const string True = "true";

    // Feature-level categorization
    public const string Feature = "Feature";
    public const string Validation = "Validation";
    public const string Idempotency = "Idempotency";
    public const string Concurrency = "Concurrency";
    public const string Pagination = "Pagination";
    public const string Search = "Search";
    public const string SoftDelete = "SoftDelete";
    public const string ErrorHandling = "ErrorHandling";
    public const string Health = "Health";
    public const string Database = "Database";
    public const string Caching = "Caching";

    // Duration categorization for long-running tests
    public const string Duration = "Duration";
    public const string Long = "Long";

    // Unit test specific traits
    public const string ETag = "ETag";
    public const string ApiVersion = "ApiVersion";
    public const string ResponseHeaders = "ResponseHeaders";
}
