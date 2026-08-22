namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using Forgejo.Client;

/// <summary>
/// A hand-rolled <see cref="HttpMessageHandler"/> that records every request
/// and replays queued responses — enough surface to test the ForgejoClient
/// pipeline (auth headers, retries, error mapping) without a live API.
///
/// Request bodies are read eagerly in <see cref="SendAsync"/> because the
/// real HTTP pipeline disposes <c>StringContent</c> right after the call
/// returns, so a later read would throw ObjectDisposedException.
/// </summary>
public sealed class TestHttpHandler : HttpMessageHandler
{
    /// <summary>Responses to replay, in order (a 503 is served once the queue drains).</summary>
    public Queue<HttpResponseMessage> Responses { get; } = new();

    public TestHttpHandler(IEnumerable<HttpResponseMessage>? responses = null)
    {
        if (responses is not null)
            foreach (var r in responses)
                Responses.Enqueue(r);
    }

    /// <summary>Every request sent through the handler, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Serialized request bodies, aligned 1:1 with <see cref="Requests"/>.</summary>
    public List<string> Bodies { get; } = new();

    public void Enqueue(System.Net.HttpStatusCode status,
        string body = "{}", string contentType = "application/json")
        => Responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Capture the body NOW — after the client's pipeline disposes the
        // content object, reading it lazily would throw.
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(request);
        Bodies.Add(body);
        return Responses.Count > 0
            ? Responses.Dequeue()
            : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("exhausted", Encoding.UTF8, "application/json"),
            };
    }
}
