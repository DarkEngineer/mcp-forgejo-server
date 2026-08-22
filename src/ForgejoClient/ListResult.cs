namespace Forgejo.Client;

/// <summary>A Forgejo list response: the items plus the API's <c>Total</c> field when present.</summary>
public sealed record ListResult<T>(IReadOnlyList<T> Items, long? Total)
{
    /// <summary>The number of items in this page.</summary>
    public int Count => Items.Count;

    internal static ListResult<T> FromArray(IReadOnlyList<T> items, long? total = null)
        => new(items, total);
}
