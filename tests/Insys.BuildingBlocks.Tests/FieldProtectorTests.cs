/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
