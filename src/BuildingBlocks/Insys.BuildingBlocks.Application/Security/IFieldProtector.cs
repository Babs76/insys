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
