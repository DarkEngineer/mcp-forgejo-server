namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using Forgejo.Client;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// Client tests for the Tier-1 read endpoints (get_issue, get_pull_request,
/// get_pull_request_files / diff, list_releases, list_file_tree) and the
/// pagination-metadata pipeline (x-total-count / Link rel="next" /
/// full-page heuristic) that feeds <c>ListResult.NextPageHint</c> (serialized
/// as <c>next_page</c>).
/// </summary>
public class ForgejoClientTier1ReadTests
{
    private static TestHttpHandler Enqueue(StatusCode status, string body,
        string contentType = "application/json")
    {
        var h = new TestHttpHandler();
        h.Enqueue(status, body, contentType);
        return h;
    }

    private static ForgejoClient ClientFor(TestHttpHandler handler, int maxRetries = 0)
        => new(new Uri("https://git.example.com"), ForgejoCredentials.Anonymous(),
               maxRetries, TimeSpan.Zero, new HttpClient(handler));

    // ------------------------------------------------------------------
    // get_issue — GET /repos/{o}/{n}/issues/{index}
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetIssue_returns_the_issue_document()
    {
        var handler = Enqueue(StatusCode.OK, """
        {
          "id": 123,
          "number": 7,
          "title": "Broken build",
          "body": "It exploded",
          "state": "open",
          "labels": [{"id": 1, "name": "bug"}],
          "assignees": [{"login": "alice"}],
          "milestone": {"id": 2, "title": "v1.0"},
          "created_at": "2026-01-02T03:04:05Z",
          "updated_at": "2026-01-03T03:04:05Z",
          "html_url": "https://git.example.com/o/n/issues/7"
        }
        """);
        using var client = ClientFor(handler);

        var issue = await client.GetIssueAsync("o", "n", 7);

        Assert.Equal(7, issue.Number);
        Assert.Equal("Broken build", issue.Title);
        Assert.Equal("It exploded", issue.Body);
        Assert.Equal("open", issue.State);
        Assert.Equal("bug", issue.Labels.Single().Name);
        Assert.Equal("alice", issue.Assignees.Single().Login);
        Assert.Equal("v1.0", issue.Milestone!.Title);
        Assert.Equal("https://git.example.com/o/n/issues/7", issue.HtmlUrl);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/issues/7", url.AbsolutePath);
    }

    [Fact]
    public async Task GetIssue_404_maps_to_forgejo_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"not found"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetIssueAsync("o", "n", 999));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("issues/999", ex.Message);
    }

    [Fact]
    public async Task GetIssue_rejects_invalid_index()
    {
        var handler = Enqueue(StatusCode.OK, "{}");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetIssueAsync("o", "n", 0));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // get_pull_request — GET /repos/{o}/{n}/pulls/{index}
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetPullRequest_returns_pr_with_base_head_and_merged()
    {
        var handler = Enqueue(StatusCode.OK, """
        {
          "id": 456,
          "number": 11,
          "title": "Add feature",
          "body": "details",
          "state": "closed",
          "merged": true,
          "merged_at": "2026-02-02T00:00:00Z",
          "base": {"label": "o:main", "ref": "main"},
          "head": {"label": "alice:feat", "ref": "feat"},
          "comments": 5,
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-02-01T00:00:00Z",
          "html_url": "https://git.example.com/o/n/pulls/11"
        }
        """);
        using var client = ClientFor(handler);

        var pr = await client.GetPullRequestAsync("o", "n", 11);

        Assert.Equal(11, pr.Number);
        Assert.Equal("Add feature", pr.Title);
        Assert.True(pr.Merged);
        Assert.Equal("main", pr.Base!.Ref);
        Assert.Equal("feat", pr.Head!.Ref);
        Assert.Equal(5, pr.Comments);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/pulls/11", url.AbsolutePath);
    }

    [Fact]
    public async Task GetPullRequest_404_maps_to_forgejo_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"missing"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetPullRequestAsync("o", "n", 42));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task GetPullRequest_rejects_invalid_index()
    {
        var handler = Enqueue(StatusCode.OK, "{}");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetPullRequestAsync("o", "n", -1));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // get_pull_request_files — GET /repos/{o}/{n}/pulls/{index}/files
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetPullRequestFiles_parses_files_with_patch_null_supported()
    {
        var handler = Enqueue(StatusCode.OK, """
        [
          {"filename":"src/A.cs","status":"modified","additions":3,"deletions":1,"changes":5,"patch":"@@ -1 +1 @@\n-old\n+new"},
          {"filename":"src/B.cs","status":"added","additions":10,"deletions":0,"changes":10,"patch":null}
        ]
        """);
        using var client = ClientFor(handler);

        var files = await client.GetPullRequestFilesAsync("o", "n", 11);

        Assert.Equal(2, files.Count);
        Assert.Equal("src/A.cs", files[0].Filename);
        Assert.Equal("modified", files[0].Status);
        Assert.Equal(3, files[0].Additions);
        Assert.Equal(1, files[0].Deletions);
        Assert.Equal(5, files[0].Changes);
        Assert.Contains("+new", files[0].Patch!);
        // Patch omitted/null in the wire document must deserialize as null.
        Assert.Null(files[1].Patch);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/pulls/11/files", url.AbsolutePath);
    }

    [Fact]
    public async Task GetPullRequestFiles_404_maps_to_forgejo_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"nope"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetPullRequestFilesAsync("o", "n", 77));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task GetPullRequestFiles_rejects_invalid_index()
    {
        var handler = Enqueue(StatusCode.OK, "[]");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetPullRequestFilesAsync("o", "n", 0));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // get_pull_request_diff — GET /repos/{o}/{n}/pulls/{index}.diff
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetPullRequestDiff_returns_raw_diff_text()
    {
        var diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var handler = Enqueue(StatusCode.OK, diff, "text/plain");
        using var client = ClientFor(handler);

        var body = await client.GetPullRequestDiffAsync("o", "n", 11);

        Assert.Equal(diff, body);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/pulls/11.diff", url.AbsolutePath);
    }

    [Fact]
    public async Task GetPullRequestDiff_404_maps_to_forgejo_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, "nope", "text/plain");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetPullRequestDiffAsync("o", "n", 33));
        Assert.Equal(404, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // list_releases — GET /repos/{o}/{n}/releases
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListReleases_returns_release_list_with_assets()
    {
        var handler = Enqueue(StatusCode.OK, """
        [
          {
            "id": 5,
            "tag_name": "v1.0.0",
            "name": "First release",
            "body": "Notes",
            "draft": false,
            "prerelease": false,
            "created_at": "2026-01-01T00:00:00Z",
            "published_at": "2026-01-01T00:00:00Z",
            "html_url": "https://git.example.com/o/n/releases/v1.0.0",
            "assets": [
              {"id": 1, "name": "pkg.tar.gz", "size": 12345,
               "url": "https://git.example.com/o/n/releases/v1.0.0/pkg.tar.gz"}
            ]
          }
        ]
        """);
        using var client = ClientFor(handler);

        var list = await client.ListReleasesAsync("o", "n",
            new Page { Number = 1, Size = 2 });

        Assert.Single(list.Items);
        var rel = list.Items[0];
        Assert.Equal("v1.0.0", rel.TagName);
        Assert.Equal("First release", rel.Name);
        Assert.Equal("Notes", rel.Body);
        Assert.False(rel.Draft);
        Assert.Single(rel.Assets);
        Assert.Equal("pkg.tar.gz", rel.Assets[0].Name);
        Assert.Equal(12345, rel.Assets[0].Size);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/releases", url.AbsolutePath);
        Assert.Contains("page=1&limit=2", url.Query);
    }

    [Fact]
    public async Task ListReleases_404_maps_to_forgejo_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"missing"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.ListReleasesAsync("o", "n"));
        Assert.Equal(404, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // list_file_tree — GET /repos/{o}/{n}/contents/{path}
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListFileTree_root_and_nested_paths()
    {
        var handler = new TestHttpHandler();
        handler.Enqueue(StatusCode.OK, """
        [
          {"name":"README.md","type":"file","path":"README.md",
           "sha":"abc123","size":256,"html_url":"https://git.example.com/o/n/README.md"},
          {"name":"src","type":"dir","path":"src","sha":"def456","size":0}
        ]
        """, "application/json");
        handler.Enqueue(StatusCode.OK, """
        [{"name":"A.cs","type":"file","path":"src/A.cs","sha":"111","size":10}]
        """, "application/json");
        using var client = ClientFor(handler);

        var root = await client.ListFileTreeAsync("o", "n");
        Assert.Equal(2, root.Count);
        Assert.Equal("README.md", root[0].Name);
        Assert.Equal("file", root[0].Type);
        Assert.Equal("abc123", root[0].Sha);
        Assert.Equal(256, root[0].Size);
        Assert.Equal("src", root[1].Name);
        Assert.Equal("dir", root[1].Type);

        var src = await client.ListFileTreeAsync("o", "n", "src", branch: "dev");
        Assert.Single(src);
        Assert.Equal("src/A.cs", src[0].Path);

        Assert.Equal("/api/v1/repos/o/n/contents/",
            handler.Requests[0].RequestUri!.AbsolutePath);
        var nested = handler.Requests[1].RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/contents/src", nested.AbsolutePath);
        Assert.Contains("ref=dev", nested.Query);
    }

    [Fact]
    public async Task ListFileTree_404_maps_to_forgejo_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"no such dir"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.ListFileTreeAsync("o", "n", "does/not/exist"));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task ListFileTree_rejects_empty_owner()
    {
        var handler = Enqueue(StatusCode.OK, "[]");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ListFileTreeAsync("", "n"));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // Pagination metadata: next_page (Link / full-page heuristic / short page)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Short_page_yields_null_next_page()
    {
        // Two items returned against limit 3 -> last page, next_page must be null.
        var handler = Enqueue(StatusCode.OK,
            """[{"id":1},{"id":2}]""");
        using var client = ClientFor(handler);

        var list = await client.ListRepositoriesAsync(new Page { Number = 1, Size = 3 });
        Assert.Equal(2, list.Count);
        Assert.Null(list.NextPageHint);
    }

    [Fact]
    public async Task Full_page_yields_next_page_plus_one()
    {
        // Three items returned against limit 3 -> may be more, next_page = 2.
        var handler = Enqueue(StatusCode.OK,
            """[{"id":1},{"id":2},{"id":3}]""");
        using var client = ClientFor(handler);

        var list = await client.ListRepositoriesAsync(new Page { Number = 1, Size = 3 });
        Assert.Equal(3, list.Count);
        Assert.Equal(2, list.NextPageHint);
    }

    [Fact]
    public async Task Link_rel_next_header_wins_over_heuristic()
    {
        // Full page (limit 2 returned) would heuristically say next_page=2,
        // but the Link header explicitly points at page 5 — header must win.
        var handler = new TestHttpHandler();
        handler.EnqueueWithHeaders(
            StatusCode.OK,
            """[{"id":1},{"id":2}]""",
            "application/json",
            ("Link", "<https://git.example.com/api/v1/user/repos?page=5&limit=2>; rel=\"next\""));
        using var client = ClientFor(handler);

        var list = await client.ListRepositoriesAsync(new Page { Number = 4, Size = 2 });
        Assert.Equal(5, list.NextPageHint);
    }

    [Fact]
    public async Task Link_with_only_prev_does_not_fabricate_next()
    {
        // A `rel="prev"`-only Link means the caller IS on the first page with
        // a full page — the heuristic still yields page+1.
        var handler = new TestHttpHandler();
        handler.EnqueueWithHeaders(
            StatusCode.OK,
            """[{"id":1},{"id":2}]""",
            "application/json",
            ("Link", "<https://git.example.com/api/v1/user/repos?page=1&limit=2>; rel=\"prev\""));
        using var client = ClientFor(handler);

        var list = await client.ListRepositoriesAsync(new Page { Number = 2, Size = 2 });
        Assert.Equal(3, list.NextPageHint);
    }

    [Fact]
    public async Task XTotalCount_header_populates_total()
    {
        var handler = new TestHttpHandler();
        handler.EnqueueWithHeaders(
            StatusCode.OK,
            """[{"id":1}]""",
            "application/json",
            ("x-total-count", "42"));
        using var client = ClientFor(handler);

        var list = await client.ListRepositoriesAsync(new Page { Number = 1, Size = 5 });
        Assert.Equal(42, list.Total);
        Assert.Null(list.NextPageHint); // short page -> no successor.
    }

    [Fact]
    public async Task No_paging_headers_and_short_page_yields_nulls()
    {
        var handler = Enqueue(StatusCode.OK, """[{"id":1}]""");
        using var client = ClientFor(handler);

        var list = await client.ListRepositoriesAsync(new Page { Number = 1, Size = 5 });
        Assert.Null(list.Total);
        Assert.Null(list.NextPageHint);
    }

    // ------------------------------------------------------------------
    // DTOs serialize the expected fields (SnakeCaseNamingPolicy round-trip)
    // ------------------------------------------------------------------

    [Fact]
    public void PullRequestFile_serializes_expected_snake_case_fields()
    {
        var f = new PullRequestFile
        {
            Filename = "src/A.cs",
            Status = "modified",
            Additions = 3,
            Deletions = 1,
            Changes = 4,
            Patch = "@@ -1 +1 @@",
        };
        var json = ForgejoJson.ToJson(f);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("src/A.cs", doc.RootElement.GetProperty("filename").GetString());
        Assert.Equal("modified", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("additions").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("deletions").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("changes").GetInt32());
        Assert.Contains("@@", doc.RootElement.GetProperty("patch").GetString());

        // A null Patch is dropped from the outgoing document (per
        // ForgejoJson.Default's WhenWritingNull) — and stays null on read.
        var bare = new PullRequestFile { Filename = "B.cs" };
        var bareJson = ForgejoJson.ToJson(bare);
        using var bareDoc = System.Text.Json.JsonDocument.Parse(bareJson);
        Assert.False(bareDoc.RootElement.TryGetProperty("patch", out _));
        Assert.Null(bare.Patch);
    }

    [Fact]
    public void Release_and_release_asset_round_trip_fields()
    {
        var r = new Release
        {
            Id = 9,
            TagName = "v1.0",
            Name = "One",
            Body = "Notes",
            Draft = false,
            Prerelease = false,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PublishedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            HtmlUrl = "https://git.example.com/o/n/releases/v1.0",
            Assets = new List<ReleaseAsset>
            {
                new() { Id = 1, Name = "pkg.tar.gz", Size = 2048,
                        Url = "https://git.example.com/o/n/releases/v1.0/pkg.tar.gz" },
            },
        };
        var json = ForgejoJson.ToJson(r);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("v1.0", doc.RootElement.GetProperty("tag_name").GetString());
        Assert.Equal("One", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("Notes", doc.RootElement.GetProperty("body").GetString());
        Assert.False(doc.RootElement.GetProperty("draft").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("prerelease").GetBoolean());
        Assert.Equal("pkg.tar.gz",
            doc.RootElement.GetProperty("assets")[0].GetProperty("name").GetString());
        Assert.Equal(2048,
            doc.RootElement.GetProperty("assets")[0].GetProperty("size").GetInt32());

        // The deserializer must accept the wire shape and land it on the
        // right C# properties.
        var read = ForgejoJson.FromJson<Release>(json)!;
        Assert.Equal(r.TagName, read.TagName);
        Assert.Equal(r.Body, read.Body);
        Assert.Single(read.Assets);
        Assert.Equal("pkg.tar.gz", read.Assets[0].Name);
    }

    [Fact]
    public void ContentEntry_round_trip_fields()
    {
        var e = new ContentEntry
        {
            Name = "src",
            Type = "dir",
            Path = "src",
            Sha = "abc123",
            Size = 0,
            HtmlUrl = "https://git.example.com/o/n/src",
        };
        var json = ForgejoJson.ToJson(e);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("src", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("dir", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("src", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal("abc123", doc.RootElement.GetProperty("sha").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("size").GetInt32());

        var read = ForgejoJson.FromJson<ContentEntry>(json)!;
        Assert.Equal("dir", read.Type);
        Assert.Equal("abc123", read.Sha);
    }

    [Fact]
    public void ListResult_next_page_serializes_as_next_page_and_stays_present_when_null()
    {
        var withNext = new ListResult<Repository>(
            new List<Repository> { new() { Id = 1 } }, 42)
        {
            NextPageHint = 3,
        };
        var json = ForgejoJson.ToJson(withNext);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("next_page").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());

        var withoutNext = new ListResult<Repository>(
            new List<Repository> { new() { Id = 1 } }, null);
        var bare = ForgejoJson.ToJson(withoutNext);
        using var bareDoc = System.Text.Json.JsonDocument.Parse(bare);
        // Contract: `next_page` is ALWAYS present — null when exhausted —
        // while `total` stays droppable when the instance never reports it.
        Assert.True(
            bareDoc.RootElement.GetProperty("next_page").ValueKind
            == System.Text.Json.JsonValueKind.Null);
        Assert.False(bareDoc.RootElement.TryGetProperty("total", out _));
    }
}
