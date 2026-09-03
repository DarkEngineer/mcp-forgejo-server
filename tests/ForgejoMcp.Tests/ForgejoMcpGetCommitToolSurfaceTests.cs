namespace Forgejo.Tests;

using System.Net.Http;
using System.Net;
using System.Text;
using Forgejo.Mcp;
using Forgejo.Client;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// MCP tool-surface tests for <c>get_commit</c>. Backing route on the
/// acceptance instance is the git-data one:
/// <c>GET /repos/{o}/{n}/git/commits/{sha}</c> (the canonical
/// <c>/commits/{sha}</c> route is 404 \u201cpage not found\u201d there).
///
/// Response shape (verified live, git.home.internal 16.0.3+gitea-1.22.0):
/// top-level <c>sha</c> / <c>created</c> / <c>html_url</c> /
/// <c>author</c> / <c>committer</c> / <c>commit{message, author, committer,
/// tree, verification}</c> / <c>parents[]</c> / <c>stats{total, additions,
/// deletions}</c> / <c>files[{filename, status}]</c>. The PGP
/// <c>verification.signature</c> block is dropped by the client model on
/// purpose (see <see cref="GitCommitDetail"/>).
/// </summary>
public class ForgejoMcpGetCommitToolSurfaceTests
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

    /// <summary>Wire body matching the live git-data single-commit shape.</summary>
    private const string CommitWireJson = """
        {
          "url": "https://git.example.com/api/v1/repos/o/n/git/commits/7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
          "sha": "7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
          "created": "2026-09-01T18:59:38",
          "html_url": "https://git.example.com/o/n/commit/7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
          "author": { "id": 3, "login": "alice", "html_url": "https://git.example.com/alice" },
          "committer": { "id": 3, "login": "alice", "html_url": "https://git.example.com/alice" },
          "commit": {
            "message": "chore: bump version\n",
            "author": { "name": "alice", "email": "a@example.com", "date": "2026-09-01T18:59:38" },
            "committer": { "name": "alice", "email": "a@example.com", "date": "2026-09-01T18:59:38" },
            "verification": { "verified": true, "reason": "alice / KEYID", "signature": "-----BEGIN PGP SIGNATURE-----\n...big...\n-----END PGP SIGNATURE-----" }
          },
          "parents": [
            { "url": "https://git.example.com/api/v1/repos/o/n/git/commits/455537f56bde5e4b7e16d9cf3dd3bc5230efb1fb",
              "sha": "455537f56bde5e4b7e16d9cf3dd3bc5230efb1fb", "created": "0001-01-01T00:00:00Z" }
          ],
          "stats": { "total": 504, "additions": 145, "deletions": 359 },
          "files": [ { "filename": "README.md", "status": "modified" } ]
        }
        """;

    // ------------------------------------------------------------------
    // happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetCommit_full_sha_hits_git_data_route_and_returns_commit_document()
    {
        var (surface, handler) = Build(new[] { Ok(CommitWireJson) });

        var json = await surface.GetCommit("o", "n", "7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
            doc.RootElement.GetProperty("sha").GetString());
        Assert.Equal("chore: bump version\n",
            doc.RootElement.GetProperty("commit").GetProperty("message").GetString());
        Assert.Equal(504, doc.RootElement.GetProperty("stats").GetProperty("total").GetInt64());
        Assert.Equal(145, doc.RootElement.GetProperty("stats").GetProperty("additions").GetInt64());
        Assert.Equal(359, doc.RootElement.GetProperty("stats").GetProperty("deletions").GetInt64());
        Assert.Equal(
            "455537f56bde5e4b7e16d9cf3dd3bc5230efb1fb",
            doc.RootElement.GetProperty("parents")[0].GetProperty("sha").GetString());
        Assert.Equal("README.md",
            doc.RootElement.GetProperty("files")[0].GetProperty("filename").GetString());
        Assert.Equal("modified",
            doc.RootElement.GetProperty("files")[0].GetProperty("status").GetString());
        Assert.Equal("alice",
            doc.RootElement.GetProperty("author").GetProperty("login").GetString());
        Assert.Equal("/api/v1/repos/o/n/git/commits/7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
            handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task GetCommit_short_sha_is_accepted_and_echoes_full_sha_in_payload()
    {
        // The instance resolves a short SHA server-side and the response
        // carries the full sha — assert exactly that contract.
        var (surface, handler) = Build(new[] { Ok(CommitWireJson) });

        var json = await surface.GetCommit("o", "n", "7ed6f8a");

        Assert.EndsWith("/git/commits/7ed6f8a", handler.Requests.Single().RequestUri!.AbsolutePath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
            doc.RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task GetCommit_payload_drops_verification_signature()
    {
        // The client model intentionally omits commit.verification so the
        // multi-KB PGP block never lands in an MCP payload.
        var (surface, _) = Build(new[] { Ok(CommitWireJson) });

        var json = await surface.GetCommit("o", "n", "7ed6f8a");

        Assert.DoesNotContain("BEGIN PGP SIGNATURE", json);
        Assert.DoesNotContain("verification", json);
    }

    // ------------------------------------------------------------------
    // validation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("7ed6f8")]            // 6 chars — below the minimum
    [InlineData("7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0ff")] // 42 chars — above the max
    [InlineData("nothex!")]           // wrong character class
    [InlineData("")]                  // empty
    public async Task GetCommit_malformed_sha_fails_fast_with_invalid_request_and_no_http(string sha)
    {
        var (surface, handler) = Build();

        var json = await surface.GetCommit("o", "n", sha);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // error mapping
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetCommit_unknown_sha_returns_not_found_error_payload()
    {
        var (surface, _) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"message\":\"deadbeef0000000000000000000000000000abcd\", \"url\":\"https://git.example.com/api/swagger\", \"errors\":[]}",
                    Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.GetCommit("o", "n", "deadbeef0000000000000000000000000000abcd");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        Assert.Contains("404", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetCommit_401_maps_to_unauthorized()
    {
        var (surface, _) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"user not logged in\"}", Encoding.UTF8, "application/json")
            }
        });

        var json = await surface.GetCommit("o", "n", "7ed6f8a");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("unauthorized",
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
