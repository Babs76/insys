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
/// The codes a string field (or each item of a string array field) can hold, where they are fixed by the schema rather
/// than by a C# enum: a database CHECK constraint, say. The OpenAPI documents declare them as the field's enum (GAP-008).
/// It describes the contract only; validation stays where it is.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OneOfAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
}

/// <summary>
/// A request field the API refuses to go without (a static data input's mandatory field): the OpenAPI documents list it
/// as required. It describes the contract only; the input's own validation refuses its absence.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredFieldAttribute : Attribute;

/// <summary>A month as yyyy-MM (a string field or parameter): the OpenAPI documents give it that pattern.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class YearMonthAttribute : Attribute
{
    public const string Pattern = "^[0-9]{4}-(0[1-9]|1[0-2])$";
}
