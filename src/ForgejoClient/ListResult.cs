using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forgejo.Client;

/// <summary>
/// A Forgejo list response: the items, plus paging metadata.
/// <list type="bullet">
/// <item><see cref="Total"/> — the instance's <c>x-total-count</c> header (or an
/// embedded <c>total</c> field) when reported; null otherwise.</item>
/// <item><see cref="NextPageHint"/> — the 1-based page number to request next,
/// or null when there is no further page. Serialized as <c>next_page</c>.
/// Derived preferentially from a <c>Link: rel="next"</c> header, else from the
/// full-page heuristic (a page shorter than <c>limit</c> has no successor).</item>
/// </list>
/// Serialized by <see cref="ListResultConverter{T}"/> (registered in
/// <see cref="ForgejoJson.Default"/>) so the envelope shape is explicit and
/// <c>next_page</c> survives the global null-dropping policy.
/// </summary>
public sealed record ListResult<T>(IReadOnlyList<T> Items, long? Total)
{
    /// <summary>The number of items in this page.</summary>
    public int Count => Items.Count;

    /// <summary>
    /// The 1-based page index of the next page, or null when the caller has
    /// seen everything. When non-null, pass <c>page = <see cref="NextPageHint"/></c>
    /// to the same list tool to continue. Serialized as <c>next_page</c> —
    /// and, unlike the rest of the envelope, it is emitted even when null
    /// (the pagination contract: the property is always present, and a value
    /// of <c>null</c> is the signal that the stream is exhausted).
    /// </summary>
    [JsonPropertyName("next_page")]
    public int? NextPageHint { get; init; }

    internal static ListResult<T> FromArray(IReadOnlyList<T> items, long? total = null)
        => new(items, total);
}

/// <summary>
/// Factory dispatching <see cref="JsonConverter"/>s for the open-generic
/// <see cref="ListResult{T}"/> family (STJ 10 does not expose a per-property
/// "never skip null" attribute, so the envelope shape is owned by the
/// converter instead of by attributes).
/// </summary>
public sealed class ListResultConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t)
        => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ListResult<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var convType = typeof(ListResultConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]);
        return (JsonConverter)Activator.CreateInstance(convType)!;
    }
}

/// <summary>
/// Serializes <see cref="ListResult{T}"/> as
/// <c>{"items": [...], "count": N, "total": M?, "next_page": P?}</c> —
/// <c>count</c> and <c>next_page</c> are ALWAYS present (a dropped
/// <c>next_page</c> key could not distinguish "last page" from "unpaged
/// tool"); <c>total</c> stays droppable because the instance in question
/// never reports one. Reading it back is supported for round-trip tests.
/// </summary>
public sealed class ListResultConverter<T> : JsonConverter<ListResult<T>>
{
    public override bool CanConvert(Type t) => t == typeof(ListResult<T>);

    public override bool HandleNull => true;

    public override ListResult<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"ListResult<{typeof(T).Name}> expected a JSON object.");

        IReadOnlyList<T> items = Array.Empty<T>();
        if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind != JsonValueKind.Null)
            items = JsonSerializer.Deserialize<IReadOnlyList<T>>(itemsEl.GetRawText(), options) ?? Array.Empty<T>();

        long? total = null;
        if (root.TryGetProperty("total", out var totalEl) && totalEl.ValueKind != JsonValueKind.Null)
            total = totalEl.GetInt64();

        int? next = null;
        if (root.TryGetProperty("next_page", out var nextEl) && nextEl.ValueKind != JsonValueKind.Null)
            next = nextEl.GetInt32();

        return new ListResult<T>(items, total)
        {
            NextPageHint = next,
        };
    }

    public override void Write(Utf8JsonWriter writer, ListResult<T>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        foreach (var item in value.Items)
        {
            if (item is null)
            {
                writer.WriteNullValue();
                continue;
            }
            JsonSerializer.Serialize(writer, item, item.GetType(), options);
        }
        writer.WriteEndArray();
        writer.WriteNumber("count", value.Count);
        if (value.Total.HasValue)
            writer.WriteNumber("total", value.Total.Value);
        // Pagination contract: always present; null == stream exhausted.
        if (value.NextPageHint.HasValue)
            writer.WriteNumber("next_page", value.NextPageHint.Value);
        else
            writer.WriteNull("next_page");
        writer.WriteEndObject();
    }
}
