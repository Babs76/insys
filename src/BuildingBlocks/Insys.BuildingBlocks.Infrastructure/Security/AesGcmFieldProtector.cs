/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using System.Text;
using Insys.BuildingBlocks.Application.Security;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Infrastructure.Security;

public sealed class FieldProtectionOptions
{
    /// <summary>Base64 256-bit encryption keys by key ID. Old keys stay here so existing values can be decrypted.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    public string ActiveKeyId { get; set; } = "";

    /// <summary>Base64 key for the HMAC-SHA256 lookup hash. TODO(spec): key custody (KMS or HSM) is an SRS §30 decision.</summary>
    public string HashKey { get; set; } = "";
}

/// <summary>
/// AES-256-GCM encryption for the _enc columns and HMAC-SHA256 for the _hash columns (DBA-014).
/// Stored layout: version (1 byte) | key ID length (1 byte) | key ID | nonce (12) | tag (16) | ciphertext.
/// </summary>
public sealed class AesGcmFieldProtector(IOptions<FieldProtectionOptions> options) : IFieldProtector
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public ProtectedValue Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var settings = options.Value;
        var key = KeyFor(settings.ActiveKeyId);
        var keyId = Encoding.ASCII.GetBytes(settings.ActiveKeyId);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[2 + keyId.Length + NonceSize + TagSize + plain.Length];
        output[0] = FormatVersion;
        output[1] = (byte)keyId.Length;
        keyId.CopyTo(output, 2);
        var offset = 2 + keyId.Length;
        var nonce = output.AsSpan(offset, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(offset + NonceSize + TagSize), output.AsSpan(offset + NonceSize, TagSize));
        return new ProtectedValue(output, Hash(plaintext), plaintext.Length <= 4 ? plaintext : plaintext[^4..]);
    }

    public string Unprotect(byte[] encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        if (encrypted.Length < 2 || encrypted[0] != FormatVersion)
        {
            throw new CryptographicException("Unknown protected value format.");
        }

        var keyIdLength = encrypted[1];
        var keyId = Encoding.ASCII.GetString(encrypted, 2, keyIdLength);
        var offset = 2 + keyIdLength;
        var cipherLength = encrypted.Length - offset - NonceSize - TagSize;
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(KeyFor(keyId), TagSize);
        aes.Decrypt(encrypted.AsSpan(offset, NonceSize), encrypted.AsSpan(offset + NonceSize + TagSize), encrypted.AsSpan(offset + NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    public string Hash(string plaintext)
    {
        var key = Convert.FromBase64String(Required(options.Value.HashKey, "FieldProtection:HashKey"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(plaintext)));
    }

    private byte[] KeyFor(string keyId)
    {
        if (!options.Value.Keys.TryGetValue(Required(keyId, "FieldProtection:ActiveKeyId"), out var encoded))
        {
            throw new CryptographicException($"Field protection key '{keyId}' is not configured.");
        }

        var key = Convert.FromBase64String(encoded);
        return key.Length == 32 ? key : throw new CryptographicException("Field protection keys must be 256 bits.");
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new CryptographicException($"{name} is not configured.") : value;
}
