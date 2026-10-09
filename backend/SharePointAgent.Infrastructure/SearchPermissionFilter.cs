namespace SharePointAgent.Infrastructure;

/// <summary>
/// The OData filter that limits Azure AI Search results to what one user may read: anonymously shared files,
/// and files whose stored principals include one of the user's. Shared by the search endpoints and the graph
/// evidence check so both apply exactly the same rule.
/// </summary>
internal static class SearchPermissionFilter
{
    /// <summary>
    /// The filter for <paramref name="userId"/>, or null without one. A null filter means unrestricted, so a
    /// caller may only omit the user on trusted, non-user-facing calls.
    /// </summary>
    public static async Task<string?> BuildAsync(SharePointClient sharePointClient, string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var principals = await sharePointClient.GetUserPrincipalsAsync(userId, cancellationToken);
        if (principals.Count == 0)
        {
            return "hasAnonymousAccess eq true";
        }

        var values = string.Join(',', principals.Select(Escape));
        return $"hasAnonymousAccess eq true or allowedPrincipals/any(p: search.in(p, '{values}', ','))";
    }

    public static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
