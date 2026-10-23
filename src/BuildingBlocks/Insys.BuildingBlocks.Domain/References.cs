/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
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
namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// Business references derived from a record's ID. IDs are UUIDv7, whose leading hex digits are a millisecond timestamp,
/// so a reference built from them repeats for records created in the same interval. The trailing digits are random.
/// TODO(spec): sequences and check digits need a numbering counter (OQ-058).
/// </summary>
public static class References
{
    /// <summary>
    /// The prefix and the ID's last hex digits (random for up to 15 of them), upper case, <paramref name="length"/>
    /// characters in all; a prefix as long as the reference is cut to it.
    /// </summary>
    public static string Of(string prefix, Guid id, int length)
    {
        var digits = Math.Clamp(length - prefix.Length, 0, 32);
        return (prefix + id.ToString("N")[^digits..].ToUpperInvariant())[..Math.Min(length, prefix.Length + digits)];
    }
}
