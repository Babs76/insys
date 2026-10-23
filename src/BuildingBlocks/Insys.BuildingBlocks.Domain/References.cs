namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// Business references derived from a record's ID. IDs are UUIDv7, whose leading hex digits are a millisecond timestamp,
/// so a reference built from them repeats for records created in the same interval. The trailing digits are random.
/// TODO(spec): sequences and check digits need a numbering counter (OQ-058).
/// </summary>
public static class References
{
    /// <summary>
    /// The prefix and the ID's last hex digits (random for up to 15 of them), upper case, <paramref name="length"/>
    /// characters in all; a prefix as long as the reference is cut to it.
    /// </summary>
    public static string Of(string prefix, Guid id, int length)
    {
        var digits = Math.Clamp(length - prefix.Length, 0, 32);
        return (prefix + id.ToString("N")[^digits..].ToUpperInvariant())[..Math.Min(length, prefix.Length + digits)];
    }
}
