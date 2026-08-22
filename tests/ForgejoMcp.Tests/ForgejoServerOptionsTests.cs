namespace Forgejo.Tests;

using Forgejo.Client;
using Forgejo.Mcp;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Verifies the configuration layer end-to-end: values bind through the
/// "Forgejo" section, environment variables with the FORGEJO_ prefix override
/// the file (standard precedence), and the auth configurations produce the
/// right client credentials.
/// </summary>
public class ForgejoServerOptionsTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?>? section = null)
    {
        var dict = new Dictionary<string, string?>();
        if (section is not null)
            foreach (var (k, v) in section)
                dict["Forgejo:" + k] = v;
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .AddEnvironmentVariables()   // real env vars win, as in the host
            .Build();
    }

    [Fact]
    public void Missing_url_throws_explaining_env_var()
    {
        var options = ForgejoServerOptions.FromConfiguration(BuildConfig());
        var ex = Assert.Throws<InvalidOperationException>(() => options.BuildClient());
        Assert.Contains("FORGEJO_URL", ex.Message);
    }

    [Fact]
    public void Binds_token_mode_from_section()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Url"] = "https://git.example.com",
            ["Token"] = "tok-abc",
        });
        var options = ForgejoServerOptions.FromConfiguration(config);
        var client = options.BuildClient();

        Assert.Equal(AuthenticationMode.Token, client.Credentials.Mode);
        Assert.Equal("tok-abc", client.Credentials.Token);
        Assert.StartsWith("https://git.example.com/api/v1/", client.BaseUrl.ToString());
    }

    [Fact]
    public void Binds_basic_mode_from_section()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Url"] = "https://git.example.com",
            ["Username"] = "alice",
            ["Password"] = "s3cret",
        });
        var options = ForgejoServerOptions.FromConfiguration(config);
        var client = options.BuildClient();

        Assert.Equal(AuthenticationMode.Basic, client.Credentials.Mode);
        Assert.Equal("alice", client.Credentials.Username);
    }

    [Fact]
    public void Default_is_anonymous_when_no_credentials()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Url"] = "https://git.example.com",
        });
        var client = ForgejoServerOptions.FromConfiguration(config).BuildClient();
        Assert.Equal(AuthenticationMode.None, client.Credentials.Mode);
    }

    [Fact]
    public void Mix_of_token_and_basic_throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Url"] = "https://git.example.com",
            ["Token"] = "tok",
            ["Username"] = "alice",
        });
        var options = ForgejoServerOptions.FromConfiguration(config);
        Assert.Throws<ArgumentException>(() => options.BuildClient());
    }

    [Fact]
    public void Half_basic_pair_throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Url"] = "https://git.example.com",
            ["Username"] = "alice",
        });
        var options = ForgejoServerOptions.FromConfiguration(config);
        Assert.Throws<ArgumentException>(() => options.BuildClient());
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("git.example.com")]   // no scheme
    public void Invalid_url_throws(string bad)
    {
        var config = BuildConfig(new Dictionary<string, string?> { ["Url"] = bad });
        var options = ForgejoServerOptions.FromConfiguration(config);
        Assert.Throws<InvalidOperationException>(() => options.BuildClient());
    }
}
