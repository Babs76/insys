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
namespace Insys.BuildingBlocks.Application.Context;

/// <summary>What a module loaded of a migration batch, of one kind (B-013). <see cref="Amount"/> is null where the module holds no amount to compare.</summary>
public sealed record MigrationTotal(string Kind, long Count, decimal? Amount);

/// <summary>Public query interface, implemented by each module that loads a migration batch.</summary>
public interface IMigrationTotals
{
    Task<IReadOnlyList<MigrationTotal>> For(Guid tenantId, string batchCode, CancellationToken cancellationToken);
}

/// <summary>A policy or membership in an export (OQ-158): numbers, status and dates, with no identifier numbers or medical data.</summary>
public sealed record PolicyExportRow(string Domain, string PolicyNumber, string ProductCode, Guid PolicyholderPartyId, string Status, DateOnly StartDate, DateOnly? EndDate, string Currency);

/// <summary>Public query interface, implemented by each module that keeps policies. With a domain named, only that module answers.</summary>
public interface IPolicyExportRows
{
    string Domain { get; }

    Task<IReadOnlyList<PolicyExportRow>> All(Guid tenantId, CancellationToken cancellationToken);
}
