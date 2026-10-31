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
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Security;

/// <summary>Envelope encryption settings (FieldProtection:Envelope; ADR-018).</summary>
public sealed class EnvelopeOptions
{
    /// <summary>"local" (a master key from configuration; development and CI) or "dew" (Huawei Cloud DEW KMS).</summary>
    public string Kms { get; set; } = "local";

    /// <summary>How long a decrypted data key is used before it is decrypted again through the KMS.</summary>
    public TimeSpan DataKeyCacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A new data key is created when the newest is this old; new values are encrypted with the newest.</summary>
    public TimeSpan RotateAfter { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often the rotation and cache refresh run.</summary>
    public TimeSpan RotationCheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>For Kms "local" only: the base64 256-bit master key. Never used in production.</summary>
    public string LocalMasterKey { get; set; } = "";
}

/// <summary>
/// The data keys in memory (ADR-018). Every key in the register is decrypted through the KMS at most once per cache
/// period, and refreshed in the background before it expires, so encrypting and decrypting fields makes no KMS call in
/// normal operation. A KMS such as DEW is rate-limited per account; this is what keeps the platform well inside it.
/// </summary>
public sealed class DataKeyRing(IKeyManagementService kms, IDataKeyStore store, IClock clock, IOptions<FieldProtectionOptions> options,
    ILogger<DataKeyRing> logger)
{
    private readonly ConcurrentDictionary<string, (byte[] Key, DateTimeOffset DecryptedAt)> _keys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _load = new(1, 1);
    private IReadOnlyList<StoredDataKey> _register = [];

    private EnvelopeOptions Settings => options.Value.Envelope;

    /// <summary>The newest data key, for encrypting.</summary>
    public (string KeyId, byte[] Key) Active()
    {
        var newest = _register.MaxBy(k => k.CreatedAt) ?? Sync(() => Refresh(CancellationToken.None)).MaxBy(k => k.CreatedAt)
            ?? throw new CryptographicException("No field protection data key exists; the rotation service creates one at startup.");
        return (newest.KeyId, Key(newest.KeyId));
    }

    /// <summary>The data key with this ID, decrypting it through the KMS only if it is not in memory or has expired.</summary>
    public byte[] Key(string keyId)
    {
        if (_keys.TryGetValue(keyId, out var cached) && clock.UtcNow - cached.DecryptedAt < Settings.DataKeyCacheDuration)
        {
            return cached.Key;
        }

        return Sync(() => Decrypt(keyId, CancellationToken.None));
    }

    /// <summary>Reloads the register and decrypts every key that is missing or expires within the next refresh.</summary>
    public async Task<IReadOnlyList<StoredDataKey>> Refresh(CancellationToken cancellationToken)
    {
        _register = await store.All(cancellationToken);
        var horizon = Settings.DataKeyCacheDuration - Settings.RotationCheckInterval;
        foreach (var key in _register)
        {
            if (!_keys.TryGetValue(key.KeyId, out var cached) || clock.UtcNow - cached.DecryptedAt >= horizon)
            {
                await Decrypt(key.KeyId, cancellationToken);
            }
        }

        return _register;
    }

    /// <summary>Creates a data key if there is none, or if the newest is older than RotateAfter (once across instances).</summary>
    public async Task RotateIfDue(CancellationToken cancellationToken)
    {
        var newest = (await store.All(cancellationToken)).MaxBy(k => k.CreatedAt);
        var now = clock.UtcNow;
        if (newest is not null && now - newest.CreatedAt < Settings.RotateAfter)
        {
            return;
        }

        var created = await kms.CreateDataKey(cancellationToken);
        var keyId = $"dk-{now:yyyyMMdd}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        if (await store.AddIfNoneNewer(new StoredDataKey(keyId, kms.MasterKeyRef, created.Ciphertext, now), now - Settings.RotateAfter, cancellationToken))
        {
            _keys[keyId] = (created.Plaintext, now);
            logger.LogInformation("Field protection data key {KeyId} created; new values use it", keyId);
        }

        _register = await store.All(cancellationToken);
    }

    private async Task<byte[]> Decrypt(string keyId, CancellationToken cancellationToken)
    {
        await _load.WaitAsync(cancellationToken);
        try
        {
            if (_keys.TryGetValue(keyId, out var cached) && clock.UtcNow - cached.DecryptedAt < Settings.DataKeyCacheDuration - Settings.RotationCheckInterval)
            {
                return cached.Key;
            }

            var stored = _register.FirstOrDefault(k => k.KeyId == keyId) ?? (await store.All(cancellationToken)).FirstOrDefault(k => k.KeyId == keyId)
                ?? throw new CryptographicException($"Field protection data key '{keyId}' is not in the register.");
            var key = await kms.DecryptDataKey(stored.Ciphertext, cancellationToken);
            _keys[keyId] = (key, clock.UtcNow);
            return key;
        }
        finally
        {
            _load.Release();
        }
    }

    // A rare path (a key not yet in memory, or the background refresh behind): the field protector's interface is synchronous.
    private static T Sync<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();
}

/// <summary>
/// The envelope provider (FieldProtection:Provider "envelope"; ADR-018): fields are encrypted with the newest data key from
/// the ring, and each value keeps its data key's ID, so rotation never rewrites existing rows. The lookup hash is unchanged.
/// </summary>
public sealed class EnvelopeFieldProtector(DataKeyRing ring, IOptions<FieldProtectionOptions> options) : IFieldProtector
{
    public ProtectedValue Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var (keyId, key) = ring.Active();
        return new ProtectedValue(FieldCipher.Encrypt(keyId, key, plaintext), Hash(plaintext), FieldCipher.Last4(plaintext));
    }

    public string Unprotect(byte[] encrypted) => FieldCipher.Decrypt(encrypted, ring.Key(FieldCipher.KeyIdOf(encrypted)));

    public string Hash(string plaintext) => FieldCipher.Hash(options.Value.HashKey, plaintext);
}

/// <summary>Creates the first data key at startup, then rotates and refreshes the ring on a schedule.</summary>
public sealed class DataKeyRotationService(DataKeyRing ring, IOptions<FieldProtectionOptions> options, ILogger<DataKeyRotationService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Envelope.RotationCheckInterval);
        do
        {
            try
            {
                await ring.RotateIfDue(stoppingToken);
                await ring.Refresh(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Field protection key rotation failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// A local stand-in for the KMS (Kms:Provider "local"): data keys are wrapped with a master key from configuration. For
/// development and CI only; production uses DEW KMS (ADR-018).
/// </summary>
public sealed class LocalKeyManagementService(IOptions<FieldProtectionOptions> options) : IKeyManagementService
{
    public string MasterKeyRef => "local";

    public Task<DataKey> CreateDataKey(CancellationToken cancellationToken)
    {
        var plaintext = RandomNumberGenerator.GetBytes(32);
        return Task.FromResult(new DataKey(plaintext, FieldCipher.Encrypt(MasterKeyRef, Master(), Convert.ToBase64String(plaintext))));
    }

    public Task<byte[]> DecryptDataKey(byte[] ciphertext, CancellationToken cancellationToken) =>
        Task.FromResult(Convert.FromBase64String(FieldCipher.Decrypt(ciphertext, Master())));

    private byte[] Master()
    {
        var encoded = options.Value.Envelope.LocalMasterKey;
        var key = string.IsNullOrWhiteSpace(encoded) ? throw new CryptographicException("FieldProtection:Envelope:LocalMasterKey is not configured.")
            : Convert.FromBase64String(encoded);
        return key.Length == 32 ? key : throw new CryptographicException("The local master key must be 256 bits.");
    }
}

/// <summary>The data key register in integration.field_data_key, on the API role (V0023): platform-wide, never plaintext.</summary>
public sealed class PostgresDataKeyStore([FromKeyedServices(DataSourceKeys.Api)] NpgsqlDataSource dataSource) : IDataKeyStore
{
    public async Task<IReadOnlyList<StoredDataKey>> All(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT key_id, master_key_ref, ciphertext, created_at FROM integration.field_data_key ORDER BY created_at");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<StoredDataKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new StoredDataKey(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return keys;
    }

    public async Task<bool> AddIfNoneNewer(StoredDataKey key, DateTimeOffset newerThan, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // One instance at a time decides whether a new key is due, so instances rotating together add one key, not several.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('integration.field_data_key'))", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO integration.field_data_key (key_id, master_key_ref, ciphertext, created_at)
            SELECT @id, @master, @ciphertext, @created
            WHERE NOT EXISTS (SELECT 1 FROM integration.field_data_key WHERE created_at > @newerThan)
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", key.KeyId);
        insert.Parameters.AddWithValue("master", key.MasterKeyRef);
        insert.Parameters.AddWithValue("ciphertext", key.Ciphertext);
        insert.Parameters.AddWithValue("created", key.CreatedAt);
        insert.Parameters.AddWithValue("newerThan", newerThan);
        var added = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return added;
    }
}

/// <summary>Chooses the field protector (FieldProtection:Provider) and, for envelope encryption, the KMS.</summary>
public static class FieldProtectionRegistration
{
    public static IServiceCollection AddFieldProtection(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddOptions<FieldProtectionOptions>().Bind(configuration.GetSection("FieldProtection"));
        var provider = configuration["FieldProtection:Provider"] ?? "local";
        if (string.Equals(provider, "envelope", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IDataKeyStore, PostgresDataKeyStore>();
            services.AddSingleton<DataKeyRing>();
            services.AddSingleton<IFieldProtector, EnvelopeFieldProtector>();
            services.AddHostedService<DataKeyRotationService>();
            if (CloudSettings.KmsProvider(configuration) == CloudSettings.LocalKms)
            {
                services.AddSingleton<IKeyManagementService, LocalKeyManagementService>();
            }
        }
        else
        {
            services.AddSingleton<IFieldProtector, AesGcmFieldProtector>();
        }

        return services;
    }
}
