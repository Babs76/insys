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

/// <summary>
/// A payee's bank details, given only to be hashed: they are never stored, logged or published. The hash is what an
/// approval of a payment to the payee covers.
/// </summary>
public sealed record PayeeBankDetails(string BankCode, string? BranchCode, string AccountNumber, string AccountHolder)
{
    public string Hash() =>
        ApprovalSubject.Hash(Normal(BankCode), Normal(BranchCode), new string(AccountNumber.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant(), Normal(AccountHolder));

    /// <summary>The record's text never shows the details.</summary>
    public override string ToString() => "PayeeBankDetails { *** }";

    private static string Normal(string? value) => (value ?? "").Trim().ToUpperInvariant();
}
