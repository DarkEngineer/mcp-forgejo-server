using System.ComponentModel.DataAnnotations;
using Forgejo.Client;
using Microsoft.Extensions.Configuration;

namespace Forgejo.Mcp;

/// <summary>
/// Strongly-typed configuration for the MCP server. Bind this to the
/// <c>Forgejo</c> section of <c>appsettings.json</c> (see
/// <see cref="SectionName"/>) and/or environment variables with the
/// <c>FORGEJO_</c> prefix (which win over the file, per the standard
/// precedence of <see cref="ConfigurationBuilder"/>).
/// </summary>
/// <remarks>
/// The options layer is the single source of truth for how the host
/// constructs a <see cref="ForgejoClient"/>. It deliberately does not reach
/// into environment variables itself — that keeps binding trivially testable
/// (see <c>tests/ForgejoMcp.Tests/ForgejoServerOptionsTests.cs</c>) and lets
/// any future host reuse the same POCO.
/// </remarks>
public sealed class ForgejoServerOptions
{
    /// <summary>The configuration section key that contains these options.</summary>
    public const string SectionName = "Forgejo";

    /// <summary>
    /// The Forgejo instance root, e.g. <c>https://git.home.internal</c>.
    /// Required — the client appends <c>/api/v1</c> automatically.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Personal access token (preferred auth mode). Sent as
    /// <c>Authorization: token &lt;t&gt;</c>.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// HTTP basic username (alternative auth mode; used with
    /// <see cref="Password"/>).
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// HTTP basic password (alternative auth mode; used with
    /// <see cref="Username"/>).
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Retries for transient failures (429, 5xx, transport) after the first
    /// attempt. Default 3 ⇒ 4 total attempts.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Base backoff delay in seconds (exponential). Default 1s.
    /// </summary>
    public double RetryBaseDelaySeconds { get; init; } = 1.0;

    /// <summary>
    /// Validates the options and constructs a <see cref="ForgejoClient"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Url"/> is missing
    /// or not a valid absolute URL.</exception>
    /// <exception cref="ArgumentException">Token and basic credentials are
    /// combined, or the basic pair is only half set — an ambiguous mix that is
    /// almost certainly a configuration mistake.</exception>
    public ForgejoClient BuildClient()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Url)} is required. " +
                "Set it in appsettings.json or via the FORGEJO_URL environment variable. " +
                "Example: FORGEJO_URL=https://git.home.internal");
        }

        Uri parsed;
        try
        {
            parsed = new Uri(Url, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Url)} = '{Url}' is not a valid absolute URL.", ex);
        }

        var hasToken = !string.IsNullOrWhiteSpace(Token);
        var hasUsername = !string.IsNullOrWhiteSpace(Username);
        var hasPassword = !string.IsNullOrWhiteSpace(Password);
        var hasBasicPair = hasUsername && hasPassword;

        if (hasToken && (hasUsername || hasPassword))
        {
            throw new ArgumentException(
                $"{nameof(Token)} and basic credentials ({nameof(Username)}/{nameof(Password)}) " +
                "are both set. Pick one authentication mode: token XOR basic auth.");
        }
        if (hasUsername != hasPassword)
        {
            throw new ArgumentException(
                $"{nameof(Username)} and {nameof(Password)} must be set together " +
                "(basic auth) or both left unset.");
        }

        ForgejoCredentials credentials;
        if (hasToken)
            credentials = ForgejoCredentials.ForToken(Token!);
        else if (hasBasicPair)
            credentials = ForgejoCredentials.ForBasic(Username!, Password!);
        else
            credentials = ForgejoCredentials.Anonymous();

        var maxRetries = Math.Max(0, MaxRetries);
        var baseDelay = TimeSpan.FromSeconds(Math.Max(0.0, RetryBaseDelaySeconds));

        return new ForgejoClient(parsed, credentials, maxRetries, baseDelay);
    }

    /// <summary>
    /// Binds the <c>Forgejo</c> section of the given configuration (typically
    /// <c>appsettings.json</c> + the <c>FORGEJO_*</c> environment variables,
    /// merged by the caller in standard precedence order) into a new options
    /// instance.
    /// </summary>
    public static ForgejoServerOptions FromConfiguration(IConfiguration config)
        => config.GetSection(SectionName).Get<ForgejoServerOptions>()
           ?? new ForgejoServerOptions();

    /// <summary>
    /// Layers the <c>FORGEJO_*</c> environment variables on top of options
    /// derived from a configuration file. This is explicit instead of relying
    /// on the <c>{SECTION}_{KEY}</c> convention: a bare environment-variable
    /// source would expose them as <c>FORGEJO_*</c> keys, which do NOT bind
    /// to properties of the <c>Forgejo</c> section.
    /// </summary>
    public static ForgejoServerOptions ApplyEnvironmentOverrides(
        ForgejoServerOptions baseOptions,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= (name) => Environment.GetEnvironmentVariable(name);
        string? Env(string name)
        {
            var v = getEnvironmentVariable?.Invoke(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        // Fast path: none of the FORGEJO_* variables set — return unchanged.
        if (Env(VarUrl) is null && Env(VarToken) is null && Env(VarUsername) is null
            && Env(VarPassword) is null && Env(VarMaxRetries) is null
            && Env(VarRetryDelay) is null)
        {
            return baseOptions;
        }

        var url = Env(VarUrl);
        var token = Env(VarToken);
        var username = Env(VarUsername);
        var password = Env(VarPassword);

        int maxRetries = baseOptions.MaxRetries;
        if (Env(VarMaxRetries) is string mrRaw && int.TryParse(mrRaw, out var mr) && mr >= 0)
        {
            maxRetries = mr;
        }
        double baseDelay = baseOptions.RetryBaseDelaySeconds;
        if (Env(VarRetryDelay) is string dRaw
            && double.TryParse(dRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)
            && d > 0)
        {
            baseDelay = d;
        }

        return new ForgejoServerOptions
        {
            Url = url ?? baseOptions.Url,
            Token = token ?? baseOptions.Token,
            Username = username ?? baseOptions.Username,
            Password = password ?? baseOptions.Password,
            MaxRetries = maxRetries,
            RetryBaseDelaySeconds = baseDelay,
        };
    }

    // Environment variable names (one per option).
    private const string VarUrl = "FORGEJO_URL";
    private const string VarToken = "FORGEJO_TOKEN";
    private const string VarUsername = "FORGEJO_USERNAME";
    private const string VarPassword = "FORGEJO_PASSWORD";
    private const string VarMaxRetries = "FORGEJO_MAX_RETRIES";
    private const string VarRetryDelay = "FORGEJO_RETRY_BASE_DELAY_SECONDS";
}
