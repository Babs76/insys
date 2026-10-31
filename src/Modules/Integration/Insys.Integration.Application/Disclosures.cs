/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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

namespace Insys.Integration.Application;

// F060 (addenda/finance-and-compliance.md §4.6).

public sealed record DisclosureTextView(string NoticeType, string DisplayPoint, string Jurisdiction, string Language, int TextVersion, string Text);

public sealed record GetDisclosureText(string DisplayPoint, string NoticeType, string Jurisdiction, string Language) : IQuery<DisclosureTextView>;

/// <param name="PartyId">Staff name the party; on the portal it is the caller, whatever is sent.</param>
public sealed record AcknowledgeDisclosure(Guid? PartyId, string DisplayPoint, string NoticeType, string Jurisdiction, string Language, string? BusinessRefType, Guid? BusinessRefId) : ICommand<DisclosureAcknowledgementView>;

public sealed record DisclosureAcknowledgementView(Guid Id, Guid PartyId, string NoticeType, string DisplayPoint, int TextVersion, DateTimeOffset AcknowledgedAt);

public interface IDisclosures
{
    Task<DisclosureTextView?> Text(Guid tenantId, string displayPoint, string noticeType, string jurisdiction, string language, DateOnly day, CancellationToken cancellationToken);

    Task Record(Guid tenantId, Guid id, Guid partyId, DisclosureTextView text, string? businessRefType, Guid? businessRefId, DateTimeOffset at, string? channelCode, string recordedBy, CancellationToken cancellationToken);
}

public sealed class AcknowledgeDisclosureValidator : AbstractValidator<AcknowledgeDisclosure>
{
    public AcknowledgeDisclosureValidator()
    {
        RuleFor(c => c.DisplayPoint).NotEmpty().MaximumLength(40);
        RuleFor(c => c.NoticeType).NotEmpty().MaximumLength(20);
        RuleFor(c => c.BusinessRefType).MaximumLength(30);
    }
}

internal sealed class DisclosureHandlers(IDisclosures disclosures, IExecutionContext execution, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    IQueryHandler<GetDisclosureText, DisclosureTextView>,
    ICommandHandler<AcknowledgeDisclosure, DisclosureAcknowledgementView>
{
    private static Error NoText => Error.NotFound(ErrorCodes.NotFound, "No such text is in force.");

    public async Task<Result<DisclosureTextView>> Handle(GetDisclosureText query, CancellationToken cancellationToken) =>
        await disclosures.Text(tenant.RequireTenantId(), query.DisplayPoint, query.NoticeType, query.Jurisdiction, query.Language, businessDate.Today, cancellationToken) is { } text ? text : NoText;

    public async Task<Result<DisclosureAcknowledgementView>> Handle(AcknowledgeDisclosure command, CancellationToken cancellationToken)
    {
        var party = execution.External is { } who ? who.Kind == ExternalSubject.Party ? who.PartyId : null : command.PartyId;
        if (party is not { } partyId)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A disclosure is acknowledged by a party.");
        }

        var tenantId = tenant.RequireTenantId();
        if (await disclosures.Text(tenantId, command.DisplayPoint, command.NoticeType, command.Jurisdiction, command.Language, businessDate.Today, cancellationToken) is not { } text)
        {
            return NoText;
        }

        var id = Guid.CreateVersion7();
        var at = clock.UtcNow;
        await disclosures.Record(tenantId, id, partyId, text, command.BusinessRefType, command.BusinessRefId, at, execution.External?.Kind, execution.ActorId, cancellationToken);
        audit.Record(new AuditEntry("CHANNEL", "AcknowledgeDisclosure", "DisclosureAcknowledgement", id.ToString(), new Dictionary<string, string?> { ["point"] = text.DisplayPoint, ["version"] = text.TextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return new DisclosureAcknowledgementView(id, partyId, text.NoticeType, text.DisplayPoint, text.TextVersion, at);
    }
}
