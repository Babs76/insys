/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Life.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>
/// Life's records in an external user's relationship (external-user access §2), for other modules' portal reads: a
/// policy is the member's when they hold it or are an insured life, and the broker's when it carries one of its codes.
/// A broker's clients are the holders of its policies and the applicants of its proposals.
/// </summary>
internal sealed class LifePortalRelations([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPortalRecordRelations, IBrokeredPolicies,
    IBrokeredClients
{
    public bool Covers(string businessRefType) => businessRefType == LifeDocuments.PolicyRefType;

    public Task<bool> Related(Guid tenantId, ExternalSubject who, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        Covers(businessRefType)
            ? PortalRelationReader.Exists(dataSource, tenantId, """
                SELECT 1 FROM life.policy p WHERE p.id = @id AND (p.policyholder_party_id = @party OR p.intermediary_code = ANY(@codes)
                  OR EXISTS (SELECT 1 FROM life.policy_insured_life l WHERE l.tenant_id = p.tenant_id AND l.policy_id = p.id AND l.party_id = @party))
                """, businessRefId, who, cancellationToken)
            : Task.FromResult(false);

    public Task<IReadOnlyList<Guid>> PolicyIds(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, CancellationToken cancellationToken) =>
        PortalRelationReader.Ids(dataSource, tenantId, "SELECT id FROM life.policy WHERE intermediary_code = ANY(@codes)", intermediaryCodes, null, cancellationToken);

    public Task<IReadOnlyList<Guid>> PartyIds(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, CancellationToken cancellationToken) =>
        PortalRelationReader.Ids(dataSource, tenantId, """
            SELECT policyholder_party_id FROM life.policy WHERE intermediary_code = ANY(@codes)
            UNION SELECT applicant_party_id FROM life.proposal WHERE intermediary_code = ANY(@codes)
            """, intermediaryCodes, null, cancellationToken);

    public async Task<bool> Brokers(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, Guid policyId, CancellationToken cancellationToken) =>
        (await PortalRelationReader.Ids(dataSource, tenantId, "SELECT id FROM life.policy WHERE id = @id AND intermediary_code = ANY(@codes)", intermediaryCodes,
            policyId, cancellationToken)).Count > 0;
}
