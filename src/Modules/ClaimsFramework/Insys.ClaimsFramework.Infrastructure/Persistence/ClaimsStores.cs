/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Application;
using Insys.ClaimsFramework.Domain.Claims;
using Insys.ClaimsFramework.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.ClaimsFramework.Infrastructure.Persistence;

/// <summary>
/// The Claims Framework's records, scoped as the domain claim they belong to (F103, addenda/access-scopes.md §4): by the
/// product and organisation node recorded at registration. Outside the caller's scope for the endpoint's permission, a read
/// finds nothing (404) and an action is refused (403 OUT_OF_SCOPE). Registration, jobs and consumers are unrestricted.
/// </summary>
/// <summary>The claim register search (BR-034), in the query so paging stays correct, within the caller's scope (F103).</summary>
internal sealed class ClaimRegisterSearch(ClaimsFrameworkDbContext db, IExecutionContext execution) : IClaimRegisterSearch
{
    public async Task<IReadOnlyList<ClaimRegisterSummary>> Search(SearchClaimRegister query, CancellationToken cancellationToken)
    {
        var claims = db.Claims.AsNoTracking().InScope(execution.CurrentScope, c => c.ProductCode, c => c.OrgNodeCode);
        if (query.ClaimNumber is { } number)
        {
            claims = claims.Where(c => c.ClaimNumber == number);
        }

        if (query.Domain is { } domain)
        {
            claims = claims.Where(c => c.Domain == domain);
        }

        if (query.PolicyRef is { } policy)
        {
            claims = claims.Where(c => c.PolicyRef == policy);
        }

        if (query.RegisteredFrom is { } from)
        {
            claims = claims.Where(c => c.RegisteredAt >= from);
        }

        if (query.RegisteredTo is { } to)
        {
            claims = claims.Where(c => c.RegisteredAt < to);
        }

        if (query.AfterId is { } after)
        {
            claims = claims.Where(c => c.Id.CompareTo(after) > 0);
        }

        return await claims.OrderBy(c => c.Id).Take(query.Limit)
            .Select(c => new ClaimRegisterSummary(c.Id, c.ClaimNumber, c.Domain, c.DomainClaimId, c.PolicyRef, c.RegisteredAt, c.ProductCode))
            .ToListAsync(cancellationToken);
    }
}

internal sealed class ClaimsRepository(ClaimsFrameworkDbContext db, IExecutionContext execution) : IClaimsRepository
{
    public async Task<ClaimRegistration?> ByNumber(string claimNumber, CancellationToken cancellationToken)
    {
        var claim = await Claims().SingleOrDefaultAsync(c => c.ClaimNumber == claimNumber, cancellationToken);
        return execution.InScope(claim, claim?.ProductCode, claim?.OrgNodeCode);
    }

    public Task<ClaimRegistration?> ByDomainClaim(string domain, Guid domainClaimId, CancellationToken cancellationToken) =>
        Claims().SingleOrDefaultAsync(c => c.Domain == domain && c.DomainClaimId == domainClaimId, cancellationToken);

    public async Task<IReadOnlyList<FinancialMovement>> Movements(string claimNumber, CancellationToken cancellationToken)
    {
        var stored = await db.Movements.Where(m => m.ClaimNumber == claimNumber).ToListAsync(cancellationToken);

        // Movements added in this unit of work but not yet saved count too, so two in one request see each other.
        var added = db.ChangeTracker.Entries<FinancialMovement>().Where(e => e.State == EntityState.Added && e.Entity.ClaimNumber == claimNumber).Select(e => e.Entity);
        return stored.Concat(added).Distinct().ToList();
    }

    public async Task<IReadOnlyList<ClaimPaymentInstruction>> Instructions(string claimNumber, CancellationToken cancellationToken) =>
        await db.Instructions.AsNoTracking().Where(i => i.ClaimNumber == claimNumber).OrderBy(i => i.Id).ToListAsync(cancellationToken);

    public async Task<ClaimPaymentInstruction?> Instruction(Guid id, CancellationToken cancellationToken) =>
        await OfClaim(await db.Instructions.SingleOrDefaultAsync(i => i.Id == id, cancellationToken), cancellationToken);

    public async Task<ClaimPaymentInstruction?> Instruction(string claimNumber, string instructionRef, CancellationToken cancellationToken) =>
        await OfClaim(await db.Instructions.SingleOrDefaultAsync(i => i.ClaimNumber == claimNumber && i.InstructionRef == instructionRef, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ClaimPaymentInstruction>> HeldOnPolicy(string domain, Guid? policyRef, string? claimNumber, string reason,
        CancellationToken cancellationToken)
    {
        var numbers = db.Claims.Where(c => c.Domain == domain && (policyRef == null || c.PolicyRef == policyRef) && (claimNumber == null || c.ClaimNumber == claimNumber))
            .Select(c => c.ClaimNumber);
        var held = new List<ClaimPaymentInstruction>();
        foreach (var instruction in await db.Instructions.Where(i => i.HeldReason == reason && i.Status == InstructionStatus.Approved && numbers.Contains(i.ClaimNumber))
                     .OrderBy(i => i.Id).ToListAsync(cancellationToken))
        {
            if (await OfClaim(instruction, cancellationToken) is { } inScope)
            {
                held.Add(inScope);
            }
        }

        return held;
    }

    public async Task<IReadOnlyList<ClaimPaymentInstruction>> HeldFor(Guid payeePartyId, string reason, CancellationToken cancellationToken)
    {
        var held = new List<ClaimPaymentInstruction>();
        foreach (var instruction in await db.Instructions.Where(i => i.PayeePartyId == payeePartyId && i.HeldReason == reason).OrderBy(i => i.Id).ToListAsync(cancellationToken))
        {
            if (await OfClaim(instruction, cancellationToken) is { } inScope)
            {
                held.Add(inScope);
            }
        }

        return held;
    }

    private async Task<ClaimPaymentInstruction?> OfClaim(ClaimPaymentInstruction? instruction, CancellationToken cancellationToken)
    {
        if (instruction is null || execution.CurrentScope.IsUnrestricted)
        {
            return instruction;
        }

        var owner = await db.Claims.AsNoTracking().Where(c => c.ClaimNumber == instruction.ClaimNumber).Select(c => new { c.ProductCode, c.OrgNodeCode })
            .SingleOrDefaultAsync(cancellationToken);
        return execution.InScope(instruction, owner?.ProductCode, owner?.OrgNodeCode);
    }

    public void Add(ClaimRegistration registration) => db.Claims.Add(registration);

    public void Add(FinancialMovement movement) => db.Movements.Add(movement);

    public void Add(ClaimPaymentInstruction instruction) => db.Instructions.Add(instruction);

    private IQueryable<ClaimRegistration> Claims() =>
        db.Claims.Include(c => c.Evidence).Include(c => c.Communications).Include(c => c.FraudHits).Include(c => c.FraudEvaluations).AsSplitQuery();
}

internal sealed class ClaimsCodes(ClaimsFrameworkDbContext db) : IClaimsCodes
{
    public Task<string?> NumberPrefix(string domain, DateOnly day, CancellationToken cancellationToken) =>
        InForce<ClaimNumberingSchemes>(day).Where(s => s.Domain == domain).OrderBy(s => s.Code).Select(s => (string?)s.Prefix).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> EvidenceTypeExists(string code, DateOnly day, CancellationToken cancellationToken) =>
        InForce<EvidenceTypes>(day).AnyAsync(e => e.Code == code, cancellationToken);

    public Task<bool> ReserveTypeExists(string code, DateOnly day, CancellationToken cancellationToken) =>
        InForce<ReserveTypes>(day).AnyAsync(r => r.Code == code, cancellationToken);

    public async Task<Result<IReadOnlyList<FraudIndicator>>> FraudIndicators(DateOnly day, CancellationToken cancellationToken)
    {
        var rows = await InForce<FraudIndicatorDefinitions>(day).OrderBy(d => d.IndicatorCode).ToListAsync(cancellationToken);
        var indicators = new List<FraudIndicator>();
        foreach (var row in rows)
        {
            var logic = Expression.Parse(row.Logic);
            if (logic.IsFailure)
            {
                return Error.Validation(logic.Error.Code, $"Fraud indicator {row.IndicatorCode}: {logic.Error.Message}");
            }

            indicators.Add(new FraudIndicator(row.IndicatorCode, logic.Value, row.Score, row.ReferralThreshold));
        }

        return indicators;
    }

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(ClaimsFrameworkStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal sealed class ClaimsEventMapper(Microsoft.Extensions.Options.IOptions<ClaimsPaymentOptions> heldReleaseRole) : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        ClaimNumberAllocated e => new Contracts.ClaimNumberAllocated(e.ClaimNumber, e.Domain, e.DomainClaimId, e.PolicyRef),
        FraudIndicatorRaised e => new Contracts.FraudIndicatorRaised(e.ClaimNumber, e.IndicatorCode, e.Score, e.Referred),
        FraudIndicatorsSkipped e => new Contracts.FraudIndicatorsNotEvaluated(e.EvaluationId, e.ClaimNumber,
            e.Skipped.Select(s => new Contracts.SkippedFraudIndicator(s.IndicatorCode, s.Reason, s.MissingFacts, s.ErrorCode)).ToList()),
        ClaimReserveChanged e => new Contracts.ClaimReserveChanged(e.MovementId, e.ClaimNumber, e.Domain, e.ReserveType, e.Change, e.Outstanding, e.Currency, e.MovementDate),
        ClaimRecoveryRecorded e => new Contracts.ClaimRecoveryRecorded(e.MovementId, e.ClaimNumber, e.Domain, e.ReserveType, e.Amount, e.Currency, e.MovementDate),
        ClaimPaymentReleaseHeld e => new Contracts.ClaimPaymentReleaseHeld(e.InstructionId, e.ClaimNumber, e.Domain, e.Reason, e.Amount, e.Currency, e.ReserveType,
            heldReleaseRole.Value.HeldReleaseRole),
        PayeeScreeningNeeded e => new BuildingBlocks.Contracts.Payouts.PayeeScreeningRequested(e.PartyId),
        ClaimPaymentInstructed e => new Contracts.ClaimPaymentInstructed(e.InstructionId, e.ClaimNumber, e.Domain, e.PayeePartyId, e.BankAccountId, e.Amount, e.Currency,
            e.Attempt),
        ClaimPaymentCompleted e => new Contracts.ClaimPaymentCompleted(e.InstructionId, e.MovementId, e.ClaimNumber, e.Domain, e.ReserveType, e.Amount, e.Currency, e.PaidOn,
            e.PremiumOffset),
        InstructionSubmitted e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.ClaimsApprovals.InstructionBusinessRefType, e.InstructionId,
            Contracts.ClaimsApprovals.InstructionApprovalType, e.DraftedBy, e.Amount, e.Currency, $"Claim payment on {e.ClaimNumber}", e.SubjectHash),
        InstructionRetryRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.ClaimsApprovals.InstructionBusinessRefType, e.InstructionId,
            Contracts.ClaimsApprovals.InstructionApprovalType, e.RequestedBy, e.Amount, e.Currency, $"New payee details for the claim payment on {e.ClaimNumber}", e.SubjectHash),
        _ => null,
    };
}
