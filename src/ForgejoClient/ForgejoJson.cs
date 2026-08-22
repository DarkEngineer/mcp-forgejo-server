namespace Forgejo.Client;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Converts PascalCase .NET property names to the snake_case names used by the
/// Forgejo REST API, and back (case-insensitive on read).
/// Examples: <c>CloneUrl → clone_url</c>, <c>Owner.Login → owner.login</c>.
/// </summary>
public sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public static readonly SnakeCaseNamingPolicy Instance = new();

    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                var prevUpper = char.IsUpper(name[i - 1]);
                var nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (!prevUpper || nextLower)
                    sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> every <see cref="ForgejoClient"/>
/// request/responses uses: snake_case names, case-insensitive reads, and null
/// values dropped from outgoing payloads (so optional fields stay absent).
/// </summary>
public static class ForgejoJson
{
    /// <summary>The canonical serializer options for the Forgejo API.</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes <paramref name="value"/> to a JSON string using <see cref="Default"/>.</summary>
    public static string ToJson(object? value) => JsonSerializer.Serialize(value, Default);

    /// <summary>Deserializes a JSON string to <typeparamref name="T"/> using <see cref="Default"/>.</summary>
    public static T? FromJson<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Default);
}
