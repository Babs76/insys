/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.Health.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>Health's records in an external user's relationship (external-user access §2), for other modules' portal reads: a remittance advice is its provider's.</summary>
internal sealed class HealthPortalRelations([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPortalRecordRelations
{
    public bool Covers(string businessRefType) => businessRefType == HealthReferences.RemittanceRefType;

    public Task<bool> Related(Guid tenantId, ExternalSubject who, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        Covers(businessRefType)
            ? PortalRelationReader.Exists(dataSource, tenantId, "SELECT 1 FROM health.remittance_advice r WHERE r.id = @id AND r.provider_id = @provider", businessRefId,
                who, cancellationToken)
            : Task.FromResult(false);
}
