namespace Insys.BuildingBlocks.Domain;

/// <summary>Hashes of what an approval covers (SHA-256, lower-case hex), so a decision is bound to its subject.</summary>
public static class ApprovalSubject
{
    public static string Hash(params object?[] parts) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
            parts.Select(p => p switch
            {
                decimal d => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                null => "",
                _ => p.ToString(),
            })))));
}
