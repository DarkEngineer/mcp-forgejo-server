namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using Forgejo.Mcp;
using Forgejo.Client;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// MCP tool-surface tests for the Tier-1 read tools: <c>get_issue</c>,
/// <c>get_pull_request</c>, <c>get_pull_request_files</c>,
/// <c>list_releases</c>, <c>list_file_tree</c> — plus the
/// <c>next_page</c> envelope shape on the existing list tools.
/// Tools never throw for API-level failures; they emit the
/// <c>{"error":{...}}</c> JSON payload.
/// </summary>
public class ForgejoMcpTier1ToolSurfaceTests
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

    private static HttpResponseMessage Ok(string body, string contentType = "application/json")
        => new(StatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static HttpResponseMessage Not_found(string message = "no such thing")
        => new(StatusCode.NotFound)
        {
            Content = new StringContent(
                "{\"message\":\"" + message + "\"}", Encoding.UTF8, "application/json"),
        };

    // ------------------------------------------------------------------
    // get_issue
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetIssue_returns_issue_document_snake_case()
    {
        var (surface, handler) = Build(new[] { Ok("""
            {
              "id": 88, "number": 3, "title": "Bug", "body": "oops",
              "state": "open", "html_url": "https://git.example.com/o/n/issues/3"
            }
            """) });

        var json = await surface.GetIssue("o", "n", 3);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(88, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("number").GetInt32());
        Assert.Equal("Bug", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("open", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("/api/v1/repos/o/n/issues/3",
            handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetIssue_404_returns_not_found_error_payload()
    {
        var (surface, _) = Build(new[] { Not_found() });

        var json = await surface.GetIssue("o", "n", 99);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        Assert.Contains("404", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetIssue_invalid_index_short_circuits_before_http()
    {
        var (surface, handler) = Build();

        var json = await surface.GetIssue("o", "n", 0);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // get_pull_request
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetPullRequest_returns_pr_envelope()
    {
        var (surface, handler) = Build(new[] { Ok("""
            {
              "id": 90, "number": 12, "title": "PR", "state": "open",
              "merged": false, "base": {"ref": "main"}, "head": {"ref": "dev"},
              "comments": 2, "html_url": "https://git.example.com/o/n/pulls/12"
            }
            """) });

        var json = await surface.GetPullRequest("o", "n", 12);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(90, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(12, doc.RootElement.GetProperty("number").GetInt32());
        Assert.False(doc.RootElement.GetProperty("merged").GetBoolean());
        Assert.Equal("main", doc.RootElement.GetProperty("base").GetProperty("ref").GetString());
        Assert.Equal("dev", doc.RootElement.GetProperty("head").GetProperty("ref").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("comments").GetInt32());
        Assert.Equal("/api/v1/repos/o/n/pulls/12",
            handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetPullRequest_404_returns_error_payload()
    {
        var (surface, _) = Build(new[] { Not_found() });

        var json = await surface.GetPullRequest("o", "n", 55);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------
    // get_pull_request_files (files + optional combined diff)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetPullRequestFiles_without_diff_fetches_only_files_endpoint()
    {
        var (surface, handler) = Build(new[] { Ok("""
            [{"filename":"a.txt","status":"modified","additions":1,"deletions":0,"changes":1,"patch":null}]
            """) });

        var json = await surface.GetPullRequestFiles("o", "n", 12);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal("a.txt", files[0].GetProperty("filename").GetString());
        Assert.Equal(1, files[0].GetProperty("additions").GetInt32());
        Assert.Equal(0, files[0].GetProperty("deletions").GetInt32());
        Assert.Equal(1, files[0].GetProperty("changes").GetInt32());
        // ForgejoJson drops null-valued properties (WhenWritingNull), so a
        // null patch is ABSENT from the document, and `diff` is only emitted
        // when show_diff is on.
        Assert.False(files[0].TryGetProperty("patch", out _));
        Assert.False(doc.RootElement.TryGetProperty("diff", out _));
        // Without show_diff exactly ONE request is made (the files one).
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/pulls/12/files", url.AbsolutePath);
    }

    [Fact]
    public async Task GetPullRequestFiles_with_show_diff_makes_two_calls_and_returns_diff()
    {
        var (surface, handler) = Build(new[]
        {
            Ok("""[{"filename":"a.txt","status":"modified","additions":2,"deletions":1,"changes":3,"patch":null}]"""),
            Ok("diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1,2 @@\n-old\n+new1\n+new2\n", "text/plain"),
        });

        var json = await surface.GetPullRequestFiles("o", "n", 12, show_diff: true);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("files").GetArrayLength());
        Assert.Contains("+new1", doc.RootElement.GetProperty("diff").GetString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v1/repos/o/n/pulls/12/files", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/api/v1/repos/o/n/pulls/12.diff", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetPullRequestFiles_404_returns_error_payload()
    {
        var (surface, _) = Build(new[] { Not_found() });

        var json = await surface.GetPullRequestFiles("o", "n", 13);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetPullRequestFiles_invalid_index_short_circuits()
    {
        var (surface, handler) = Build();

        var json = await surface.GetPullRequestFiles("o", "n", -3);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // list_releases
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListReleases_returns_envelope_with_releases()
    {
        var (surface, handler) = Build(new[] { Ok("""
            [
              {"id":5,"tag_name":"v1.2.0","name":"Stable","body":"changelog",
               "draft":false,"prerelease":false,
               "created_at":"2026-01-01T00:00:00Z",
               "published_at":"2026-01-01T00:00:00Z",
               "html_url":"https://git.example.com/o/n/releases/v1.2.0",
               "assets":[{"id":1,"name":"bin.tar.gz","size":100,
                          "url":"https://git.example.com/o/n/releases/v1.2.0/bin.tar.gz"}]}
            ]
            """) });

        var json = await surface.ListReleases("o", "n", page: 1, limit: 20);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal("v1.2.0", items[0].GetProperty("tag_name").GetString());
        Assert.Equal("Stable", items[0].GetProperty("name").GetString());
        Assert.Equal("bin.tar.gz", items[0].GetProperty("assets")[0].GetProperty("name").GetString());
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/releases", url.AbsolutePath);
        Assert.Contains("page=1&limit=20", url.Query);
    }

    [Fact]
    public async Task ListReleases_404_returns_error_payload()
    {
        var (surface, _) = Build(new[] { Not_found() });

        var json = await surface.ListReleases("o", "n");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------
    // list_file_tree
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListFileTree_root_returns_path_and_entries()
    {
        var (surface, handler) = Build(new[] { Ok("""
            [
              {"name":"README.md","type":"file","path":"README.md","sha":"a1","size":10},
              {"name":"src","type":"dir","path":"src","sha":"b2","size":0}
            ]
            """) });

        var json = await surface.ListFileTree("o", "n");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("", doc.RootElement.GetProperty("path").GetString());
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("README.md", entries[0].GetProperty("name").GetString());
        Assert.Equal("file", entries[0].GetProperty("type").GetString());
        Assert.Equal("a1", entries[0].GetProperty("sha").GetString());
        Assert.Equal(10, entries[0].GetProperty("size").GetInt32());
        Assert.Equal("dir", entries[1].GetProperty("type").GetString());
        Assert.Equal("/api/v1/repos/o/n/contents/",
            handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListFileTree_subdir_and_branch_query()
    {
        var (surface, handler) = Build(new[] { Ok("""
            [{"name":"A.cs","type":"file","path":"src/a/A.cs","sha":"c3","size":7}]
            """) });

        var json = await surface.ListFileTree("o", "n", path: "src/a", branch: "dev");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("src/a", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal("src/a/A.cs", doc.RootElement.GetProperty("entries")[0].GetProperty("path").GetString());
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/contents/src/a", url.AbsolutePath);
        Assert.Contains("ref=dev", url.Query);
    }

    [Fact]
    public async Task ListFileTree_404_returns_error_payload()
    {
        var (surface, _) = Build(new[] { Not_found("no such directory") });

        var json = await surface.ListFileTree("o", "n", path: "missing");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------
    // next_page envelope shape on the existing list tools
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListIssues_short_page_emits_null_next_page()
    {
        var (surface, _) = Build(new[] { Ok("""
            [{"id":1,"number":1,"title":"a","state":"open"},{"id":2,"number":2,"title":"b","state":"open"}]
            """) });

        var json = await surface.ListIssues("o", "n", state: "all", page: 1, limit: 50);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        // 2 < 50 (limit) -> short page -> no successor. The pagination contract
        // keeps the property present and null (a dropped key would not tell the
        // caller the difference between "no more pages" and "unpaged tool").
        var np = doc.RootElement.GetProperty("next_page");
        Assert.True(np.ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task ListPullRequests_full_page_emits_next_page()
    {
        var body = "[" + string.Join(",",
            Enumerable.Range(1, 5).Select(i => "{\"id\":" + i + ",\"number\":" + i + ",\"title\":\"t\"}")) + "]";
        var (surface, _) = Build(new[] { Ok(body) });

        var json = await surface.ListPullRequests("o", "n", state: "all", page: 1, limit: 5);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("next_page").GetInt32());
    }

    [Fact]
    public async Task ListRepos_link_header_drives_next_page()
    {
        var handler = new TestHttpHandler();
        handler.EnqueueWithHeaders(
            StatusCode.OK,
            """[{"id":1,"name":"a"},{"id":2,"name":"b"},{"id":3,"name":"c"},{"id":4,"name":"d"},{"id":5,"name":"e"}]""",
            "application/json",
            ("Link", "<https://git.example.com/api/v1/user/repos?page=7&limit=5>; rel=\"next\""));
        var client = new ForgejoClient(
            new Uri("https://git.example.com"), ForgejoCredentials.Anonymous(),
            retryBaseDelay: TimeSpan.Zero, httpClient: new HttpClient(handler));
        var surface = new ForgejoMcpToolSurface(client);

        var json = await surface.ListRepos(page: 6, limit: 5);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(7, doc.RootElement.GetProperty("next_page").GetInt32());
    }

    [Fact]
    public async Task ListCommits_full_page_emits_next_page()
    {
        var body = "[" + string.Join(",",
            Enumerable.Range(1, 3).Select(i => Commit(i))) + "]";

        static string Commit(int i)
        {
            var author = "{\"name\":\"a\",\"email\":\"a@x\"}";
            return "{\"sha\":\"sha" + i +
                   "\",\"commit\":{\"message\":\"m" + i +
                   "\",\"author\":" + author +
                   ",\"committer\":" + author + "}," +
                   "\"author\":{\"login\":\"a\"}}";
        }
        var (surface, _) = Build(new[] { Ok(body) });

        var json = await surface.ListCommits("o", "n", page: 2, limit: 3);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("next_page").GetInt32());
    }
}
