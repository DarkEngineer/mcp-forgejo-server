namespace Forgejo.Client;

using System.Net.Http.Headers;
using System.Text;

/// <summary>The authentication mode used by a <see cref="ForgejoCredentials"/> instance.</summary>
public enum AuthenticationMode
{
    /// <summary>No authentication header is sent (read-only public data).</summary>
    None,
    /// <summary>An access token, sent as <c>Authorization: token &lt;token&gt;</c>.</summary>
    Token,
    /// <summary>HTTP basic authentication with a username and password / application password.</summary>
    Basic,
}

/// <summary>
/// Immutable credentials for a <see cref="ForgejoClient"/>. Create instances
/// with the <see cref="ForToken(string)"/>, <see cref="ForBasic(string, string)"/>
/// or <see cref="Anonymous"/> factories.
/// </summary>
public sealed class ForgejoCredentials
{
    private ForgejoCredentials(AuthenticationMode mode, string? token, string? username, string? password)
    {
        Mode = mode;
        Token = token;
        Username = username;
        Password = password;
    }

    /// <summary>The authentication mode.</summary>
    public AuthenticationMode Mode { get; }

    /// <summary>The token value (only when <see cref="Mode"/> is <see cref="AuthenticationMode.Token"/>).</summary>
    public string? Token { get; }

    /// <summary>The basic-auth username (only for <see cref="AuthenticationMode.Basic"/>).</summary>
    public string? Username { get; }

    /// <summary>The basic-auth password (only for <see cref="AuthenticationMode.Basic"/>).</summary>
    public string? Password { get; }

    /// <summary>Unauthenticated credentials.</summary>
    public static ForgejoCredentials Anonymous() => new(AuthenticationMode.None, null, null, null);

    /// <summary>
    /// Creates token credentials.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="token"/> is null or empty.</exception>
    public static ForgejoCredentials ForToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return new ForgejoCredentials(AuthenticationMode.Token, token, null, null);
    }

    /// <summary>
    /// Creates basic-auth credentials.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="username"/> or <paramref name="password"/> is null or empty.</exception>
    public static ForgejoCredentials ForBasic(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return new ForgejoCredentials(AuthenticationMode.Basic, null, username, password);
    }

    internal void ApplyTo(HttpRequestMessage request)
    {
        switch (Mode)
        {
            case AuthenticationMode.Token:
                request.Headers.Authorization = new AuthenticationHeaderValue("token", Token);
                break;
            case AuthenticationMode.Basic:
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", b64);
                break;
        }
    }
}
