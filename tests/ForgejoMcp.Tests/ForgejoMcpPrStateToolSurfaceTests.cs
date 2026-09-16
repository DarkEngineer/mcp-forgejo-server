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
/// Tests for the PR-state surface of issue #18:
/// <c>close_pull_request</c> and <c>merge_pull_request</c>.
/// </summary>
public class ForgejoMcpPrStateToolSurfaceTests
{
    private const string ClosedPrJson = """
        { "id": 100, "number": 42, "title": "feat: example", "state": "closed", "merged": false }
        """;

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

    // ---- close_pull_request ---------------------------------------------

    [Fact]
    public async Task ClosePullRequest_sends_patch_state_closed_and_returns_pr()
    {
        var (surface, handler) = Build(new[] { Ok(ClosedPrJson) });
        var json = await surface.ClosePullRequest("o", "n", 42);

        var req = handler.Requests.Single();
        Assert.Equal("/api/v1/repos/o/n/pulls/42", req.RequestUri!.AbsolutePath);
        Assert.Equal("PATCH", req.Method.Method);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("closed", body.RootElement.GetProperty("state").GetString());
        Assert.False(body.RootElement.TryGetProperty("title", out _));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task ClosePullRequest_404_maps_to_not_found_envelope()
    {
        var resp = new HttpResponseMessage(StatusCode.NotFound)
        { Content = new StringContent("{\"message\":\"PR not found\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(new[] { resp });
        var json = await surface.ClosePullRequest("o", "n", 999);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ClosePullRequest_blank_owner_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.ClosePullRequest("  ", "n", 1);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ClosePullRequest_index_below_one_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.ClosePullRequest("o", "n", 0);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ---- merge_pull_request ---------------------------------------------

    [Fact]
    public async Task MergePullRequest_defaults_are_delete_branch_true_and_no_accept_type()
    {
        var (surface, handler) = Build(null);
        handler.Enqueue(StatusCode.OK, "abc123def456");
        var json = await surface.MergePullRequest("o", "n", 42);

        var req = handler.Requests.Single();
        Assert.Equal("/api/v1/repos/o/n/pulls/42/merge", req.RequestUri!.AbsolutePath);
        Assert.Equal("PUT", req.Method.Method);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.True(body.RootElement.GetProperty("delete_branch").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("accept_type", out _));

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("merged").GetBoolean());
    }

    [Theory]
    [InlineData("squash")]
    [InlineData("rebase")]
    [InlineData("fastforward")]
    [InlineData("merge")]
    public async Task MergePullRequest_accept_type_passes_through_wire(string acceptType)
    {
        var (surface, handler) = Build(null);
        handler.Enqueue(StatusCode.OK, "abc123");
        await surface.MergePullRequest("o", "n", 42, accept_type: acceptType, delete_branch: false);

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(acceptType, body.RootElement.GetProperty("accept_type").GetString());
        Assert.False(body.RootElement.GetProperty("delete_branch").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    public async Task MergePullRequest_invalid_accept_type_is_invalid_request_with_no_http(string bad)
    {
        var (surface, handler) = Build();
        await surface.MergePullRequest("o", "n", 42, accept_type: bad);
        Assert.Empty(handler.Requests);
    }
}
