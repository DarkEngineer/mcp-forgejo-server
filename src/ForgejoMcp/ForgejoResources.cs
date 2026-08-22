using Forgejo.Client;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Forgejo.Mcp;

/// <summary>
/// MCP resource exposing the metadata of a single Forgejo repository at
/// <c>forgejo://repo/{owner}/{name}</c>. Reading it returns the same JSON
/// document the <c>get_repo</c> tool returns (snake_case wire shape).
/// </summary>
/// <remarks>
/// URI template: <c>forgejo://repo/{owner}/{name}</c>. The two path segments
/// are parsed directly off the scheme-relative part of the URI, so no extra
/// URL machinery is required. When the repository does not exist or is not
/// visible to the configured credentials, reading the resource fails with an
/// MCP protocol error whose message carries the Forgejo HTTP status and the
/// server error body.
/// </remarks>
public sealed class ForgejoRepoResource : McpServerResource
{
    private readonly ForgejoClient _client;

    public ForgejoRepoResource(ForgejoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public override ResourceTemplate ProtocolResourceTemplate => new()
    {
        Name = "repo",
        Title = "Forgejo repository",
        UriTemplate = "repo/{owner}/{name}",
        MimeType = "application/json",
        Description =
            "Metadata for a single repository on the configured Forgejo instance " +
            "(the same document the `get_repo` tool returns). " +
            "Owner and name are URL-decoded from the two path segments of the URI.",
    };

    public override IReadOnlyList<object> Metadata { get; }
        = new object[] { "forgejo", "repository" };

    public override bool IsMatch(string uri)
    {
        // The SDK calls IsMatch with the full scheme-relative path
        // (e.g. "repo/dark-eternity/emberfall" for forgejo://repo/...).
        // Must accept the leading "repo/" prefix of the template.
        if (string.IsNullOrEmpty(uri))
            return false;

        var path = StripScheme(uri);
        path = StripRepoPrefix(path);
        return IsOwnerName(path);
    }

    public override async ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken)
    {
        var uri = request.Params?.Uri
            ?? throw new McpProtocolException("read_resource: missing 'uri' in request params.");

        var path = StripScheme(uri);
        path = StripRepoPrefix(path);

        if (!IsOwnerName(path))
            throw new McpProtocolException(
                $"read_resource: URI '{uri}' does not match the template 'forgejo://repo/{{owner}}/{{name}}'.");

        var firstSlash = path.IndexOf('/');
        var owner = Uri.UnescapeDataString(path[..firstSlash]);
        var name = Uri.UnescapeDataString(path[(firstSlash + 1)..]);

        var repo = await _client
            .GetRepositoryAsync(owner, name, cancellationToken)
            .ConfigureAwait(false);

        var json = ForgejoJson.ToJson(repo);
        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "application/json",
                    Text = json,
                },
            },
        };
    }

    /// <summary>Strips a leading <c>forgejo://</c> scheme, if present.</summary>
    private static string StripScheme(string uri)
    {
        var u = uri;
        if (u.StartsWith("forgejo://", StringComparison.OrdinalIgnoreCase))
            u = u["forgejo://".Length..];
        return u;
    }

    /// <summary>Strips the <c>repo/</c> template prefix, if the SDK passed it through.</summary>
    private static string StripRepoPrefix(string path)
    {
        if (path.StartsWith("repo/", StringComparison.OrdinalIgnoreCase))
            return path["repo/".Length..];
        return path;
    }

    /// <summary>
    /// True when <paramref name="path"/> is exactly <c>{owner}/{name}</c>:
    /// two non-empty segments, neither containing a slash.
    /// </summary>
    private static bool IsOwnerName(string path)
    {
        var firstSlash = path.IndexOf('/');
        if (firstSlash < 1)
            return false;
        var rest = path[(firstSlash + 1)..];
        return rest.Length > 0 && rest.IndexOf('/') < 0;
    }
}

/// <summary>
/// MCP resource exposed at <c>forgejo://instance</c>: server self-identification
/// (instance URL, authentication mode, protocol version). Useful for clients to
/// print "connected to <instance> as <mode>"; no network call is made.
/// </summary>
public sealed class ForgejoInstanceResource : McpServerResource
{
    private const string Template = "instance";

    private readonly ForgejoClient _client;

    public ForgejoInstanceResource(ForgejoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public override ResourceTemplate ProtocolResourceTemplate => new()
    {
        Name = "instance",
        Title = "Forgejo instance",
        UriTemplate = Template,
        MimeType = "application/json",
        Description = "Identity of the configured Forgejo instance: base URL and authentication mode.",
    };

    public override IReadOnlyList<object> Metadata { get; }
        = new object[] { "forgejo", "instance" };

    public override bool IsMatch(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return false;
        var u = uri;
        if (u.StartsWith("forgejo://", StringComparison.OrdinalIgnoreCase))
            u = u["forgejo://".Length..];
        return string.Equals(u, Template, StringComparison.OrdinalIgnoreCase);
    }

    public override ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken)
    {
        var payload = ForgejoJson.ToJson(new
        {
            instance = _client.BaseUrl.AbsoluteUri,
            auth_mode = _client.Credentials.Mode.ToString().ToLowerInvariant(),
            protocol = "mcp",
            server = "forgejo-mcp-server",
        });
        var result = new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Uri = request.Params?.Uri ?? "forgejo://instance",
                    MimeType = "application/json",
                    Text = payload,
                },
            },
        };
        return new ValueTask<ReadResourceResult>(result);
    }
}
