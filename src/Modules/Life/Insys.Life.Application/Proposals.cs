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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Distribution.Contracts;
using Insys.Life.Domain;
using Insys.Life.Domain.Proposals;
using Insys.Party.Contracts;
using Insys.ProductFactory.Contracts;

namespace Insys.Life.Application;

/// <summary>An insured life: the party (whose date of birth gives the age next birthday at inception), role and smoker status.</summary>
public sealed record InsuredLifeInput(Guid PartyId, string LifeRole, string SmokerStatus, string? OccupationCode);

public sealed record DisclosureInput(Guid InsuredPartyId, string QuestionCode, int QuestionVersion, JsonElement Answer);

/// <summary>Command CaptureProposal (LIF-001): a Life product version on sale, the insured lives, covers, disclosures and nominations.</summary>
public sealed record CaptureProposal(string ProductCode, int? ProductVersion, Guid ApplicantPartyId, string? IntermediaryCode, string ChannelCode, string Currency,
    DateOnly InceptionDate, string Frequency, string CollectionMethod, Guid? PayerPartyId, IReadOnlyList<InsuredLifeInput> Lives, IReadOnlyList<CoverRequest> Covers,
    IReadOnlyList<DisclosureInput>? Disclosures, IReadOnlyList<BeneficiaryNomination>? Nominations) : ICommand<ProposalView>;

public sealed record SubmitLifeProposal(Guid ProposalId) : ICommand<ProposalView>;

public sealed record WithdrawLifeProposal(Guid ProposalId) : ICommand<ProposalView>;

public sealed record GetLifeProposal(Guid ProposalId) : IQuery<ProposalView>;

public sealed class CaptureProposalValidator : AbstractValidator<CaptureProposal>
{
    public CaptureProposalValidator()
    {
        RuleFor(c => c.ProductCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ApplicantPartyId).NotEmpty();
        RuleFor(c => c.IntermediaryCode).MaximumLength(15);
        RuleFor(c => c.ChannelCode).NotEmpty().MaximumLength(12);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Frequency).NotEmpty().MaximumLength(10);
        RuleFor(c => c.CollectionMethod).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Lives).NotEmpty();
        RuleForEach(c => c.Lives).ChildRules(l =>
        {
            l.RuleFor(x => x.PartyId).NotEmpty();
            l.RuleFor(x => x.LifeRole).NotEmpty().MaximumLength(10);
            l.RuleFor(x => x.SmokerStatus).NotEmpty().MaximumLength(10);
            l.RuleFor(x => x.OccupationCode).MaximumLength(10);
        });
        RuleFor(c => c.Covers).NotEmpty();
        RuleForEach(c => c.Covers).ChildRules(c =>
        {
            c.RuleFor(x => x.BenefitCode).NotEmpty().MaximumLength(20);
            c.RuleFor(x => x.SumAssured).GreaterThan(0).PrecisionScale(18, 2, true);
            c.RuleFor(x => x.TermYears).InclusiveBetween(1, 100);
        });
        RuleForEach(c => c.Disclosures).ChildRules(d => d.RuleFor(x => x.QuestionCode).NotEmpty().MaximumLength(20));
        RuleForEach(c => c.Nominations).ChildRules(n =>
        {
            n.RuleFor(x => x.PartyId).NotEmpty();
            n.RuleFor(x => x.RelationshipCode).NotEmpty().MaximumLength(15);
            n.RuleFor(x => x.SharePct).PrecisionScale(5, 2, true);
        });
    }
}

internal sealed class CaptureProposalHandler(ILifeRepository life, ILifeCodes codes, ISellableProducts products, IPersonFacts persons, IIntermediaries intermediaries,
    ITenantContext tenant, IBusinessDate businessDate, IAuditTrail audit) : ICommandHandler<CaptureProposal, ProposalView>
{
    public async Task<Result<ProposalView>> Handle(CaptureProposal command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var product = await Products.Sellable(products, tenantId, command.ProductCode, command.ProductVersion, businessDate.Today, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        // DST-001: a proposal through an intermediary needs one that may sell the product.
        if (command.IntermediaryCode is { Length: > 0 } intermediary
            && await intermediaries.CanSell(tenantId, intermediary, product.Value.ProductCode, IntermediaryTransactions.NewBusiness, businessDate.Today, cancellationToken)
                is { } refusal)
        {
            return Error.Validation(refusal.Code, refusal.Message);
        }

        var lives = new List<InsuredLifeRequest>();
        foreach (var life in command.Lives)
        {
            if (await persons.BirthDate(tenantId, life.PartyId, cancellationToken) is not { } birthDate)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Party {life.PartyId} is not a person with a date of birth.");
            }

            lives.Add(new InsuredLifeRequest(life.PartyId, life.LifeRole, Ages.NextBirthday(birthDate, command.InceptionDate), life.SmokerStatus, life.OccupationCode));
        }

        var nominations = command.Nominations ?? [];
        foreach (var relationship in nominations.Select(n => n.RelationshipCode).Distinct())
        {
            if (!await codes.Exists(LifeCode.BeneficiaryRelationship, relationship, businessDate.Today, cancellationToken))
            {
                return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{relationship} is not a beneficiary relationship in force.");
            }
        }

        var disclosures = (command.Disclosures ?? []).Select(d => new DisclosureRequest(d.InsuredPartyId, d.QuestionCode, d.QuestionVersion, d.Answer.GetRawText())).ToList();
        var id = Guid.CreateVersion7();
        var proposal = LifeProposal.CaptureProposal(id, Numbers.Of("LP", id, 15), product.Value, command.ApplicantPartyId, command.IntermediaryCode, command.ChannelCode,
            command.Currency, command.InceptionDate, new ProposalBilling(command.PayerPartyId ?? command.ApplicantPartyId, command.Frequency, command.CollectionMethod),
            lives, command.Covers, disclosures, nominations, await codes.NominationRule(product.Value.ProductCode, businessDate.Today, cancellationToken));
        if (proposal.IsFailure)
        {
            return proposal.Error;
        }

        life.Add(proposal.Value);
        audit.Record(new AuditEntry("LIFE", "CaptureProposal", nameof(LifeProposal), id.ToString()));
        return ProposalView.From(proposal.Value);
    }
}

internal sealed class SubmitLifeProposalHandler(ILifeRepository life, IClock clock, IAuditTrail audit) : ICommandHandler<SubmitLifeProposal, ProposalView>
{
    public async Task<Result<ProposalView>> Handle(SubmitLifeProposal command, CancellationToken cancellationToken)
    {
        if (await life.Proposal(command.ProposalId, cancellationToken) is not { } proposal)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
        }

        if (proposal.Status != ProposalStatus.Quoted)
        {
            return proposal.Status is ProposalStatus.Submitted or ProposalStatus.InUnderwriting
                ? ProposalView.From(proposal)
                : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Proposal {proposal.ProposalNumber} is {proposal.Status}; it is submitted once quoted.");
        }

        audit.Record(new AuditEntry("LIFE", "SubmitProposal", nameof(LifeProposal), proposal.Id.ToString()));
        var result = proposal.SubmitProposal(clock.UtcNow);
        return result.IsFailure ? result.Error : ProposalView.From(proposal);
    }
}

internal sealed class WithdrawLifeProposalHandler(ILifeRepository life, IClock clock, IAuditTrail audit) : ICommandHandler<WithdrawLifeProposal, ProposalView>
{
    public async Task<Result<ProposalView>> Handle(WithdrawLifeProposal command, CancellationToken cancellationToken)
    {
        if (await life.Proposal(command.ProposalId, cancellationToken) is not { } proposal)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
        }

        audit.Record(new AuditEntry("LIFE", "WithdrawProposal", nameof(LifeProposal), proposal.Id.ToString()));
        var result = proposal.WithdrawProposal(clock.UtcNow);
        return result.IsFailure ? result.Error : ProposalView.From(proposal);
    }
}

internal sealed class GetLifeProposalHandler(ILifeRepository life) : IQueryHandler<GetLifeProposal, ProposalView>
{
    public async Task<Result<ProposalView>> Handle(GetLifeProposal query, CancellationToken cancellationToken) =>
        await life.Proposal(query.ProposalId, cancellationToken) is { } proposal ? ProposalView.From(proposal) : Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
}
