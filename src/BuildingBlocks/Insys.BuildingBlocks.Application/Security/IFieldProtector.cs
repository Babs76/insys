namespace Insys.BuildingBlocks.Application.Security;

/// <summary>The stored form of a sensitive identifier: the _enc, _hash and _last4 columns (DBA-014, §24.1).</summary>
public sealed record ProtectedValue(byte[] Encrypted, string Hash, string Last4);

/// <summary>Encrypts identifier and bank numbers in the application, with a keyed hash for exact-match lookup.</summary>
public interface IFieldProtector
{
    ProtectedValue Protect(string plaintext);

    string Unprotect(byte[] encrypted);

    /// <summary>The keyed hash used for lookup: 64 lower-case hex characters, matching char(64) columns.</summary>
    string Hash(string plaintext);
}
