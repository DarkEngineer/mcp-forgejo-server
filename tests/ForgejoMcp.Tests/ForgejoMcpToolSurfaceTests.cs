namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using Forgejo.Mcp;
using Forgejo.Client;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// Tests of the MCP tool surface against a fake Forgejo API.
/// Tool methods never throw for API-level failures — they return the
/// <c>{"error":{...}}</c> JSON payload — so assert on the payload.
/// </summary>
public class ForgejoMcpToolSurfaceTests
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

    [Fact]
    public async Task GetRepo_success_returns_snake_case_json()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":42,"full_name":"owner/repo","name":"repo","description":"demo","default_branch":"main"}""",
                    Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.GetRepo("owner", "repo");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("owner/repo", doc.RootElement.GetProperty("full_name").GetString());
        Assert.Equal("/api/v1/repos/owner/repo", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetRepo_404_returns_error_payload_not_exception()
    {
        var (surface, _) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.NotFound)
            {
                Content = new StringContent("""{"message":"not found"}""", Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.GetRepo("owner", "missing");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        Assert.Contains("Forgejo API error", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateIssue_posts_and_returns_created_issue()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.Created)
            {
                Content = new StringContent("""{"id":9,"title":"Bug","state":"open"}""", Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.CreateIssue(
            owner: "o", name: "n", title: "Bug",
            body: "details", labels: new[] { "bug" }, assignees: null, milestone: null);

        var request = handler.Requests.Single();
        var body = handler.Bodies.Single();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(9, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"title\":\"Bug\"", body);
        Assert.Contains("\"body\":\"details\"", body);
    }

    [Fact]
    public async Task ListIssues_sends_state_and_page_query()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":1,"title":"a","state":"open"},{"id":2,"title":"b","state":"open"}]""",
                    Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.ListIssues(
            owner: "o", name: "n", state: "all", label: new[] { "x", "y" }, page: 2, limit: 10);

        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/issues", url.AbsolutePath);
        // The client treats state="all" as "no state filter" and omits it from the query.
        Assert.DoesNotContain("state=", url.Query);
        Assert.Contains("labels=x", url.Query);
        Assert.Contains("page=2&limit=10", url.Query);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ListPullRequests_hits_pulls_endpoint()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            }
        });

        await surface.ListPullRequests(owner: "o", name: "n", state: "open");
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/pulls", url.AbsolutePath);
        Assert.Contains("state=open", url.Query);
    }

    [Fact]
    public async Task ListCommits_hits_commits_endpoint_with_branch()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":1,"sha":"abc","commit":{"message":"hello"}}]""",
                    Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.ListCommits(owner: "o", name: "n", branch: "dev", page: 1, limit: 5);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/commits", url.AbsolutePath);
        Assert.Contains("branch=dev", url.Query);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("abc", doc.RootElement.GetProperty("items")[0].GetProperty("sha").GetString());
    }

    [Fact]
    public async Task GetFile_returns_text_when_utf8()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent("# Hello\nworld", Encoding.UTF8, "text/markdown"),
            }
        });

        var json = await surface.GetFile("o", "n", "README.md", branch: "dev");
        var url = handler.Requests.Single().RequestUri!;
        Assert.EndsWith("/raw/README.md", url.AbsolutePath);
        Assert.Contains("ref=dev", url.Query);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("# Hello\nworld", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("text/markdown", doc.RootElement.GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task EmptyTitle_returns_invalid_request_error()
    {
        var (surface, handler) = Build();
        // CreateIssue validates title before any HTTP call — expect the stable
        // invalid_request code and zero requests sent.
        var json = await surface.CreateIssue("", "n", "   ");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }
}
