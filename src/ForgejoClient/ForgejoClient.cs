namespace Forgejo.Client;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// A typed, retrying HTTP client for the Forgejo REST API (v1).
/// </summary>
/// <remarks>
/// Behaviour:
/// <list type="bullet">
/// <item>Auth: access token (<c>Authorization: token …</c>) or HTTP basic, per
/// <see cref="ForgejoCredentials"/>.</item>
/// <item>Retries: transient failures (HTTP 429, HTTP 5xx, transport errors) are
/// retried up to <c>maxRetries</c> times with exponential backoff; a
/// <c>Retry-After</c> response header (delta or date) takes precedence as the
/// wait for that round.</item>
/// <item>Errors: non-2xx responses (after the retry budget) and transport
/// failures surface as <see cref="ForgejoException"/> carrying status + body.</item>
/// </list>
/// Tests and advanced callers may inject a pre-configured <see cref="HttpClient"/>
/// (e.g. over a fake <see cref="HttpMessageHandler"/>); the client then does not
/// own it and will not dispose it.
/// </remarks>
public sealed class ForgejoClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _baseUrl;
    private readonly ForgejoCredentials _credentials;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBaseDelay;

    /// <summary>Maximum number of retry attempts after the first request.</summary>
    public int MaxRetries { get; }

    /// <summary>The Forgejo instance this client talks to.</summary>
    public Uri BaseUrl => _baseUrl;

    /// <summary>The credentials in use.</summary>
    public ForgejoCredentials Credentials => _credentials;

    /// <summary>
    /// Creates a client for the Forgejo instance at <paramref name="baseUrl"/>.
    /// </summary>
    /// <param name="baseUrl">Instance root, e.g. <c>https://git.home.internal</c> (a trailing slash is tolerated).</param>
    /// <param name="credentials">Auth material; <see cref="ForgejoCredentials.Anonymous"/> for public data.</param>
    /// <param name="maxRetries">
    /// Retries after the first attempt for transient failures (default 3 ⇒ 4 total attempts).
    /// </param>
    /// <param name="retryBaseDelay">
    /// Base backoff delay: attempt n waits <c>retryBaseDelay * 2^n</c> (default 1s).
    /// <see cref="TimeSpan.Zero"/> disables artificial delays (tests).
    /// </param>
    /// <param name="httpClient">Optional pre-configured HTTP client; when omitted the client creates and owns one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl"/> or <paramref name="credentials"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetries"/> &lt; 0 or <paramref name="retryBaseDelay"/> &lt; 0.</exception>
    public ForgejoClient(
        Uri baseUrl,
        ForgejoCredentials credentials,
        int maxRetries = 3,
        TimeSpan? retryBaseDelay = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(credentials);
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "maxRetries must be >= 0.");
        var baseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryBaseDelay), "retryBaseDelay must be >= TimeSpan.Zero.");

        _baseUrl = NormalizeApiRoot(baseUrl);
        _credentials = credentials;
        _maxRetries = maxRetries;
        _retryBaseDelay = baseDelay;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        _ownsHttp = httpClient is null;
        MaxRetries = maxRetries;
    }

    // ---------------------------------------------------------------------------
    // Public endpoint surface (wrapped by the MCP tools)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Lists the repositories visible to the authenticated user — the backing
    /// endpoint of the <c>list_repos</c> MCP tool.
    /// </summary>
    /// <remarks>
    /// Backing call: <c>GET /user/repos</c> (requires a token; unauthenticated
    /// calls fail with 401). The response carries a <c>total_count</c> header
    /// which is surfaced as <see cref="ListResult{T}.Total"/>.
    /// </remarks>
    public Task<ListResult<Repository>> ListRepositoriesAsync(
        Page page = default,
        CancellationToken ct = default)
    {
        page ??= new Page();
        return SendPagedAsync<Repository>("/user/repos", page.ToQuery(), null, ct);
    }

    /// <summary>
    /// Gets a single repository by owner and name — backing of <c>get_repo</c>.
    /// Errors: 404 (not found / no permission), 401 (missing auth), 403 (forbidden).
    /// </summary>
    public Task<Repository> GetRepositoryAsync(string owner, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return SendAsync<Repository>(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}", ct: ct);
    }

    /// <summary>
    /// Creates an issue in <c>owner/name</c> with <paramref name="title"/> — backing of
    /// <c>create_issue</c> (a mutating operation; see <see cref="CreateIssueRequest"/>).
    /// </summary>
    /// <remarks>Back: <c>POST /repos/{owner}/{name}/issues</c>. Requires write permission.</remarks>
    public Task<Issue> CreateIssueAsync(string owner, string name, CreateIssueRequest req, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ArgumentException("CreateIssueRequest.Title must not be empty.", nameof(req));
        return SendAsync<Issue>(HttpMethod.Post, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues", req, ct);
    }

    /// <summary>
    /// Lists the issues of <c>owner/name</c> — backing of <c>list_issues</c>.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{r}/issues</c>. Pull requests are included by
    /// default in Forgejo's issue list; the client surface exposes them via
    /// <c>list_pull_requests</c> as the dedicated endpoint.</remarks>
    public Task<ListResult<Issue>> ListIssuesAsync(
        string owner,
        string name,
        IssueListOptions filters = default,
        Page page = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        filters ??= new IssueListOptions();
        page ??= new Page();
        var q = filters.ToQuery();
        var full = q.Length == 0 ? page.ToQuery() : $"{q}&{page.ToQuery()}";
        return SendPagedAsync<Issue>($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues", full, null, ct);
    }

    /// <summary>
    /// Lists the pull requests of <c>owner/name</c> — backing of <c>list_pull_requests</c>.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{r}/pulls</c>.</remarks>
    public Task<ListResult<PullRequest>> ListPullRequestsAsync(
        string owner,
        string name,
        IssueListOptions filters = default,
        Page page = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        filters ??= new IssueListOptions();
        page ??= new Page();
        var q = filters.ToQuery();
        var full = q.Length == 0 ? page.ToQuery() : $"{q}&{page.ToQuery()}";
        return SendPagedAsync<PullRequest>($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls", full, null, ct);
    }

    /// <summary>
    /// Reads the <em>raw</em> content of a file at <paramref name="path"/> on
    /// <paramref name="branch"/> — backing of <c>get_file</c>.
    /// </summary>
    /// <remarks>
    /// Back: <c>GET /repos/{o}/{r}/raw/{path}?ref={branch}</c> (defaults to the
    /// repository's default branch when <paramref name="branch"/> is null).
    /// The payload may be any binary; <see cref="FileContent.Text"/> is only set
    /// when the bytes are valid UTF-8.
    /// </remarks>
    public Task<FileContent> GetFileContentAsync(
        string owner,
        string name,
        string path,
        string? branch = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var relative = path.StartsWith('/') ? path[1..] : path;
        var qs = branch is null ? string.Empty : $"?ref={Uri.EscapeDataString(branch)}";
        return SendRawAsync(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/raw/{relative}{qs}", ct)
            .ContinueWith(t =>
            {
                var (bytes, contentType) = t.GetAwaiter().GetResult();
                string? text = null;
                if (IsLikelyText(bytes, contentType))
                    text = Encoding.UTF8.GetString(bytes);
                return new FileContent { Path = path, Bytes = bytes, Text = text ?? string.Empty, ContentType = contentType, Size = bytes.Length };
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// Lists commits of <c>owner/name</c> on <paramref name="options.Branch"/> (or the
    /// default branch) — backing of <c>list_commits</c>.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{r}/commits</c>.</remarks>
    public Task<ListResult<RepositoryCommit>> ListCommitsAsync(
        string owner,
        string name,
        Page page = default,
        CommitListOptions options = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        page ??= new Page();
        options ??= new CommitListOptions();
        var b = string.IsNullOrEmpty(options.Branch) ? "" : $"&branch={Uri.EscapeDataString(options.Branch)}";
        return SendPagedAsync<RepositoryCommit>($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/commits?{page.ToQuery()}{b}", null, null, ct);
    }

    // ---------------------------------------------------------------------------
    // Wire / retry / error pipeline
    // ---------------------------------------------------------------------------

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var json = await SendAsyncCore(method, path, body, null, ct).ConfigureAwait(false);
        if (typeof(T) == typeof(string))
            return (T)(object)json;
        if (string.IsNullOrWhiteSpace(json))
            throw new ForgejoException($"Empty response deserialising to {typeof(T).Name} from {method} {PathOnly(path)}.");
        try
        {
            var v = ForgejoJson.FromJson<T>(json);
            if (v is null)
                throw new ForgejoException($"Null deserialising {typeof(T).Name} from {method} {PathOnly(path)}.");
            return v;
        }
        catch (JsonException je)
        {
            throw new ForgejoException($"Forgejo response for {method} {PathOnly(path)} is not valid {typeof(T).Name}: {je.Message}", responseBody: json, inner: je);
        }
    }

    private async Task<ListResult<T>> SendPagedAsync<T>(string path, string query, Action<HttpRequestMessage>? extra, CancellationToken ct)
    {
        var full = AppendQuery(path, query);
        var (json, total) = await SendAsyncCoreWithTotal(HttpMethod.Get, full, null, extra, ct).ConfigureAwait(false);
        var items = ForgejoJson.FromJson<IReadOnlyList<T>>(json)
            ?? throw new ForgejoException($"List response for {path} was not a JSON array.");
        return new ListResult<T>(items, total);
    }

    private async Task<(byte[] bytes, string contentType)> SendRawAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var (bytes, status, contentType) = await SendAsyncCoreBytes(method, path, null, ct).ConfigureAwait(false);
        if (status < 200 || status >= 300)
            throw new ForgejoException($"GET {PathOnly(path)} failed with HTTP {status} {HttpDescription(status)}.", status, responseBody: Encoding.UTF8.GetString(bytes));
        if (bytes.Length == 0)
            throw new ForgejoException($"GET {PathOnly(path)} returned no body.");
        return (bytes, contentType ?? string.Empty);
    }

    private async Task<string> SendAsyncCore(
        HttpMethod method, string path, object? body, Action<HttpRequestMessage>? extra, CancellationToken ct)
        => (await SendAsyncCoreWithTotal(method, path, body, null!, ct).ConfigureAwait(false)).json;

    private async Task<(string json, long? total)> SendAsyncCoreWithTotal(
        HttpMethod method, string path, object? body, Action<HttpRequestMessage>? extra, CancellationToken ct)
    {
        var (bytes, status, contentType) = await SendAsyncCoreBytes(method, path, body, ct, extra).ConfigureAwait(false);
        if (status < 200 || status >= 300)
            throw new ForgejoException(
                $"{method} {PathOnly(path)} failed with HTTP {status} {HttpDescription(status)}.",
                status,
                TryExtractErrorCode(bytes) ?? null,
                Encoding.UTF8.GetString(bytes ?? Array.Empty<byte>()));

        var json = bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);
        long? total = null;
        if (TryExtractTotal(bytes, out var t))
            total = t;
        return (json, total);
    }

    private async Task<(byte[]? bytes, int status, string contentType)> SendAsyncCoreBytes(
        HttpMethod method, string path, object? body, CancellationToken ct, Action<HttpRequestMessage>? extra = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var url = BuildUrl(path);
        var totalAttempts = _maxRetries + 1;
        TimeSpan? lastRetryAfter = null;
        Exception? lastTransport = null;

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = ComputeDelay(attempt - 1);
                if (lastRetryAfter is { } ra && ra > delay)
                    delay = ra;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await SendOnceAsync(method, url, body, extra, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // User cancellation: don't retry.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                lastTransport = ex;
                if (attempt == totalAttempts - 1)
                {
                    throw new ForgejoException(
                        $"Transport error contacting {method} {PathOnly(path)} after {totalAttempts} attempt(s): {ex.Message}",
                        inner: ex);
                }
                // Retryable: loop.
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var text = bytes is null || bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);

                // Capture Retry-After before the response is disposed.
                lastRetryAfter = ParseRetryAfter(response);

                if (status < 200 || status >= 300)
                {
                    if (IsRetryable(status) && attempt < totalAttempts - 1)
                        continue; // transient failure: loop
                    throw new ForgejoException(
                        $"{method} {PathOnly(path)} failed with HTTP {status} {HttpDescription(status)}.",
                        statusCode: status,
                        errorCode: TryExtractErrorCode(bytes),
                        responseBody: text);
                }
                return (bytes ?? Array.Empty<byte>(), status, contentType);
            }
        }

        throw new ForgejoException($"Retries exhausted contacting {method} {PathOnly(path)}.", inner: lastTransport);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, Uri url, object? body, Action<HttpRequestMessage>? extra, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Headers = { Accept = { new MediaTypeWithQualityHeaderValue("application/json") } },
        };
        if (body is not null)
        {
            var json = body is string s ? s : ForgejoJson.ToJson(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        _credentials.ApplyTo(request);
        extra?.Invoke(request);

        // SendAsync owns the response; caller disposes.
        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private TimeSpan ComputeDelay(int n) =>
        _retryBaseDelay * Math.Pow(2, Math.Min(10, n));

    private Uri BuildUrl(string path)
    {
        var trimmed = path.StartsWith('/') ? path[1..] : path;
        if (string.IsNullOrEmpty(trimmed))
            return _baseUrl;
        return new Uri(_baseUrl, trimmed);
    }

    /// <summary>
    /// Normalizes an instance root (e.g. <c>https://git.home.internal</c> or
    /// <c>https://git.home.internal/forgejo</c>) to the REST API root
    /// <c>https://git.home.internal/api/v1/</c>. Idempotent: URLs that already
    /// end in <c>/api/v1</c> (or <c>/api/v1/</c>) are left unchanged.
    /// </summary>
    private static Uri NormalizeApiRoot(Uri instanceRoot)
    {
        // Strip any trailing slash, then check for /api/v1.
        var builder = new UriBuilder(instanceRoot);
        var path = builder.Path ?? "/";
        // Remove trailing slash(es) for comparison.
        var trailing = path.TrimEnd('/');
        var basePath = trailing.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            ? trailing
            : trailing + "/api/v1";
        builder.Path = basePath + "/";
        return builder.Uri;
    }

    private static string PathOnly(string path)
    {
        var q = path.IndexOf('?');
        return q < 0 ? path : path[..q];
    }

    private static string AppendQuery(string path, string query)
    {
        if (string.IsNullOrEmpty(query))
            return path;
        var sep = path.Contains('?') ? "&" : "?";
        return $"{path}{sep}{query}";
    }

    private static bool IsRetryable(int status)
        => status == 429 || (status >= 500 && status < 600);

    private static string HttpDescription(int status)
    {
        try
        {
            return ((HttpStatusCode)status).ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null)
            return null;
        if (ra.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra.Date is { } date)
        {
            var span = date.ToUniversalTime() - DateTimeOffset.UtcNow;
            return span < TimeSpan.Zero ? TimeSpan.Zero : span;
        }
        return null;
    }

    private static string? TryExtractErrorCode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var key in new[] { "error", "code", "message" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind is JsonValueKind.String)
                    return el.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryExtractTotal(byte[]? bytes, out long total)
    {
        total = 0;
        if (bytes is null || bytes.Length == 0)
            return false;
        // Forgejo returns a total_count on the page envelope for some endpoints;
        // list endpoints return an array and total_count in the response header
        // "Total-Count", so this helper is currently used as a no-op fallback
        // for shapes that embed "total" in the JSON.
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var key in new[] { "total_count", "totalCount", "total" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el) && el.TryGetInt64(out var v))
                {
                    total = v;
                    return true;
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsLikelyText(byte[]? bytes, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.StartsWith("text/", StringComparison.Ordinal) ||
                ct.Contains("json", StringComparison.Ordinal) ||
                ct.Contains("xml", StringComparison.Ordinal) ||
                ct.Contains("javascript", StringComparison.Ordinal) ||
                ct.Contains("yaml", StringComparison.Ordinal) ||
                ct.Contains("csv", StringComparison.Ordinal) ||
                ct.Contains("plain", StringComparison.Ordinal) ||
                ct.Contains("html", StringComparison.Ordinal))
                return true;
            return false;
        }
        if (bytes is null || bytes.Length == 0)
            return true;
        // Heuristic: no NUL byte in the first 1KB ⇒ treat as text.
        var n = Math.Min(1024, bytes.Length);
        for (var i = 0; i < n; i++)
            if (bytes[i] == 0)
                return false;
        return true;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Mutation payload for <see cref="ForgejoClient.CreateIssueAsync"/>.
/// Maps to the <c>POST /repos/{o}/{r}/issues</c> body.
/// </summary>
public sealed record CreateIssueRequest
{
    /// <summary>Issue title (required).</summary>
    public string Title { get; init; } = null!;

    /// <summary>Issue body (Markdown; nullable).</summary>
    public string? Body { get; init; }

    /// <summary>Label names to attach (must exist in the repo).</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Assignee usernames (must exist on the instance).</summary>
    public IReadOnlyList<string> Assignees { get; init; } = []
;

    /// <summary>Milestone number, when the repository has milestones.</summary>
    public long? Milestone { get; init; }
}
