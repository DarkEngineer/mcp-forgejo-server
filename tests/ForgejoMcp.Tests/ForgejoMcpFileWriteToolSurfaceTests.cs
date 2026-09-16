// SPDX-License-Identifier: MIT

namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Forgejo.Client;
using Forgejo.Mcp;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// Tests for the file-write surface of issue #19:
/// <c>edit_file</c> (create via POST / update via PUT) and
/// <c>delete_file</c> (DELETE), plus the <c>get_file_contents</c> read path
/// that supplies the content-blob <c>sha</c> both mutations need.
///
/// Surface return shapes (record names in the client assembly):
/// - <see cref="FileContents"/> — sha / name / path / size / html_url / content (decoded)
/// - <see cref="FileWriteResult"/> — commit_sha / commit_message / commit_html_url / file
/// </summary>
public class ForgejoMcpFileWriteToolSurfaceTests
{
    // ------------------------------------------------------------------
    // Raw wire responses — what the <c>contents</c> endpoint sends.
    // The content field is base64 on the wire in all three cases.
    //
    // We build these with System.Text.Json so the JSON is well-formed by
    // construction (no hand-escaped quotes / braces to trip on).
    // ------------------------------------------------------------------

    private static string WireFileResponse(string path, string sha, string utf8Content)
    {
        var content = utf8Content is null ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(utf8Content));
        var size = utf8Content is null ? 0L : Encoding.UTF8.GetByteCount(utf8Content);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = PathLeaf(path),
            ["path"] = path,
            ["sha"] = sha,
            ["size"] = size,
            ["content"] = content,
            ["html_url"] = $"https://git.example.com/o/n/src/branch/main/{path}",
        });
    }

    private static string WireMutationResponse(string commitSha, string commitMsg, string path, string contentSha, string utf8Content)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(utf8Content));
        var size = Encoding.UTF8.GetByteCount(utf8Content);
        var content = new Dictionary<string, object?>
        {
            ["name"] = PathLeaf(path),
            ["path"] = path,
            ["sha"] = contentSha,
            ["size"] = size,
            ["content"] = b64,
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["commit"] = new Dictionary<string, object?>
            {
                ["sha"] = commitSha,
                ["message"] = commitMsg,
                ["html_url"] = $"https://git.example.com/o/n/commit/{commitSha}",
            },
            ["content"] = content,
        });
    }

    private static string WireDeleteResponse(string commitSha, string commitMsg)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["commit"] = new Dictionary<string, object?>
            {
                ["sha"] = commitSha,
                ["message"] = commitMsg,
                ["html_url"] = $"https://git.example.com/o/n/commit/{commitSha}",
            },
        });

    private static string PathLeaf(string p) => p.Split('/').Last();

    private static (ForgejoMcpToolSurface Surface, TestHttpHandler Handler) Build(
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

    // ---- get_file_contents --------

    [Fact]
    public async Task GetFileContents_reads_file_and_decodes_base64_content()
    {
        var (surface, handler) = Build(responses: new[] { Ok(WireFileResponse("README.md", "abc123", "hello world")) });
        var json = await surface.GetFileContents("o", "n", "README.md");

        var req = handler.Requests.Single();
        Assert.Equal("GET", req.Method.Method);
        Assert.Equal("/api/v1/repos/o/n/contents/README.md", req.RequestUri!.AbsolutePath);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("abc123", root.GetProperty("sha").GetString());
        Assert.Equal("README.md", root.GetProperty("name").GetString());
        Assert.Equal("hello world", root.GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetFileContents_sends_ref_query_when_branch_is_set()
    {
        var (surface, handler) = Build(responses: new[] { Ok(WireFileResponse("README.md", "abc123", "x")) });
        await surface.GetFileContents("o", "n", "README.md", branch: "develop");
        Assert.Contains("ref=develop", handler.Requests.Single().RequestUri!.Query);
    }

    // ---- edit_file: create (POST) --------

    [Fact]
    public async Task EditFile_create_uses_post_and_base64_content_and_default_message()
    {
        var (surface, handler) = Build(responses: new[] { Ok(WireMutationResponse("c1", "Add docs/a.md", "docs/a.md", "b2", "content here")) });
        var json = await surface.EditFile("o", "n", "docs/a.md", content: "content here");

        var req = handler.Requests.Single();
        Assert.Equal("POST", req.Method.Method);
        Assert.Equal("/api/v1/repos/o/n/contents/docs/a.md", req.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("Add docs/a.md", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("content here")),
            body.RootElement.GetProperty("content").GetString());
        Assert.False(body.RootElement.TryGetProperty("sha", out _));
        Assert.False(body.RootElement.TryGetProperty("branch", out _));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("c1", doc.RootElement.GetProperty("commit_sha").GetString());
        Assert.Equal("content here", doc.RootElement.GetProperty("file").GetProperty("content").GetString());
        Assert.Equal("b2", doc.RootElement.GetProperty("file").GetProperty("sha").GetString());
    }

    [Fact]
    public async Task EditFile_create_blank_owner_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.EditFile("  ", "n", "a.md", content: "x");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EditFile_create_blank_path_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.EditFile("o", "n", "   ", content: "x");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ---- edit_file: update (PUT) --------

    [Fact]
    public async Task EditFile_update_uses_put_and_sends_sha_and_default_update_message()
    {
        var (surface, handler) = Build(responses: new[] { Ok(WireMutationResponse("c2", "Update README.md", "README.md", "b3", "v2 content")) });
        var json = await surface.EditFile("o", "n", "README.md", content: "v2 content", sha: "b2", branch: "main");

        var req = handler.Requests.Single();
        Assert.Equal("PUT", req.Method.Method);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("Update README.md", body.RootElement.GetProperty("message").GetString());
        Assert.Equal("b2", body.RootElement.GetProperty("sha").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("branch").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("v2 content")),
            body.RootElement.GetProperty("content").GetString());
        Assert.Equal("v2 content", json.Length > 0
            ? JsonDocument.Parse(json).RootElement.GetProperty("file").GetProperty("content").GetString()
            : "");
    }

    [Fact]
    public async Task EditFile_409_maps_to_conflict_envelope()
    {
        var resp = new HttpResponseMessage(StatusCode.Conflict)
        { Content = new StringContent("{\"message\":\"sha mismatch\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(responses: new[] { resp });
        var json = await surface.EditFile("o", "n", "README.md", content: "x", sha: "stale");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("conflict", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task EditFile_custom_commit_message_is_sent_verbatim()
    {
        var (surface, handler) = Build(responses: new[] { Ok(WireMutationResponse("c9", "chore: tweak", "a.md", "b9", "x")) });
        await surface.EditFile("o", "n", "a.md", content: "x", commit_message: "chore: tweak");
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("chore: tweak", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task EditFile_update_404_when_path_never_exists_is_not_found()
    {
        var resp = new HttpResponseMessage(StatusCode.NotFound)
        { Content = new StringContent("{\"message\":\"file does not exist\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(responses: new[] { resp });
        var json = await surface.EditFile("o", "n", "ghost.md", content: "x", sha: "stale");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- delete_file --------

    [Fact]
    public async Task DeleteFile_uses_delete_with_sha_and_default_delete_message()
    {
        var (surface, handler) = Build(responses: new[] { Ok(WireDeleteResponse("d1", "Delete a.md")) });
        var json = await surface.DeleteFile("o", "n", "a.md", sha: "b2");

        var req = handler.Requests.Single();
        Assert.Equal("DELETE", req.Method.Method);
        Assert.Equal("/api/v1/repos/o/n/contents/a.md", req.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("Delete a.md", body.RootElement.GetProperty("message").GetString());
        Assert.Equal("b2", body.RootElement.GetProperty("sha").GetString());
        Assert.False(body.RootElement.TryGetProperty("content", out _));
        Assert.False(body.RootElement.TryGetProperty("branch", out _));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("d1", doc.RootElement.GetProperty("commit_sha").GetString());
    }

    [Fact]
    public async Task DeleteFile_blank_sha_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.DeleteFile("o", "n", "a.md", sha: "   ");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DeleteFile_404_maps_to_not_found_envelope()
    {
        var resp = new HttpResponseMessage(StatusCode.NotFound)
        { Content = new StringContent("{\"message\":\"file not found\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(responses: new[] { resp });
        var json = await surface.DeleteFile("o", "n", "gone.md", sha: "b2");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeleteFile_409_for_stale_sha_maps_to_conflict_envelope()
    {
        var resp = new HttpResponseMessage(StatusCode.Conflict)
        { Content = new StringContent("{\"message\":\"sha mismatch\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(responses: new[] { resp });
        var json = await surface.DeleteFile("o", "n", "stale.md", sha: "stale");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("conflict", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeleteFile_custom_commit_message_is_sent_verbatim()
    {
        var (surface, handler) = Build(responses: new[] { Ok(WireDeleteResponse("d9", "chore: drop")) });
        await surface.DeleteFile("o", "n", "a.md", sha: "b2", commit_message: "chore: drop");
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("chore: drop", body.RootElement.GetProperty("message").GetString());
    }
}
