using System.Security.Cryptography;
using Insys.BuildingBlocks.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Tests;

public sealed class FieldProtectorTests
{
    private static AesGcmFieldProtector Create(string activeKey = "k1") => new(Options.Create(new FieldProtectionOptions
    {
        ActiveKeyId = activeKey,
        Keys =
        {
            ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["k2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        },
        HashKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    }));

    [Fact]
    public void FieldProtector_EncryptsAndDecrypts_DBA014()
    {
        var protector = Create();
        var stored = protector.Protect("8001015009087");

        stored.Encrypted.ShouldNotBeEmpty();
        System.Text.Encoding.UTF8.GetString(stored.Encrypted).ShouldNotContain("8001015009087");
        protector.Unprotect(stored.Encrypted).ShouldBe("8001015009087");
        stored.Last4.ShouldBe("9087");
    }

    [Fact]
    public void FieldProtector_Hash_IsDeterministicHex64_ForLookup_DBA014()
    {
        var protector = Create();

        protector.Hash("12345").ShouldBe(protector.Hash("12345"));
        protector.Hash("12345").Length.ShouldBe(64);
        protector.Protect("12345").Encrypted.ShouldNotBe(protector.Protect("12345").Encrypted);
    }

    [Fact]
    public void FieldProtector_TamperedCiphertext_IsRejected()
    {
        var protector = Create();
        var stored = protector.Protect("secret");
        stored.Encrypted[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(() => protector.Unprotect(stored.Encrypted));
    }
}
