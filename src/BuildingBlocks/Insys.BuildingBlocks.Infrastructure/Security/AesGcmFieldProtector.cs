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
    /// <summary>"local" (keys from configuration; development, tests and CI) or "envelope" (data keys from a KMS; ADR-018).</summary>
    public string Provider { get; set; } = "local";

    public EnvelopeOptions Envelope { get; set; } = new();

    /// <summary>Base64 256-bit encryption keys by key ID. Old keys stay here so existing values can be decrypted.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    public string ActiveKeyId { get; set; } = "";

    /// <summary>Base64 key for the HMAC-SHA256 lookup hash. TODO(spec): key custody (KMS or HSM) is an SRS §30 decision.</summary>
    public string HashKey { get; set; } = "";
}

/// <summary>
/// The stored layout of an encrypted field, shared by every provider: version (1 byte) | key ID length (1 byte) | key ID |
/// nonce (12) | tag (16) | ciphertext, AES-256-GCM. The key ID says which key decrypts it.
/// </summary>
public static class FieldCipher
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Encrypt(string keyId, byte[] key, string plaintext)
    {
        var id = Encoding.ASCII.GetBytes(keyId);
        if (id.Length is 0 or > 255)
        {
            throw new CryptographicException("A key ID is 1 to 255 ASCII characters.");
        }

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[2 + id.Length + NonceSize + TagSize + plain.Length];
        output[0] = FormatVersion;
        output[1] = (byte)id.Length;
        id.CopyTo(output, 2);
        var offset = 2 + id.Length;
        var nonce = output.AsSpan(offset, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(offset + NonceSize + TagSize), output.AsSpan(offset + NonceSize, TagSize));
        return output;
    }

    public static string KeyIdOf(byte[] encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        return encrypted.Length < 2 || encrypted[0] != FormatVersion || encrypted.Length < 2 + encrypted[1] + NonceSize + TagSize
            ? throw new CryptographicException("Unknown protected value format.")
            : Encoding.ASCII.GetString(encrypted, 2, encrypted[1]);
    }

    public static string Decrypt(byte[] encrypted, byte[] key)
    {
        var offset = 2 + KeyIdOf(encrypted).Length;
        var plain = new byte[encrypted.Length - offset - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(encrypted.AsSpan(offset, NonceSize), encrypted.AsSpan(offset + NonceSize + TagSize), encrypted.AsSpan(offset + NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static string Last4(string plaintext) => plaintext.Length <= 4 ? plaintext : plaintext[^4..];

    public static string Hash(string hashKeyBase64, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(hashKeyBase64))
        {
            throw new CryptographicException("FieldProtection:HashKey is not configured.");
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromBase64String(hashKeyBase64), Encoding.UTF8.GetBytes(plaintext)));
    }
}

/// <summary>
/// The local provider (FieldProtection:Provider "local", the default): AES-256-GCM keys from configuration for the _enc
/// columns, and HMAC-SHA256 for the _hash columns (DBA-014). For development, tests and CI (ADR-018).
/// </summary>
public sealed class AesGcmFieldProtector(IOptions<FieldProtectionOptions> options) : IFieldProtector
{
    public ProtectedValue Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var keyId = options.Value.ActiveKeyId;
        return new ProtectedValue(FieldCipher.Encrypt(keyId, KeyFor(keyId), plaintext), Hash(plaintext), FieldCipher.Last4(plaintext));
    }

    public string Unprotect(byte[] encrypted) => FieldCipher.Decrypt(encrypted, KeyFor(FieldCipher.KeyIdOf(encrypted)));

    public string Hash(string plaintext) => FieldCipher.Hash(options.Value.HashKey, plaintext);

    private byte[] KeyFor(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new CryptographicException("FieldProtection:ActiveKeyId is not configured.");
        }

        if (!options.Value.Keys.TryGetValue(keyId, out var encoded))
        {
            throw new CryptographicException($"Field protection key '{keyId}' is not configured.");
        }

        var key = Convert.FromBase64String(encoded);
        return key.Length == 32 ? key : throw new CryptographicException("Field protection keys must be 256 bits.");
    }
}
