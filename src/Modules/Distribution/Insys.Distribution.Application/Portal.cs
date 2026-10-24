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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Distribution.Domain.Commission;

namespace Insys.Distribution.Application;

// External-user access (docs/specs/addenda/external-user-access.md §2): a broker's commission statements in its portal,
// its own and its sub-brokers' (through the hierarchy, decided at sign-off). Only statements a second person approved
// are shown: a draft is still the tenant's work. The view leaves out the approval and the bank account (§4).

public sealed record PortalStatementView(Guid Id, string IntermediaryCode, string PeriodCode, string Currency, decimal GrossAmount, decimal TaxAmount, decimal NetAmount,
    StatementStatus Status, DateOnly? PaidOn)
{
    public static PortalStatementView From(CommissionStatement s) => new(s.Id, s.IntermediaryCode, s.PeriodCode, s.Currency, s.GrossAmount, s.TaxAmount, s.NetAmount,
        s.Status, s.PaidOn);
}

public sealed record ListPortalStatements(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalStatementView>>;

public sealed record GetPortalStatement(Guid StatementId) : IQuery<PortalStatementView>;

/// <summary>A broker's approved and paid statements, by ID for cursor paging.</summary>
public interface IDistributionPortalQueries
{
    Task<IReadOnlyList<CommissionStatement>> Statements(IReadOnlyCollection<string> intermediaryCodes, Guid? afterId, int limit, CancellationToken cancellationToken);
}

internal sealed class PortalStatementHandlers(IDistributionRepository distribution, IDistributionPortalQueries queries, IExecutionContext execution) :
    IQueryHandler<ListPortalStatements, IReadOnlyList<PortalStatementView>>,
    IQueryHandler<GetPortalStatement, PortalStatementView>
{
    private ExternalSubject Who => execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");

    public async Task<Result<IReadOnlyList<PortalStatementView>>> Handle(ListPortalStatements query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalStatementView>>(
            (await queries.Statements(Who.IntermediaryCodes, query.AfterId, query.Limit, cancellationToken)).Select(PortalStatementView.From).ToList());

    public async Task<Result<PortalStatementView>> Handle(GetPortalStatement query, CancellationToken cancellationToken) =>
        await distribution.Statement(query.StatementId, cancellationToken) is { } statement && Who.Brokers(statement.IntermediaryCode)
        && statement.Status != StatementStatus.Draft
            ? PortalStatementView.From(statement)
            : Error.NotFound(ErrorCodes.NotFound, "The statement was not found.");
}
