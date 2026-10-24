/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Distribution.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Distribution.Infrastructure.Persistence;

/// <summary>A commission statement is in a broker's relationship once approved, when it is the broker's or a sub-broker's (external-user access §2).</summary>
internal sealed class DistributionPortalRelations([FromKeyedServices(DistributionDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPortalRecordRelations
{
    public bool Covers(string businessRefType) => businessRefType == DistributionApprovals.StatementBusinessRefType;

    public Task<bool> Related(Guid tenantId, ExternalSubject who, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        Covers(businessRefType)
            ? PortalRelationReader.Exists(dataSource, tenantId,
                "SELECT 1 FROM commission.commission_statement s WHERE s.id = @id AND s.intermediary_code = ANY(@codes) AND s.status <> 'DRAFT'", businessRefId, who,
                cancellationToken)
            : Task.FromResult(false);
}
