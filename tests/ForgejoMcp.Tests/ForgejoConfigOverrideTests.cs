namespace Forgejo.Tests;

using Forgejo.Mcp;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Verifies the layered-configuration path that a real host uses: later
/// sources (environment variables, added last by the host via
/// <c>AddEnvironmentVariables</c>) override earlier ones (appsettings.json).
/// Simulated here by two InMemoryCollection sources where the second layer
/// stands in for the environment — the binding result is identical.
/// </summary>
public class ForgejoConfigOverrideTests
{
    private static ForgejoServerOptions Layered(
        Dictionary<string, string?> file, Dictionary<string, string?> env)
    {
        var builder = new ConfigurationBuilder();
        foreach (var (k, v) in file) builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Forgejo:" + k] = v });
        // The env layer, added LAST — same position the host puts EnvironmentVariables.
        foreach (var (k, v) in env) builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Forgejo:" + k] = v });
        return ForgejoServerOptions.FromConfiguration(builder.Build());
    }

    [Fact]
    public void Env_layer_token_and_url_override_file()
    {
        var file = new Dictionary<string, string?>
        {
            ["Url"] = "https://file.example.com",
            ["Token"] = "from-file",
        };
        var env = new Dictionary<string, string?>
        {
            ["Url"] = "https://env.example.com",
            ["Token"] = "from-env",
        };

        var client = Layered(file, env).BuildClient();

        Assert.Equal("from-env", client.Credentials.Token);
        Assert.StartsWith("https://env.example.com/api/v1/", client.BaseUrl.ToString());
    }

    [Fact]
    public void File_values_stick_when_env_layer_is_empty()
    {
        var file = new Dictionary<string, string?>
        {
            ["Url"] = "https://file.example.com",
            ["Token"] = "from-file",
            ["MaxRetries"] = "5",
        };
        var client = Layered(file, new Dictionary<string, string?>()).BuildClient();

        Assert.Equal("from-file", client.Credentials.Token);
        Assert.Equal(5, client.MaxRetries);
    }
}
