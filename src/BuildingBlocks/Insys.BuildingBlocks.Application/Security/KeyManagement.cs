/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
namespace Insys.BuildingBlocks.Application.Security;

/// <summary>A data key: its plaintext (kept in memory only) and its ciphertext under the KMS master key (stored).</summary>
public sealed record DataKey(byte[] Plaintext, byte[] Ciphertext);

/// <summary>
/// A key management service for envelope encryption (ADR-018): it creates data keys under a master key and decrypts
/// them. Huawei Cloud DEW KMS in production, a local master key in development and CI. Calls are rate-limited by the
/// provider, so callers cache decrypted data keys and call this rarely.
/// </summary>
public interface IKeyManagementService
{
    /// <summary>The master key's reference, recorded with every data key it protects.</summary>
    string MasterKeyRef { get; }

    Task<DataKey> CreateDataKey(CancellationToken cancellationToken);

    Task<byte[]> DecryptDataKey(byte[] ciphertext, CancellationToken cancellationToken);
}

/// <summary>A stored data key: the ID that encrypted values carry, its ciphertext, and when it was created.</summary>
public sealed record StoredDataKey(string KeyId, string MasterKeyRef, byte[] Ciphertext, DateTimeOffset CreatedAt);

/// <summary>The register of data keys (integration.field_data_key): never their plaintext.</summary>
public interface IDataKeyStore
{
    Task<IReadOnlyList<StoredDataKey>> All(CancellationToken cancellationToken);

    /// <summary>Adds a key unless another instance added one after <paramref name="newerThan"/>; returns whether it was added.</summary>
    Task<bool> AddIfNoneNewer(StoredDataKey key, DateTimeOffset newerThan, CancellationToken cancellationToken);
}
