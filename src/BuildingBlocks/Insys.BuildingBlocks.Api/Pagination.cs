using System.Buffers.Text;

namespace Insys.BuildingBlocks.Api;

/// <summary>A page of results with an opaque cursor for the next page.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// Cursor pagination over UUIDv7 IDs, which sort by creation time. TODO(spec): page size limits are not specified.
/// </summary>
public static class Cursor
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static string Encode(Guid lastId) => Base64Url.EncodeToString(lastId.ToByteArray(bigEndian: true));

    public static bool TryDecode(string? cursor, out Guid? afterId)
    {
        afterId = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            if (bytes.Length != 16)
            {
                return false;
            }

            afterId = new Guid(bytes, bigEndian: true);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static int ClampLimit(int? limit) => limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);
}
