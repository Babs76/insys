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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Tests;

/// <summary>
/// Envelope encryption (ADR-018, Phase 8 Workstream D): data keys are cached in memory, so field encryption and decryption
/// make no KMS call in normal operation (DEW KMS is rate-limited per account); keys rotate on a schedule without rewriting
/// existing values.
/// </summary>
public sealed class EnvelopeFieldProtectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThousandFieldDecryptions_OnAFreshInstance_MakeAtMostAHandfulOfKmsCalls()
    {
        var store = new MemoryStore();
        var clock = new Clock(Start);
        var writer = Instance(new CountingKms(), store, clock);
        await writer.Ring.RotateIfDue(CancellationToken.None);
        var values = Enumerable.Range(0, 1_000).Select(i => writer.Protector.Protect($"ID-{i:D6}").Encrypted).ToList();
        writer.Kms.Calls.ShouldBe(1, "one data key created; encrypting 1,000 values used it from memory");

        // Another instance (a restart, or a second API instance) reads them all.
        var reader = Instance(new CountingKms(), store, clock);
        var decrypted = values.Select(v => reader.Protector.Unprotect(v)).ToList();

        decrypted.ShouldBe(Enumerable.Range(0, 1_000).Select(i => $"ID-{i:D6}"));
        reader.Kms.Calls.ShouldBeLessThanOrEqualTo(3);
        reader.Kms.Calls.ShouldBe(1, "the data key is decrypted once and then held in memory");
    }

    [Fact]
    public async Task ExpiredCache_DecryptsTheDataKeyAgain_OnceForTheNextPeriod()
    {
        var store = new MemoryStore();
        var clock = new Clock(Start);
        var instance = Instance(new CountingKms(), store, clock);
        await instance.Ring.RotateIfDue(CancellationToken.None);
        var value = instance.Protector.Protect("7701015800087").Encrypted;
        var reader = Instance(new CountingKms(), store, clock);
        reader.Protector.Unprotect(value);

        clock.Now += TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1);
        foreach (var _ in Enumerable.Range(0, 100))
        {
            reader.Protector.Unprotect(value).ShouldBe("7701015800087");
        }

        reader.Kms.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Rotation_EncryptsNewValuesWithTheNewKey_AndOldValuesStayReadable()
    {
        var store = new MemoryStore();
        var clock = new Clock(Start);
        var instance = Instance(new CountingKms(), store, clock);
        await instance.Ring.RotateIfDue(CancellationToken.None);
        var before = instance.Protector.Protect("4111111111111111").Encrypted;

        clock.Now += TimeSpan.FromDays(30);
        await instance.Ring.RotateIfDue(CancellationToken.None);
        store.Keys.Count.ShouldBe(1, "not due before RotateAfter (90 days)");

        clock.Now += TimeSpan.FromDays(61);
        await instance.Ring.RotateIfDue(CancellationToken.None);
        await Instance(new CountingKms(), store, clock).Ring.RotateIfDue(CancellationToken.None);
        store.Keys.Count.ShouldBe(2, "one new key, however many instances rotate");

        var after = instance.Protector.Protect("4111111111111111").Encrypted;
        FieldCipher.KeyIdOf(after).ShouldBe(store.Keys.MaxBy(k => k.CreatedAt)!.KeyId);
        FieldCipher.KeyIdOf(before).ShouldNotBe(FieldCipher.KeyIdOf(after));
        var reader = Instance(new CountingKms(), store, clock);
        (reader.Protector.Unprotect(before), reader.Protector.Unprotect(after)).ShouldBe(("4111111111111111", "4111111111111111"));
    }

    [Fact]
    public void LookupHash_IsTheSameAsTheLocalProvider()
    {
        var instance = Instance(new CountingKms(), new MemoryStore(), new Clock(Start));
        var local = new AesGcmFieldProtector(instance.Options);
        instance.Protector.Hash("7701015800087").ShouldBe(local.Hash("7701015800087"));
    }

    private static (EnvelopeFieldProtector Protector, DataKeyRing Ring, CountingKms Kms, IOptions<FieldProtectionOptions> Options) Instance(
        CountingKms kms, MemoryStore store, Clock clock)
    {
        var options = Options.Create(new FieldProtectionOptions
        {
            Provider = "envelope", HashKey = Convert.ToBase64String(new byte[32]),
            Envelope = new EnvelopeOptions { DataKeyCacheDuration = TimeSpan.FromHours(1), RotateAfter = TimeSpan.FromDays(90), RotationCheckInterval = TimeSpan.FromMinutes(10) },
        });
        var ring = new DataKeyRing(kms, store, clock, options, NullLogger<DataKeyRing>.Instance);
        return (new EnvelopeFieldProtector(ring, options), ring, kms, options);
    }

    /// <summary>A KMS that counts every call, wrapping keys with a fixed master key.</summary>
    private sealed class CountingKms : IKeyManagementService
    {
        private static readonly byte[] Master = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        public int Calls;

        public string MasterKeyRef => "test-master";

        public Task<DataKey> CreateDataKey(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var plaintext = RandomNumberGenerator.GetBytes(32);
            return Task.FromResult(new DataKey(plaintext, FieldCipher.Encrypt(MasterKeyRef, Master, Convert.ToBase64String(plaintext))));
        }

        public Task<byte[]> DecryptDataKey(byte[] ciphertext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Convert.FromBase64String(FieldCipher.Decrypt(ciphertext, Master)));
        }
    }

    private sealed class MemoryStore : IDataKeyStore
    {
        public List<StoredDataKey> Keys { get; } = [];

        public Task<IReadOnlyList<StoredDataKey>> All(CancellationToken cancellationToken)
        {
            lock (Keys)
            {
                return Task.FromResult<IReadOnlyList<StoredDataKey>>([.. Keys]);
            }
        }

        public Task<bool> AddIfNoneNewer(StoredDataKey key, DateTimeOffset newerThan, CancellationToken cancellationToken)
        {
            lock (Keys)
            {
                if (Keys.Any(k => k.CreatedAt > newerThan))
                {
                    return Task.FromResult(false);
                }

                Keys.Add(key);
                return Task.FromResult(true);
            }
        }
    }

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public DateTimeOffset UtcNow => Now;
    }
}
