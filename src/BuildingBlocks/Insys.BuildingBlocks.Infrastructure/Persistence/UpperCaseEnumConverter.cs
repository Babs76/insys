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
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>Stores an enum as an upper-case code, the schema's convention: PendingApproval becomes PENDING_APPROVAL.</summary>
public sealed class UpperCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => ToCode(v),
    v => FromCode(v))
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> ByCode = Enum.GetValues<TEnum>().ToDictionary(ToCode, v => v, StringComparer.Ordinal);

    public static string ToCode(TEnum value) => SnakeCase.From(value.ToString()).ToUpperInvariant();

    public static TEnum FromCode(string code) =>
        ByCode.TryGetValue(code, out var value) ? value : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} code '{code}'.");
}
