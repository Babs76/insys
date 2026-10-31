/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Distribution.Contracts;
using Insys.Health.Contracts;
using Insys.Identity.Domain.Users;
using Insys.Party.Contracts;

namespace Insys.Identity.Application.Users;

/// <summary>
/// Command LinkSubject (external-user-access.md §1): the external user is the party, intermediary or provider named,
/// once a second person approves it. Give the key that matches the kind.
/// </summary>
public sealed record LinkSubject(Guid UserId, SubjectKind Kind, Guid? PartyId, string? IntermediaryCode, string? ProviderId, long ExpectedVersion) : ICommand<long>, IReturnsSavedVersion;

public sealed record EndSubjectLink(Guid UserId, long ExpectedVersion) : ICommand<long>, IReturnsSavedVersion;

public sealed record GetSubjectLink(Guid UserId) : IQuery<SubjectLinkView>;

public sealed record SubjectLinkView(Guid Id, SubjectKind Kind, Guid? PartyId, string? IntermediaryCode, string? ProviderId, SubjectLinkStatus Status, Guid RequestedBy,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, Workflow.Contracts.LatestRejection? LatestRejection = null)
{
    public static SubjectLinkView From(SubjectLink l) => new(l.Id, l.Kind, l.PartyId, l.IntermediaryCode, l.ProviderId, l.Status, l.RequestedBy, l.DecidedBy, l.DecidedAt);
}

public sealed class LinkSubjectValidator : AbstractValidator<LinkSubject>
{
    public LinkSubjectValidator()
    {
        RuleFor(c => c.PartyId).NotNull().When(c => c.Kind == SubjectKind.Party);
        RuleFor(c => c.IntermediaryCode).NotEmpty().MaximumLength(15).When(c => c.Kind == SubjectKind.Intermediary);
        RuleFor(c => c.ProviderId).NotEmpty().MaximumLength(15).When(c => c.Kind == SubjectKind.Provider);
        RuleFor(c => c).Must(c => (c.PartyId is null ? 0 : 1) + (string.IsNullOrEmpty(c.IntermediaryCode) ? 0 : 1) + (string.IsNullOrEmpty(c.ProviderId) ? 0 : 1) == 1)
            .WithName("subject").WithMessage("Give exactly one of partyId, intermediaryCode and providerId, the one the kind names.");
    }
}

internal sealed class LinkSubjectHandler(IUserRepository users, IExecutionContext execution, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit, IPartyDirectory parties, IIntermediaries intermediaries, IProviderDirectory providers) : ICommandHandler<LinkSubject, long>
{
    public async Task<Result<long>> Handle(LinkSubject command, CancellationToken cancellationToken)
    {
        var administrator = Administrator.Require(execution);
        if (administrator.IsFailure)
        {
            return administrator.Error;
        }

        var user = await Administrator.Load(users, command.UserId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error;
        }

        // The subject is in force: a party not merged away; an active, licensed intermediary; an active provider.
        var tenantId = tenant.RequireTenantId();
        var exists = command.Kind switch
        {
            SubjectKind.Party => await parties.Exists(tenantId, command.PartyId!.Value, cancellationToken),
            SubjectKind.Intermediary => await intermediaries.IsActiveAndLicensed(tenantId, command.IntermediaryCode!, businessDate.Today, cancellationToken),
            _ => await providers.IsActive(tenantId, command.ProviderId!, businessDate.Today, cancellationToken),
        };
        if (!exists)
        {
            return Error.Validation(IdentityErrorCodes.SubjectNotFound, $"The {command.Kind} to link to is not in force (an intermediary must be active and licensed).");
        }

        audit.Record(new AuditEntry("IDENTITY", "LinkSubject", nameof(UserAccount), command.UserId.ToString(),
            new Dictionary<string, string?> { ["kind"] = command.Kind.ToString() }));
        var result = user.Value.LinkSubject(command.Kind, command.PartyId, command.IntermediaryCode, command.ProviderId, administrator.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : user.Value.RowVersion;
    }
}

internal sealed class EndSubjectLinkHandler(IUserRepository users, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<EndSubjectLink, long>
{
    public async Task<Result<long>> Handle(EndSubjectLink command, CancellationToken cancellationToken)
    {
        var administrator = Administrator.Require(execution);
        if (administrator.IsFailure)
        {
            return administrator.Error;
        }

        var user = await Administrator.Load(users, command.UserId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error;
        }

        audit.Record(new AuditEntry("IDENTITY", "EndSubjectLink", nameof(UserAccount), command.UserId.ToString()));
        var result = user.Value.EndSubjectLink(administrator.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : user.Value.RowVersion;
    }
}

internal sealed class GetSubjectLinkHandler(IUserRepository users, Workflow.Contracts.IApprovalHistory history) : IQueryHandler<GetSubjectLink, SubjectLinkView>
{
    public async Task<Result<SubjectLinkView>> Handle(GetSubjectLink query, CancellationToken cancellationToken) =>
        await users.Get(query.UserId, cancellationToken) is { CurrentLink: { } link }
            ? SubjectLinkView.From(link) with
            {
                LatestRejection = await history.Latest(Contracts.IdentityApprovals.SubjectLinkBusinessRefType, link.Id, cancellationToken),
            }
            : Error.NotFound(IdentityErrorCodes.SubjectLinkNotFound, "The user has no link pending or in effect.");
}
