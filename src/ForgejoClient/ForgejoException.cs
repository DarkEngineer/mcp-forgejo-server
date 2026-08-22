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
