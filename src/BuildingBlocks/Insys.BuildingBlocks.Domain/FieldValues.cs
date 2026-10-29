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
/// Values a free-string field is known to take (BR-025, BR-036): the ones INSYS itself writes or sends, which the OpenAPI
/// documents list as x-known-values. Not an enum, because other values are allowed (a tenant's own rule outcomes, say).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class KnownValuesAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
}

/// <summary>
/// The static data table a code field takes its codes from (BR-025, BR-036), as schema.table, and the column holding the
/// value the field carries (BR-105): the OpenAPI documents name them as x-code-list and x-code-list-column, so a client
/// offers the table's values in force and their labels.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CodeListAttribute(string table, string column) : Attribute
{
    public string Table { get; } = table;

    /// <summary>The table's column the field's value is (its code, or a named value column such as method_code).</summary>
    public string Column { get; } = column;

    /// <summary>For a table holding several lists, the rows of this one, as column=value (BR-026): published as x-code-list-filter.</summary>
    public string? Filter { get; init; }
}
