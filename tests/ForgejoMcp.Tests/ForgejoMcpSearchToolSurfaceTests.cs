namespace Forgejo.Tests;

using System.Net.Http;
using System.Net;
using System.Text;
using Forgejo.Mcp;
using Forgejo.Client;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// MCP tool-surface tests for <c>search_repos</c> + <c>search_code</c>
/// (issue #38).
///
/// Wire contracts (verified via the instance's swagger + live probes,
/// 2026-11-24, git.home.internal 16.0.3+gitea-1.22.0):
/// - <c>GET /repos/search</c> answers the SearchResults envelope
///   <c>{ok: bool, data: Repository[]}</c> — NOT the bare array the
///   list endpoints use. Results are scoped to the repos the calling
///   identity can see.
/// - <c>GET /repos/search/code</c> is NOT served on the acceptance
///   instance (every candidate route 404s; the swagger carries no
///   code-search path) → the typed <c>search_disabled</c> error.
///
/// Acceptance criteria covered: empty result (no error), hit, limit cap
/// (clamp to 1…100), disabled-route error, param passthrough
/// (owner/repo on code search; q/limit on both), sort/order passthrough.
/// </summary>
public class ForgejoMcpSearchToolSurfaceTests
{
    private static (ForgejoMcpToolSurface surface, TestHttpHandler handler) Build(
        IEnumerable<HttpResponseMessage>? responses = null)
    {
        var handler = new TestHttpHandler(responses);
        var client = new ForgejoClient(
            new Uri("https://git.example.com"),
            ForgejoCredentials.Anonymous(),
            retryBaseDelay: TimeSpan.Zero,
            httpClient: new HttpClient(handler));
        return (new ForgejoMcpToolSurface(client), handler);
    }

    private static HttpResponseMessage Ok(string body)
        => new(StatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(System.Net.HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Wire body matching the live SearchResults envelope shape.</summary>
    private const string SearchResultsJson = """
        {
          "ok": true,
          "data": [
            {
              "id": 20,
              "name": "mcp-forgejo-server",
              "full_name": "dark-eternity/mcp-forgejo-server",
              "description": "MCP server template for Forgejo (C#, ModelContextProtocol SDK)",
              "private": false,
              "empty": false,
              "fork": false,
              "template": false,
              "owner": { "id": 2, "login": "dark-eternity" },
              "default_branch": "main",
              "language": "C#",
              "stars_count": 0,
              "forks_count": 0,
              "watchers_count": 4,
              "open_issues_count": 7,
              "created_at": "2026-08-17T19:32:39Z",
              "updated_at": "2026-09-23T22:25:38Z",
              "html_url": "https://git.example.com/dark-eternity/mcp-forgejo-server"
            },
            {
              "id": 14,
              "name": "emberfall",
              "full_name": "dark-eternity/emberfall",
              "description": "Blazor .NET 10 RPG",
              "private": false,
              "empty": false,
              "fork": false,
              "template": false,
              "owner": { "id": 2, "login": "dark-eternity" },
              "default_branch": "main",
              "language": "C#",
              "created_at": "2026-08-20T10:00:00Z",
              "updated_at": "2026-09-20T08:00:00Z",
              "html_url": "https://git.example.com/dark-eternity/emberfall"
            }
          ]
        }
        """;

    // ------------------------------------------------------------------
    // search_repos — happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchRepos_hit_hits_search_route_and_returns_list_envelope()
    {
        var (surface, handler) = Build(new[] { Ok(SearchResultsJson) });

        var json = await surface.SearchRepos("mcp");

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/v1/repos/search", req.RequestUri!.AbsolutePath);
        // Default limit (20) + the query, URL-escaped, on the wire.
        Assert.Contains("q=mcp", req.RequestUri.Query);
        Assert.Contains("limit=20", req.RequestUri.Query);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _), "no error expected: " + json);
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
        Assert.Equal("dark-eternity/mcp-forgejo-server",
            root.GetProperty("items")[0].GetProperty("full_name").GetString());
        // Short page (2 < 20) → next_page is null (always present).
        Assert.True(root.GetProperty("next_page").ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task SearchRepos_empty_data_returns_empty_items_without_error()
    {
        var (surface, _) = Build(new[] { Ok("""{"ok":true,"data":[]}""") });

        var json = await surface.SearchRepos("zzz-no-match-xyz");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _), "no error expected: " + json);
        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task SearchRepos_total_from_x_total_count_header_is_surfaced()
    {
        var handler = new TestHttpHandler();
        handler.EnqueueWithHeaders(
            StatusCode.OK,
            SearchResultsJson,
            "application/json",
            ("x-total-count", "42"));
        var client = new ForgejoClient(
            new Uri("https://git.example.com"),
            ForgejoCredentials.Anonymous(),
            retryBaseDelay: TimeSpan.Zero,
            httpClient: new HttpClient(handler));
        var surface = new ForgejoMcpToolSurface(client);

        var json = await surface.SearchRepos("mcp");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("total").GetInt64());
    }

    // ------------------------------------------------------------------
    // search_repos — param passthrough + limit clamp
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchRepos_sort_and_order_pass_through_on_the_wire()
    {
        var (surface, handler) = Build(new[] { Ok(SearchResultsJson) });

        await surface.SearchRepos("mcp", sort: "stars", order: "desc");

        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.Contains("sort=stars", query);
        Assert.Contains("order=desc", query);
    }

    [Fact]
    public async Task SearchRepos_omitted_sort_and_order_absent_from_the_wire()
    {
        var (surface, handler) = Build(new[] { Ok(SearchResultsJson) });

        await surface.SearchRepos("mcp");

        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.DoesNotContain("sort=", query);
        Assert.DoesNotContain("order=", query);
    }

    [Theory]
    [InlineData(1000)]   // above the cap → 100
    [InlineData(5000)]   // far above the cap → 100
    [InlineData(100)]    // exactly at the cap → unchanged
    public async Task SearchRepos_limit_capped_at_100_on_the_wire(int limit)
    {
        var (surface, handler) = Build(new[] { Ok(SearchResultsJson) });

        await surface.SearchRepos("mcp", limit: limit);

        Assert.Contains("limit=100", handler.Requests.Single().RequestUri!.Query);
    }

    [Theory]
    [InlineData(0)]      // below the floor → 1
    [InlineData(-5)]     // far below the floor → 1
    [InlineData(1)]      // exactly at the floor → unchanged
    public async Task SearchRepos_limit_floored_at_1_on_the_wire(int limit)
    {
        var (surface, handler) = Build(new[] { Ok(SearchResultsJson) });

        await surface.SearchRepos("mcp", limit: limit);

        // limit=1 must not accidentally match limit=10/100 — anchor the
        // pair boundary.
        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.Matches(@"[?&]limit=1(&|$)", query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchRepos_blank_query_fails_fast_with_invalid_request_and_no_http(string query)
    {
        var (surface, handler) = Build();

        var json = await surface.SearchRepos(query);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // search_repos — disabled route → typed search_disabled
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SearchRepos_route_not_served_maps_to_search_disabled(StatusCode status)
    {
        var (surface, _) = Build(new[]
        {
            Status(status, """{"message":"page not found","url":"https://git.example.com/api/swagger"}""")
        });

        var json = await surface.SearchRepos("mcp");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("search_disabled", error.GetProperty("code").GetString());
        // The message explains WHY (route not served) and WHAT to do
        // (don't retry; use list_* tools).
        Assert.Contains("not served", error.GetProperty("message").GetString());
    }

    // ------------------------------------------------------------------
    // search_code — disabled route (the acceptance instance's reality)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchCode_route_not_served_maps_to_search_disabled()
    {
        var (surface, handler) = Build(new[]
        {
            Status(StatusCode.NotFound, """{"message":"page not found"}""")
        });

        var json = await surface.SearchCode("ForgejoClient", owner: "dark-eternity");

        // The client still attempted the canonical code-search route with
        // the narrowing params on the wire.
        var req = handler.Requests.Single();
        Assert.Equal("/api/v1/repos/search/code", req.RequestUri!.AbsolutePath);
        var query = req.RequestUri.Query;
        Assert.Contains("q=ForgejoClient", query);
        Assert.Contains("owner=dark-eternity", query);
        Assert.Contains("limit=20", query);
        Assert.DoesNotContain("repo=", query);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("search_disabled", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SearchCode_blank_q_fails_fast_with_invalid_request_and_no_http()
    {
        var (surface, handler) = Build();

        var json = await surface.SearchCode("");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // search_code — enabled-route passthrough (other instances)
    // ------------------------------------------------------------------

    /// <summary>
    /// Shape of a Gitea-style code-search hit (owner/repository/path
    /// nested objects) — the client must pass entries through VERBATIM,
    /// including the instance's own snake_case property names (no
    /// projection, no renaming by the serializer).
    /// </summary>
    private const string CodeSearchJson = """
        {
          "ok": true,
          "data": [
            {
              "owner": { "id": 2, "username": "dark-eternity" },
              "repository": { "id": 20, "name": "mcp-forgejo-server", "full_name": "dark-eternity/mcp-forgejo-server" },
              "path": { "name": "ForgejoClient.cs", "content": "..." }
            }
          ]
        }
        """;

    [Fact]
    public async Task SearchCode_hit_passes_entries_through_verbatim_with_owner_repo_on_the_wire()
    {
        var (surface, handler) = Build(new[] { Ok(CodeSearchJson) });

        var json = await surface.SearchCode("ForgejoClient", owner: "dark-eternity", repo: "mcp-forgejo-server");

        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.Contains("owner=dark-eternity", query);
        Assert.Contains("repo=mcp-forgejo-server", query);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _), "no error expected: " + json);
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        var entry = root.GetProperty("items")[0];
        // Verbatim passthrough: the instance's property names survive.
        Assert.Equal("dark-eternity/mcp-forgejo-server",
            entry.GetProperty("repository").GetProperty("full_name").GetString());
        Assert.Equal("dark-eternity",
            entry.GetProperty("owner").GetProperty("username").GetString());
        Assert.Equal("ForgejoClient.cs",
            entry.GetProperty("path").GetProperty("name").GetString());
    }
}
