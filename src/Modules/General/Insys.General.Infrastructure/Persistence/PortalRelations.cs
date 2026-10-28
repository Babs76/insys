/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.General.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>
/// General's records in an external user's relationship (external-user access §2), for other modules' portal reads: a
/// policy (and its endorsements) is the member's when they hold it, and the broker's when it carries one of its codes.
/// A broker's clients are the holders and proposers of its policies and quotes.
/// </summary>
internal sealed class GeneralPortalRelations([FromKeyedServices(GeneralDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPortalRecordRelations, IBrokeredPolicies,
    IBrokeredClients
{
    private const string Policy = "p.policyholder_party_id = @party OR p.intermediary_code = ANY(@codes)";

    public bool Covers(string businessRefType) => Sql(businessRefType) is not null;

    public Task<bool> Related(Guid tenantId, ExternalSubject who, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        Sql(businessRefType) is { } sql ? PortalRelationReader.Exists(dataSource, tenantId, sql, businessRefId, who, cancellationToken) : Task.FromResult(false);

    public Task<IReadOnlyList<Guid>> PolicyIds(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, CancellationToken cancellationToken) =>
        PortalRelationReader.Ids(dataSource, tenantId, "SELECT id FROM general.policy WHERE intermediary_code = ANY(@codes)", intermediaryCodes, null, cancellationToken);

    public async Task<bool> Brokers(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, Guid policyId, CancellationToken cancellationToken) =>
        (await PortalRelationReader.Ids(dataSource, tenantId, "SELECT id FROM general.policy WHERE id = @id AND intermediary_code = ANY(@codes)", intermediaryCodes,
            policyId, cancellationToken)).Count > 0;

    public Task<IReadOnlyList<Guid>> PartyIds(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, CancellationToken cancellationToken) =>
        PortalRelationReader.Ids(dataSource, tenantId, """
            SELECT policyholder_party_id FROM general.policy WHERE intermediary_code = ANY(@codes)
            UNION SELECT proposer_party_id FROM general.quote WHERE intermediary_code = ANY(@codes)
            """, intermediaryCodes, null, cancellationToken);

    private static string? Sql(string businessRefType) => businessRefType switch
    {
        GeneralDocuments.PolicyRefType => $"SELECT 1 FROM general.policy p WHERE p.id = @id AND ({Policy})",
        GeneralDocuments.EndorsementRefType => $"SELECT 1 FROM general.endorsement e JOIN general.policy p ON p.tenant_id = e.tenant_id AND p.id = e.policy_id WHERE e.id = @id AND ({Policy})",
        _ => null,
    };
}
