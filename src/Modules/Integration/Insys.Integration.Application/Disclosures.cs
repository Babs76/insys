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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Contracts;

namespace Insys.Integration.Application;

// F060 (addenda/finance-and-compliance.md §4.6).

public sealed record DisclosureTextView(string NoticeType, string DisplayPoint, string Jurisdiction, string Language, int TextVersion, string Text);

public sealed record GetDisclosureText(string DisplayPoint, string NoticeType, string Jurisdiction, string Language) : IQuery<DisclosureTextView>;

/// <param name="PartyId">Staff name the party; on the portal it is the caller, whatever is sent.</param>
public sealed record AcknowledgeDisclosure(Guid? PartyId, string DisplayPoint, string NoticeType, string Jurisdiction, string Language, string? BusinessRefType, Guid? BusinessRefId) : ICommand<DisclosureAcknowledgementView>;

/// <summary>
/// DE-039 part 2 (Ade, 25 November 2026; OQ-201): a broker records its client's acknowledgement of the text in force, for its own clients only (BR-091), saying how
/// the notice was presented. TODO(spec): OQ-201, compliance to confirm per market.
/// </summary>
/// <param name="PresentationMethod">IN_PERSON, PHONE, EMAIL or OTHER.</param>
/// <param name="PresentationDetail">The reason or detail; required with OTHER, at most 200 characters.</param>
public sealed record BrokerAcknowledgeDisclosure(Guid PartyId, string DisplayPoint, string NoticeType, string Jurisdiction, string Language, string? BusinessRefType, Guid? BusinessRefId,
    string? PresentationMethod, string? PresentationDetail) : ICommand<DisclosureAcknowledgementView>;

/// <summary>The text a broker reads, with whether it may record the acknowledgement in this tenant (false: the screen shows the notice read-only).</summary>
public sealed record BrokerDisclosureTextView(string NoticeType, string DisplayPoint, string Jurisdiction, string Language, int TextVersion, string Text, bool BrokerAcknowledgementAllowed);

public sealed record GetBrokerDisclosureText(string DisplayPoint, string NoticeType, string Jurisdiction, string Language) : IQuery<BrokerDisclosureTextView>;

/// <summary>DE-039: who recorded an acknowledgement. SELF: the customer in the member portal; BROKER: their broker; STAFF: the insurer's staff.</summary>
public static class DisclosureRecorders
{
    public const string Self = "SELF";
    public const string Broker = "BROKER";
    public const string Staff = "STAFF";
}

/// <summary>DE-039: how a broker presented the notice.</summary>
public static class PresentationMethods
{
    public const string InPerson = "IN_PERSON";
    public const string Phone = "PHONE";
    public const string Email = "EMAIL";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All = [InPerson, Phone, Email, Other];
}

/// <summary>
/// One of the member's acknowledgements as their disclosures view shows it, newest first. RecordedBy is SELF, BROKER or STAFF; with BROKER, the broker's code and how
/// the notice was presented (PresentationMethod, and PresentationDetail with OTHER).
/// </summary>
public sealed record PortalDisclosureAcknowledgementView(Guid Id, string NoticeType, string DisplayPoint, string? Jurisdiction, string Language, int TextVersion, string? BusinessRefType,
    Guid? BusinessRefId, DateTimeOffset AcknowledgedAt, string RecordedBy, string? IntermediaryCode, string? PresentationMethod, string? PresentationDetail);

public sealed record ListPortalDisclosureAcknowledgements : IQuery<IReadOnlyList<PortalDisclosureAcknowledgementView>>;

/// <summary>DE-039, OQ-201: the tenant's disclosure setting; IsDefault until the tenant sets its own (brokers may record).</summary>
public sealed record DisclosureSettingView(bool BrokerAcknowledgementAllowed, bool IsDefault, long RowVersion);

public sealed record GetDisclosureSetting : IQuery<DisclosureSettingView>;

/// <param name="ExpectedVersion">From If-Match; required once the tenant has its own setting.</param>
public sealed record ChangeDisclosureSetting(bool? BrokerAcknowledgementAllowed, long? ExpectedVersion) : ICommand<long>;

public sealed class ChangeDisclosureSettingValidator : AbstractValidator<ChangeDisclosureSetting>
{
    public ChangeDisclosureSettingValidator() => RuleFor(c => c.BrokerAcknowledgementAllowed).NotNull();
}

/// <summary>Who recorded an acknowledgement, and how it was presented when a broker did (DE-039).</summary>
public sealed record AcknowledgementRecorder(string? ChannelCode, string RecordedBy, string? IntermediaryCode = null, string? PresentationMethod = null, string? PresentationDetail = null);

/// <param name="Outstanding">DE-039: what is still outstanding at the display point for the party and record after this acknowledgement, as DISCLOSURE_REQUIRED names it; empty when the step can be retried.</param>
public sealed record DisclosureAcknowledgementView(Guid Id, Guid PartyId, string NoticeType, string DisplayPoint, int TextVersion, DateTimeOffset AcknowledgedAt, IReadOnlyList<DisclosureNotice> Outstanding);

/// <summary>
/// DE-039 (Ade, 25 November 2026): the language an outstanding notice is opened in. The party's preferred language if a text is in force in it, else the tenant's
/// default language if one is, else the only language the notice is in force in. With several and none of those, null: the person chooses (no "first by code").
/// </summary>
public static class DisclosureLanguage
{
    public static string? Choose(IReadOnlyCollection<string> available, string? preferred, string? tenantDefault)
    {
        ArgumentOutOfRangeException.ThrowIfZero(available.Count);
        foreach (var candidate in new[] { preferred?.Trim(), tenantDefault?.Trim() })
        {
            if (!string.IsNullOrEmpty(candidate) && available.FirstOrDefault(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                return found;
            }
        }

        return available.Count == 1 ? available.First() : null;
    }
}

public interface IDisclosures
{
    Task<DisclosureTextView?> Text(Guid tenantId, string displayPoint, string noticeType, string jurisdiction, string language, DateOnly day, CancellationToken cancellationToken);

    Task Record(Guid tenantId, Guid id, Guid partyId, DisclosureTextView text, string? businessRefType, Guid? businessRefId, DateTimeOffset at, AcknowledgementRecorder recorder, CancellationToken cancellationToken);

    /// <summary>The party's acknowledgements, newest first.</summary>
    Task<IReadOnlyList<PortalDisclosureAcknowledgementView>> Acknowledgements(Guid tenantId, Guid partyId, CancellationToken cancellationToken);

    /// <summary>The channel in force whose client_ids hold the OAuth client (F005 §1), or null.</summary>
    Task<string?> ChannelOf(Guid tenantId, string? clientId, DateOnly day, CancellationToken cancellationToken);

    Task<DisclosureSettingView> Setting(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Inserts the tenant's first setting (expectedVersion null) or updates it at that version; null when it has changed meanwhile.</summary>
    Task<long?> SaveSetting(Guid tenantId, bool brokerAcknowledgementAllowed, long? expectedVersion, string by, DateTimeOffset at, CancellationToken cancellationToken);
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

public sealed class BrokerAcknowledgeDisclosureValidator : AbstractValidator<BrokerAcknowledgeDisclosure>
{
    public BrokerAcknowledgeDisclosureValidator()
    {
        RuleFor(c => c.PartyId).NotEmpty();
        RuleFor(c => c.DisplayPoint).NotEmpty().MaximumLength(40);
        RuleFor(c => c.NoticeType).NotEmpty().MaximumLength(20);
        RuleFor(c => c.BusinessRefType).MaximumLength(30);
        RuleFor(c => c.PresentationMethod).NotEmpty().Must(m => m is null || PresentationMethods.All.Contains(m))
            .WithMessage("The presentation method is IN_PERSON, PHONE, EMAIL or OTHER.");
        RuleFor(c => c.PresentationDetail).MaximumLength(200);
        RuleFor(c => c.PresentationDetail).Must(d => !string.IsNullOrWhiteSpace(d)).When(c => c.PresentationMethod == PresentationMethods.Other)
            .WithMessage("Say how the notice was presented when the method is OTHER.");
    }
}

internal sealed class DisclosureHandlers(IDisclosures disclosures, IDisclosureGate gate, Party.Contracts.IBrokerClients brokerClients, IExecutionContext execution, ITenantContext tenant,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    IQueryHandler<GetDisclosureText, DisclosureTextView>,
    ICommandHandler<AcknowledgeDisclosure, DisclosureAcknowledgementView>,
    IQueryHandler<GetBrokerDisclosureText, BrokerDisclosureTextView>,
    ICommandHandler<BrokerAcknowledgeDisclosure, DisclosureAcknowledgementView>,
    IQueryHandler<ListPortalDisclosureAcknowledgements, IReadOnlyList<PortalDisclosureAcknowledgementView>>,
    IQueryHandler<GetDisclosureSetting, DisclosureSettingView>,
    ICommandHandler<ChangeDisclosureSetting, long>
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
        await disclosures.Record(tenantId, id, partyId, text, command.BusinessRefType, command.BusinessRefId, at, new AcknowledgementRecorder(execution.External?.Kind, execution.ActorId), cancellationToken);
        audit.Record(new AuditEntry("CHANNEL", "AcknowledgeDisclosure", "DisclosureAcknowledgement", id.ToString(), new Dictionary<string, string?> { ["point"] = text.DisplayPoint, ["version"] = text.TextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var outstanding = await gate.Outstanding(tenantId, partyId, text.DisplayPoint, command.BusinessRefType, command.BusinessRefId, businessDate.Today, cancellationToken);
        return new DisclosureAcknowledgementView(id, partyId, text.NoticeType, text.DisplayPoint, text.TextVersion, at, outstanding);
    }
    public async Task<Result<BrokerDisclosureTextView>> Handle(GetBrokerDisclosureText query, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (await disclosures.Text(tenantId, query.DisplayPoint, query.NoticeType, query.Jurisdiction, query.Language, businessDate.Today, cancellationToken) is not { } text)
        {
            return NoText;
        }

        var allowed = (await disclosures.Setting(tenantId, cancellationToken)).BrokerAcknowledgementAllowed;
        return new BrokerDisclosureTextView(text.NoticeType, text.DisplayPoint, text.Jurisdiction, text.Language, text.TextVersion, text.Text, allowed);
    }

    // TODO(spec): OQ-201 — a broker recording its client's acknowledgement is Ade's beta decision (25 November 2026); compliance to confirm per market.
    public async Task<Result<DisclosureAcknowledgementView>> Handle(BrokerAcknowledgeDisclosure command, CancellationToken cancellationToken)
    {
        if (execution.External is not { Kind: ExternalSubject.Intermediary } broker)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a broker records an acknowledgement for its client.");
        }

        var tenantId = tenant.RequireTenantId();
        if (!(await disclosures.Setting(tenantId, cancellationToken)).BrokerAcknowledgementAllowed)
        {
            return Error.Forbidden(ErrorCodes.DisclosureBrokerNotAllowed, "Only the customer can acknowledge this notice.");
        }

        // BR-091: the broker's own clients only, by the broker portal's rule; another's is not found, as one that does not exist.
        if (!await brokerClients.IsClient(tenantId, broker.IntermediaryCodes, command.PartyId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The client was not found.");
        }

        if (await disclosures.Text(tenantId, command.DisplayPoint, command.NoticeType, command.Jurisdiction, command.Language, businessDate.Today, cancellationToken) is not { } text)
        {
            return NoText;
        }

        var id = Guid.CreateVersion7();
        var at = clock.UtcNow;
        var detail = string.IsNullOrWhiteSpace(command.PresentationDetail) ? null : command.PresentationDetail.Trim();
        // The broker's channel: the channel in force for the token's client (F005 §1), else the external user's kind as other acknowledgements record it.
        var channel = execution.ChannelCode ?? await disclosures.ChannelOf(tenantId, execution.ClientId, businessDate.Today, cancellationToken) ?? broker.Kind;
        var recorder = new AcknowledgementRecorder(channel, execution.ActorId, broker.IntermediaryCode, command.PresentationMethod, detail);
        await disclosures.Record(tenantId, id, command.PartyId, text, command.BusinessRefType, command.BusinessRefId, at, recorder, cancellationToken);
        audit.Record(new AuditEntry("CHANNEL", "BrokerAcknowledgeDisclosure", "DisclosureAcknowledgement", id.ToString(), new Dictionary<string, string?>
        {
            ["party"] = command.PartyId.ToString(),
            ["point"] = text.DisplayPoint,
            ["notice"] = text.NoticeType,
            ["version"] = text.TextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["language"] = text.Language,
            ["broker"] = broker.IntermediaryCode,
            ["channel"] = recorder.ChannelCode,
            ["method"] = command.PresentationMethod,
        }));
        var outstanding = await gate.Outstanding(tenantId, command.PartyId, text.DisplayPoint, command.BusinessRefType, command.BusinessRefId, businessDate.Today, cancellationToken);
        return new DisclosureAcknowledgementView(id, command.PartyId, text.NoticeType, text.DisplayPoint, text.TextVersion, at, outstanding);
    }

    public async Task<Result<IReadOnlyList<PortalDisclosureAcknowledgementView>>> Handle(ListPortalDisclosureAcknowledgements query, CancellationToken cancellationToken) =>
        execution.External is { Kind: ExternalSubject.Party, PartyId: { } partyId }
            ? Result.Success(await disclosures.Acknowledgements(tenant.RequireTenantId(), partyId, cancellationToken))
            : Result.Success<IReadOnlyList<PortalDisclosureAcknowledgementView>>([]);

    public async Task<Result<DisclosureSettingView>> Handle(GetDisclosureSetting query, CancellationToken cancellationToken) =>
        await disclosures.Setting(tenant.RequireTenantId(), cancellationToken);

    public async Task<Result<long>> Handle(ChangeDisclosureSetting command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var current = await disclosures.Setting(tenantId, cancellationToken);
        if (command.ExpectedVersion is { } expected ? expected != current.RowVersion : !current.IsDefault)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The disclosure setting has changed since it was read.");
        }

        if (await disclosures.SaveSetting(tenantId, command.BrokerAcknowledgementAllowed!.Value, current.IsDefault ? null : current.RowVersion, execution.ActorId, clock.UtcNow, cancellationToken)
            is not { } version)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The disclosure setting has changed since it was read.");
        }

        audit.Record(new AuditEntry("CHANNEL", "ChangeDisclosureSetting", "DisclosureSetting", "disclosure",
            new Dictionary<string, string?> { ["brokerAcknowledgementAllowed"] = command.BrokerAcknowledgementAllowed == true ? "true" : "false" }));
        return version;
    }
}
