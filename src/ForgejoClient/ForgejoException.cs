namespace Forgejo.Client;

/// <summary>
/// Thrown when a Forgejo API call fails: a non-2xx response (after retries),
/// a transport error, or a deserialization problem. Carries the HTTP status
/// and the raw server body when available.
/// </summary>
public sealed class ForgejoException : Exception
{
    /// <summary>The HTTP status code, or <c>null</c> for transport-level failures.</summary>
    public int? StatusCode { get; }

    /// <summary>The machine-readable error code from the server body, when present.</summary>
    public string? ErrorCode { get; }

    /// <summary>The raw response body, for diagnostics.</summary>
    public string? ResponseBody { get; }

    public ForgejoException(string message, int? statusCode = null, string? errorCode = null, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ResponseBody = responseBody;
    }
}

/// <summary>
/// Thrown by the search endpoints (<c>GET /repos/search</c>,
/// <c>GET /repos/search/code</c>) when the instance does not serve the
/// route (HTTP 403/404) — i.e. the search capability is disabled or
/// unknown on this Forgejo build. Distinct from <see cref="ForgejoException"/>
/// because the caller's next step differs: this is a capability-level
/// failure, not a missing object — the MCP surface maps it to the typed
/// <c>search_disabled</c> error instead of <c>not_found</c> /
/// <c>forbidden</c> (see issue #38).
/// </summary>
public sealed class SearchDisabledException : Exception
{
    /// <summary>The HTTP status that revealed the route is not served.</summary>
    public int? StatusCode { get; }

    /// <summary>The raw server body (usually a plain "page not found").</summary>
    public string? ServerBody { get; }

    public SearchDisabledException(string message, int? statusCode = null, string? serverBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ServerBody = serverBody;
    }
}
