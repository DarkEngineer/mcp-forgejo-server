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
/// Tests for the milestone tool surface (issue #20): <c>list_milestones</c>,
/// <c>get_milestone</c>, and <c>create_milestone</c>.
///
/// Live shape verified against the acceptance instance (2026-09-03):
/// milestones carry <c>id</c> (a global id, NOT a repository-local
/// <c>number</c> — the Gitea/Forgejo wire has no <c>number</c> field),
/// <c>title</c>, <c>description</c>, <c>state</c>, <c>open_issues</c>,
/// <c>closed_issues</c>, <c>created_at</c>, <c>updated_at</c>,
/// <c>closed_at</c>, and <c>due_on</c>.
/// <c>GET /repos/{o}/{n}/milestones/{id}</c> looks up by the global
/// <c>id</c> (the API has no repository-local number).
/// <c>POST /repos/{o}/{n}/milestones</c> takes <c>title</c> (required),
/// <c>description</c> (optional), and <c>due_on</c> (optional ISO-8601).
/// </summary>
public class ForgejoMcpMilestoneToolSurfaceTests
{
    /// <summary>A realistic API response for a single milestone entry.</summary>
    private const string FullMilestoneJson = """
        {
          "id": 42,
          "title": "v3.1",
          "description": "Tier-1 milestone",
          "state": "open",
          "open_issues": 3,
          "closed_issues": 1,
          "created_at": "2026-09-03T21:51:13Z",
          "updated_at": "2026-09-03T21:52:00Z",
          "closed_at": null,
          "due_on": "2026-10-01T00:00:00Z"
        }
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
    private static HttpResponseMessage Created(string body)
        => new(StatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Notfound(string message)
        => new(StatusCode.NotFound)
        { Content = new StringContent("{\"message\":\"" + message + "\"}", Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------
    // list_milestones — GET /repos/{o}/{n}/milestones
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListMilestones_returns_array_of_entries_with_all_fields()
    {
        var (surface, handler) = Build(new[] { Ok($"[{FullMilestoneJson}]") });
        var json = await surface.ListMilestones("o", "n");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(arr);
        var e = arr[0];
        Assert.Equal(42, e.GetProperty("id").GetInt32());
        Assert.Equal("v3.1", e.GetProperty("title").GetString());
        Assert.Equal("Tier-1 milestone", e.GetProperty("description").GetString());
        Assert.Equal("open", e.GetProperty("state").GetString());
        Assert.Equal(3, e.GetProperty("open_issues").GetInt32());
        Assert.Equal(1, e.GetProperty("closed_issues").GetInt32());
        Assert.Equal("2026-09-03T21:51:13Z", e.GetProperty("created_at").GetString());
        Assert.True(e.TryGetProperty("due_on", out _));
        Assert.Equal("/api/v1/repos/o/n/milestones", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("GET", handler.Requests.Single().Method.Method);
    }

    [Fact]
    public async Task ListMilestones_empty_array_is_a_valid_result()
    {
        var (surface, _) = Build(new[] { Ok("[]") });
        var json = await surface.ListMilestones("o", "n");
        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task ListMilestones_404_returns_not_found_envelope()
    {
        var (surface, _) = Build(new[] { Notfound("repo not found") });
        var json = await surface.ListMilestones("o", "nofound");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListMilestones_blank_owner_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.ListMilestones("  ", "n");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // get_milestone — GET /repos/{o}/{n}/milestones/{id}
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetMilestone_returns_single_entry_by_global_id()
    {
        var (surface, handler) = Build(new[] { Ok(FullMilestoneJson) });
        var json = await surface.GetMilestone("o", "n", 42);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("v3.1", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("/api/v1/repos/o/n/milestones/42", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("GET", handler.Requests.Single().Method.Method);
    }

    [Fact]
    public async Task GetMilestone_404_returns_not_found_envelope()
    {
        var (surface, _) = Build(new[] { Notfound("milestone not found") });
        var json = await surface.GetMilestone("o", "n", 999);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("999", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetMilestone_id_below_one_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.GetMilestone("o", "n", 0);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // create_milestone — POST /repos/{o}/{n}/milestones
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateMilestone_sends_title_and_optional_fields_and_returns_created()
    {
        var (surface, handler) = Build(new[] { Created(FullMilestoneJson) });
        var json = await surface.CreateMilestone("o", "n",
            title: "v3.1",
            description: "Tier-1 milestone",
            due_on: "2026-10-01T00:00:00Z");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("v3.1", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("2026-10-01T00:00:00Z", doc.RootElement.GetProperty("due_on").GetString());
        Assert.Equal("/api/v1/repos/o/n/milestones", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("POST", handler.Requests.Single().Method.Method);
        // Wire body assertions: the key names must be exactly `title`,
        // `description`, and `due_on` (ISO-8601 as a string, not an object).
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("v3.1", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Tier-1 milestone", body.RootElement.GetProperty("description").GetString());
        Assert.Equal("2026-10-01T00:00:00Z", body.RootElement.GetProperty("due_on").GetString());
    }

    [Fact]
    public async Task CreateMilestone_title_only_sends_sparse_body()
    {
        var (surface, handler) = Build(new[] { Created(FullMilestoneJson) });
        await surface.CreateMilestone("o", "n", title: "title-only");
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("title-only", body.RootElement.GetProperty("title").GetString());
        // description and due_on must be ABSENT when not provided — the
        // sparse-payload contract means we do not send empty/null values.
        Assert.False(body.RootElement.TryGetProperty("description", out _));
        Assert.False(body.RootElement.TryGetProperty("due_on", out _));
    }

    [Fact]
    public async Task CreateMilestone_blank_title_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.CreateMilestone("o", "n", title: "   ");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateMilestone_blank_owner_is_invalid_request_with_no_http()
    {
        var (surface, handler) = Build();
        var json = await surface.CreateMilestone("  ", "n", title: "t");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateMilestone_422_returns_http_422_envelope()
    {
        var (surface, _) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"message\":\"422: invalid due_on\",\"errors\":[{\"field\":\"due_on\"}]}",
                    Encoding.UTF8, "application/json"),
            },
        });
        var json = await surface.CreateMilestone("o", "n", title: "t", due_on: "not-a-date");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("http_422", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
