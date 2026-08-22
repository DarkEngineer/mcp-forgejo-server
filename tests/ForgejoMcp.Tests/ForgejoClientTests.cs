namespace Forgejo.Tests;

using System.Net.Http;
using Forgejo.Client;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

public class ForgejoClientTests
{
    private static TestHttpHandler Enqueue(StatusCode status, string body, string contentType = "application/json")
    {
        var h = new TestHttpHandler();
        h.Enqueue(status, body, contentType);
        return h;
    }

    private static ForgejoClient ClientFor(TestHttpHandler handler, int maxRetries = 3)
        => new(new Uri("https://git.example.com"), ForgejoCredentials.Anonymous(),
               maxRetries, TimeSpan.Zero, new HttpClient(handler));

    [Fact]
    public async Task Token_auth_applies_token_header()
    {
        var handler = Enqueue(StatusCode.OK, """{"id":1,"full_name":"owner/repo","name":"repo"}""");
        using var client = new ForgejoClient(
            new Uri("https://git.example.com"),
            ForgejoCredentials.ForToken("abc123"),
            retryBaseDelay: TimeSpan.Zero,
            httpClient: new HttpClient(handler));

        var repo = await client.GetRepositoryAsync("owner", "repo");

        Assert.Equal("repo", repo.Name);
        var auth = handler.Requests.Single().Headers.Authorization;
        Assert.Equal("token", auth?.Scheme);
        Assert.Equal("abc123", auth?.Parameter);
    }

    [Fact]
    public async Task Basic_auth_applies_base64_header()
    {
        var handler = Enqueue(StatusCode.OK, """{"id":1,"name":"repo"}""");
        using var client = new ForgejoClient(
            new Uri("https://git.example.com"),
            ForgejoCredentials.ForBasic("alice", "s3cret"),
            retryBaseDelay: TimeSpan.Zero,
            httpClient: new HttpClient(handler));

        await client.GetRepositoryAsync("owner", "repo");

        var auth = handler.Requests.Single().Headers.Authorization;
        Assert.Equal("Basic", auth?.Scheme);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:s3cret")), auth?.Parameter);
    }

    [Fact]
    public async Task Anonymous_auth_sends_no_authorization_header()
    {
        var handler = Enqueue(StatusCode.OK, """{"id":1,"name":"repo"}""");
        using var client = ClientFor(handler);
        await client.GetRepositoryAsync("owner", "repo");

        Assert.Null(handler.Requests.Single().Headers.Authorization);
    }

    [Theory]
    [InlineData("https://git.example.com", "https://git.example.com/api/v1/user/repos")]
    [InlineData("https://git.example.com/", "https://git.example.com/api/v1/user/repos")]
    [InlineData("https://git.example.com/sub", "https://git.example.com/sub/api/v1/user/repos")]
    [InlineData("https://git.example.com/api/v1", "https://git.example.com/api/v1/user/repos")]
    [InlineData("https://git.example.com/api/v1/", "https://git.example.com/api/v1/user/repos")]
    public async Task NormalizeApiRoot_appends_api_v1_idempotently(string root, string expected)
    {
        var handler = Enqueue(StatusCode.OK, "[]");
        using var client = new ForgejoClient(
            new Uri(root), ForgejoCredentials.Anonymous(),
            retryBaseDelay: TimeSpan.Zero, httpClient: new HttpClient(handler));

        await client.ListRepositoriesAsync();

        var requested = handler.Requests.Single().RequestUri;
        Assert.Equal(expected, requested!.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Non2xx_maps_to_forgejo_exception_with_status_and_code()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"repo not found"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetRepositoryAsync("owner", "missing"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Transient_500_is_retried_until_success()
    {
        var h = new TestHttpHandler();
        h.Enqueue(StatusCode.InternalServerError, """{"message":"boom"}""");
        h.Enqueue(StatusCode.InternalServerError, """{"message":"boom"}""");
        h.Enqueue(StatusCode.OK, """{"id":1,"name":"repo"}""");
        using var client = ClientFor(h);

        var repo = await client.GetRepositoryAsync("o", "n");
        Assert.Equal("repo", repo.Name);
        Assert.Equal(3, h.Requests.Count);
    }

    [Fact]
    public async Task Retryable_exhaustion_surfaces_the_last_status()
    {
        var h = new TestHttpHandler();
        for (var i = 0; i < 4; i++) h.Enqueue(StatusCode.BadGateway, "upstream down", "text/plain");
        using var client = ClientFor(h, maxRetries: 3);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetRepositoryAsync("o", "n"));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(4, h.Requests.Count);
    }

    [Fact]
    public async Task Non_retryable_404_is_not_retried()
    {
        var h = new TestHttpHandler();
        h.Enqueue(StatusCode.NotFound, "{}");
        using var client = ClientFor(h);

        await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetRepositoryAsync("o", "n"));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Transport_error_is_retried()
    {
        var h = new ThrowingOnceHandler();
        using var client = new ForgejoClient(
            new Uri("https://git.example.com"), ForgejoCredentials.Anonymous(),
            2, TimeSpan.Zero, new HttpClient(h));
        h.Responses.Enqueue(new HttpResponseMessage(StatusCode.OK)
        {
            Content = new StringContent("""{"id":1,"name":"repo"}""", System.Text.Encoding.UTF8, "application/json"),
        });

        var repo = await client.GetRepositoryAsync("o", "n");
        Assert.Equal("repo", repo.Name);
        Assert.Equal(2, h.RequestCount);
    }

    [Fact]
    public async Task ListResults_carry_items_from_array()
    {
        var h = new TestHttpHandler();
        h.Responses.Enqueue(new HttpResponseMessage(StatusCode.OK)
        {
            Content = new StringContent(
                """[{"id":1,"name":"a"},{"id":2,"name":"b"}]""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        using var client = ClientFor(h);

        var list = await client.ListRepositoriesAsync(new Page { Number = 1, Size = 2 });
        Assert.Equal(2, list.Count);
        Assert.Equal(1, list.Items[0].Id);
        var requested = h.Requests.Single().RequestUri;
        Assert.Contains("page=1&limit=2", requested!.Query);
    }

    [Fact]
    public async Task Invalid_arguments_throw()
    {
        var handler = Enqueue(StatusCode.OK, "[]");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetRepositoryAsync("", "n"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateIssueAsync("o", "n", new CreateIssueRequest { Title = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetFileContentAsync("o", "n", ""));
    }

    [Fact]
    public async Task CreateIssue_posts_snake_case_payload()
    {
        var handler = Enqueue(StatusCode.Created, """{"id":7,"title":"Bug"}""");
        using var client = ClientFor(handler);

        var issue = await client.CreateIssueAsync("o", "n", new CreateIssueRequest
        {
            Title = "Bug",
            Labels = new[] { "bug" },
        });

        Assert.Equal(7, issue.Id);
        var request = handler.Requests.Single();
        var body = handler.Bodies.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"title\":\"Bug\"", body);
        Assert.Contains("\"labels\":[\"bug\"]", body);
    }
}

/// <summary>Throws a transport exception for the first request, then serves queued responses.</summary>
public sealed class ThrowingOnceHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    public Queue<HttpResponseMessage> Responses { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (RequestCount == 1)
            throw new HttpRequestException("simulated network failure");
        return Task.FromResult(Responses.Dequeue());
    }
}
