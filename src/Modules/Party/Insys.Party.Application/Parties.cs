/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using System.Text.RegularExpressions;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Contracts;
using Insys.Party.Domain.Parties;
using Insys.StaticData.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Party.Application;

public interface IPartyRepository
{
    Task<Domain.Parties.Party?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>The party that holds the bank account.</summary>
    Task<Domain.Parties.Party?> WithBankAccount(Guid bankAccountId, CancellationToken cancellationToken);

    Task<bool> IdentifierExists(string docType, string country, string hash, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConsentRecord>> Consents(Guid partyId, CancellationToken cancellationToken);

    void Add(Domain.Parties.Party party);

    void Add(ConsentRecord record);
}

/// <summary>The Party module's static data in force that commands check codes against.</summary>
public interface IPartyCodes
{
    /// <summary>The identity document type for the country, with its format pattern.</summary>
    Task<(bool Exists, string? FormatPattern)> IdentityDocumentType(string country, string docType, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether the role type allows the party kind and the domain.</summary>
    Task<bool> RoleAllowed(string roleCode, PartyKind kind, string domain, DateOnly day, CancellationToken cancellationToken);

    Task<bool> RelationshipTypeExists(string relationshipCode, DateOnly day, CancellationToken cancellationToken);

    Task<bool> ConsentPurposeExists(string purposeCode, DateOnly day, CancellationToken cancellationToken);
}

public sealed record AddressView(Guid Id, string AddressType, string Line1, string? Line2, string City, string? Subdivision, string? PostalCode, string Country,
    DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record ContactPointView(Guid Id, string ChannelType, string Value, bool IsPrimary, DateTimeOffset? VerifiedAt);

/// <summary>An identifier without its number, which is never returned (PTY-005).</summary>
public sealed record IdentifierView(Guid Id, string DocType, string Country, bool Verified, DateTimeOffset? VerifiedAt);

public sealed record RoleView(Guid Id, string RoleCode, string Domain, string ContextType, Guid ContextId, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record RelationshipView(Guid Id, Guid ToPartyId, string RelationshipCode, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record PartyView(Guid Id, string Kind, string DisplayName, string Status, Guid? MergedIntoId, IndividualData? Individual, OrganisationData? Organisation,
    DateOnly? DeceasedDate, IReadOnlyList<AddressView> Addresses, IReadOnlyList<ContactPointView> ContactPoints, IReadOnlyList<IdentifierView> Identifiers,
    IReadOnlyList<RoleView> Roles, IReadOnlyList<RelationshipView> Relationships, long RowVersion)
{
    public static PartyView From(Domain.Parties.Party p) => new(p.Id, p.Kind.ToString(), p.DisplayName, p.Status.ToString(), p.MergedIntoId,
        p.Individual is { } i ? new IndividualData(i.TitleCode, i.FirstNames, i.Surname, i.BirthDate, i.GenderCode, i.MaritalStatusCode, i.Nationality, i.PreferredLanguage) : null,
        p.Organisation is { } o ? new OrganisationData(o.LegalName, o.TradingName, o.LegalFormCode, o.RegistrationNumber, o.IndustryCode) : null,
        p.Individual?.DeceasedDate,
        p.Addresses.OrderBy(a => a.ValidFrom).Select(a => new AddressView(a.Id, a.AddressType, a.Line1, a.Line2, a.City, a.Subdivision, a.PostalCode, a.Country, a.ValidFrom, a.ValidTo)).ToList(),
        p.ContactPoints.Select(c => new ContactPointView(c.Id, c.ChannelType, c.Value, c.IsPrimary, c.VerifiedAt)).ToList(),
        p.Identifiers.Select(i => new IdentifierView(i.Id, i.DocType, i.Country, i.VerifiedAt is not null, i.VerifiedAt)).ToList(),
        p.Roles.Select(r => new RoleView(r.Id, r.RoleCode, r.Domain, r.ContextType, r.ContextId, r.ValidFrom, r.ValidTo)).ToList(),
        p.Relationships.Select(r => new RelationshipView(r.Id, r.ToPartyId, r.RelationshipCode, r.ValidFrom, r.ValidTo)).ToList(),
        p.RowVersion);
}

public sealed record ConsentView(string PurposeCode, bool Granted, DateTimeOffset CapturedAt, string CaptureChannel, string? EvidenceRef, DateTimeOffset? WithdrawnAt);

/// <summary>Command RegisterParty: exactly one of individual and organisation (PTY-001).</summary>
public sealed record RegisterParty(IndividualData? Individual, OrganisationData? Organisation) : ICommand<Guid>;

public sealed record UpdateIndividualDetails(Guid PartyId, IndividualData Details, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record UpdateOrganisationDetails(Guid PartyId, OrganisationData Details, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Command ChangePreferredLanguage: an ISO 639-1 code in the language register, or null to clear it (addendum §7).</summary>
public sealed record ChangePreferredLanguage(Guid PartyId, string? Language, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record AddAddress(Guid PartyId, AddressData Address, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record AddContactPoint(Guid PartyId, string ChannelType, string Value, bool IsPrimary, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Command AddIdentifier: the number is checked against the document type's format, encrypted, and hashed for lookup.</summary>
public sealed record AddIdentifier(Guid PartyId, string DocType, string Country, string Number, long ExpectedVersion) : ICommand<Guid>, IVersionedCommand;

/// <summary>Command AddBankAccount: the number is encrypted at once and never returned; the party's current account in the currency is superseded.</summary>
public sealed record AddBankAccount(Guid PartyId, string AccountNumber, string BankCode, string? BranchCode, string AccountHolderName, string AccountType, string Currency)
    : ICommand<BankAccountView>;

public sealed record ListBankAccounts(Guid PartyId) : IQuery<IReadOnlyList<BankAccountView>>;

/// <summary>A bank account without its number: the last four digits only.</summary>
public sealed record BankAccountView(Guid Id, Guid PartyId, string Last4, string BankCode, string? BranchCode, string AccountType, string Currency, string Status,
    DateTimeOffset? VerifiedAt, Guid? SupersededById)
{
    public static BankAccountView From(BankAccount a) => new(a.Id, a.PartyId, a.AccountNumberLast4, a.BankCode, a.BranchCode, a.AccountType, a.Currency,
        UpperCase(a.Status), a.VerifiedAt, a.SupersededById);

    private static string UpperCase(BankAccountStatus status) => status switch
    {
        BankAccountStatus.PendingVerification => "PENDING_VERIFICATION",
        BankAccountStatus.Active => "ACTIVE",
        BankAccountStatus.Rejected => "REJECTED",
        _ => "SUPERSEDED",
    };
}

public sealed class AddBankAccountValidator : AbstractValidator<AddBankAccount>
{
    public AddBankAccountValidator()
    {
        RuleFor(c => c.AccountNumber).NotEmpty().Must(n => n.Count(char.IsAsciiDigit) is >= 6 and <= 34 && n.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-'))
            .WithMessage("An account number has 6 to 34 digits.");
        RuleFor(c => c.BankCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.BranchCode).MaximumLength(20);
        RuleFor(c => c.AccountHolderName).NotEmpty().MaximumLength(140);
        RuleFor(c => c.AccountType).Must(t => t is "CURRENT" or "SAVINGS" or "TRANSMISSION" or "OTHER").WithMessage("CURRENT, SAVINGS, TRANSMISSION or OTHER.");
        RuleFor(c => c.Currency).Length(3);
    }
}

internal sealed class AddBankAccountHandler(IPartyRepository parties, IFieldProtector protector, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<AddBankAccount, BankAccountView>
{
    public async Task<Result<BankAccountView>> Handle(AddBankAccount command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(execution.ActorId, out var user) || execution.ActorType != ActorType.User)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
        }

        if (await parties.Get(command.PartyId, cancellationToken) is not { } party)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        var number = new string(command.AccountNumber.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        var protectedValue = protector.Protect(number);
        var added = party.AddBankAccount(new BankAccountData(protectedValue.Encrypted, protectedValue.Hash, protectedValue.Last4, command.BankCode.Trim(),
            command.BranchCode?.Trim(), command.AccountHolderName.Trim(), command.AccountType, command.Currency), user, clock.UtcNow);
        if (added.IsFailure)
        {
            return added.Error;
        }

        audit.Record(new AuditEntry("PARTY", "AddBankAccount", nameof(BankAccount), added.Value.ToString(),
            new Dictionary<string, string?> { ["party"] = party.Id.ToString(), ["last4"] = protectedValue.Last4 }));
        return BankAccountView.From(party.BankAccounts.Single(a => a.Id == added.Value));
    }
}

/// <summary>
/// Workflow's decision on a bank account's verification (BANK_ACCOUNT_VERIFICATION): approved, the account becomes
/// active; rejected, it is never activated (ADR-013). Workflow has checked maker ≠ checker, authority and the
/// bank_account.verify permission; the subject hash binds the decision to the account as it was.
/// </summary>
public sealed class BankAccountVerificationPolicy(IPartyRepository parties, IClock clock, IAuditTrail audit, ILogger<BankAccountVerificationPolicy> logger) :
    IIntegrationEventHandler<Workflow.Contracts.ApprovalGranted>,
    IIntegrationEventHandler<Workflow.Contracts.ApprovalRejected>
{
    public Task Handle(Workflow.Contracts.ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, true, granted.ApprovalId, granted.CheckerId, granted.SubjectHash, cancellationToken);

    public Task Handle(Workflow.Contracts.ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, false, rejected.ApprovalId, rejected.CheckerId, rejected.SubjectHash, cancellationToken);

    private async Task Decide(string businessRefType, Guid accountId, bool approved, Guid approvalId, Guid checkerId, string? subjectHash,
        CancellationToken cancellationToken)
    {
        if (businessRefType != PartyApprovals.BankAccountBusinessRefType || await parties.WithBankAccount(accountId, cancellationToken) is not { } party)
        {
            return;
        }

        var decided = party.DecideBankAccountVerification(accountId, approved, approvalId, checkerId, subjectHash, clock.UtcNow);
        if (decided.IsFailure)
        {
            logger.LogWarning("Approval {ApprovalId} not applied to bank account {AccountId}: {Code}", approvalId, accountId, decided.Error.Code);
            return;
        }

        audit.Record(new AuditEntry("PARTY", approved ? "VerifyBankAccount" : "RejectBankAccount", nameof(BankAccount), accountId.ToString(),
            new Dictionary<string, string?> { ["approval"] = approvalId.ToString() }));
    }
}

internal sealed class ListBankAccountsHandler(IPartyRepository parties) : IQueryHandler<ListBankAccounts, IReadOnlyList<BankAccountView>>
{
    public async Task<Result<IReadOnlyList<BankAccountView>>> Handle(ListBankAccounts query, CancellationToken cancellationToken) =>
        await parties.Get(query.PartyId, cancellationToken) is { } party
            ? party.BankAccounts.OrderBy(a => a.Id).Select(BankAccountView.From).ToList()
            : Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
}

public sealed record VerifyIdentifier(Guid PartyId, Guid IdentifierId, string VerificationRef, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record RecordRelationship(Guid PartyId, Guid ToPartyId, string RelationshipCode, DateOnly ValidFrom, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record AssignPartyRole(Guid PartyId, string RoleCode, string Domain, string ContextType, Guid ContextId, DateOnly ValidFrom, long ExpectedVersion)
    : ICommand<long>, IVersionedCommand;

public sealed record RecordDeath(Guid PartyId, DateOnly DeceasedDate, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Command MergeParties: <paramref name="MergedId"/> is merged into <paramref name="SurvivorId"/> (PTY-004).</summary>
public sealed record MergeParties(Guid SurvivorId, Guid MergedId, string Reason) : ICommand<Guid>;

public sealed record AnonymiseParty(Guid PartyId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record GrantConsent(Guid PartyId, string PurposeCode, string CaptureChannel, string? EvidenceRef) : ICommand<IReadOnlyList<ConsentView>>;

public sealed record WithdrawConsent(Guid PartyId, string PurposeCode, string CaptureChannel, string? EvidenceRef) : ICommand<IReadOnlyList<ConsentView>>;

public sealed record GetParty(Guid PartyId) : IQuery<PartyView>;

/// <summary>The current consent per purpose (the latest record of each).</summary>
public sealed record GetConsents(Guid PartyId) : IQuery<IReadOnlyList<ConsentView>>;

public sealed class RegisterPartyValidator : AbstractValidator<RegisterParty>
{
    public RegisterPartyValidator()
    {
        RuleFor(c => c).Must(c => (c.Individual is null) != (c.Organisation is null)).WithMessage("Give either individual or organisation details (PTY-001).");
        When(c => c.Individual is not null, () => RuleFor(c => c.Individual!).SetValidator(new IndividualValidator()));
        When(c => c.Organisation is not null, () => RuleFor(c => c.Organisation!).SetValidator(new OrganisationValidator()));
    }
}

public sealed class IndividualValidator : AbstractValidator<IndividualData>
{
    public IndividualValidator()
    {
        RuleFor(i => i.FirstNames).NotEmpty().MaximumLength(100);
        RuleFor(i => i.Surname).NotEmpty().MaximumLength(100);
        RuleFor(i => i.TitleCode).MaximumLength(10);
        RuleFor(i => i.GenderCode).MaximumLength(10);
        RuleFor(i => i.MaritalStatusCode).MaximumLength(15);
        RuleFor(i => i.Nationality).Length(2).When(i => i.Nationality is not null);
        RuleFor(i => i.PreferredLanguage).Matches("^[a-z]{2}$").WithMessage("preferredLanguage must be a two-letter lower-case ISO 639-1 code.")
            .When(i => i.PreferredLanguage is not null);
    }
}

public sealed class OrganisationValidator : AbstractValidator<OrganisationData>
{
    public OrganisationValidator()
    {
        RuleFor(o => o.LegalName).NotEmpty().MaximumLength(150);
        RuleFor(o => o.TradingName).MaximumLength(150);
        RuleFor(o => o.LegalFormCode).NotEmpty().MaximumLength(10);
        RuleFor(o => o.RegistrationNumber).MaximumLength(40);
        RuleFor(o => o.IndustryCode).MaximumLength(6);
    }
}

public sealed class UpdateIndividualDetailsValidator : AbstractValidator<UpdateIndividualDetails>
{
    public UpdateIndividualDetailsValidator() => RuleFor(c => c.Details).SetValidator(new IndividualValidator());
}

public sealed class ChangePreferredLanguageValidator : AbstractValidator<ChangePreferredLanguage>
{
    public ChangePreferredLanguageValidator() =>
        RuleFor(c => c.Language).Matches("^[a-z]{2}$").WithMessage("language must be a two-letter lower-case ISO 639-1 code.").When(c => c.Language is not null);
}

public sealed class UpdateOrganisationDetailsValidator : AbstractValidator<UpdateOrganisationDetails>
{
    public UpdateOrganisationDetailsValidator() => RuleFor(c => c.Details).SetValidator(new OrganisationValidator());
}

public sealed class AddAddressValidator : AbstractValidator<AddAddress>
{
    public AddAddressValidator()
    {
        RuleFor(c => c.Address.AddressType).NotEmpty().MaximumLength(12);
        RuleFor(c => c.Address.Line1).NotEmpty().MaximumLength(120);
        RuleFor(c => c.Address.Line2).MaximumLength(120);
        RuleFor(c => c.Address.City).NotEmpty().MaximumLength(80);
        RuleFor(c => c.Address.Subdivision).MaximumLength(6);
        RuleFor(c => c.Address.PostalCode).MaximumLength(10);
        RuleFor(c => c.Address.Country).Length(2);
    }
}

public sealed class AddContactPointValidator : AbstractValidator<AddContactPoint>
{
    public AddContactPointValidator()
    {
        RuleFor(c => c.ChannelType).NotEmpty().MaximumLength(10);
        RuleFor(c => c.Value).NotEmpty().MaximumLength(200);
    }
}

public sealed class AddIdentifierValidator : AbstractValidator<AddIdentifier>
{
    public AddIdentifierValidator()
    {
        RuleFor(c => c.DocType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Country).Length(2);
        RuleFor(c => c.Number).NotEmpty().MaximumLength(60);
    }
}

public sealed class MergePartiesValidator : AbstractValidator<MergeParties>
{
    public MergePartiesValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
}

public sealed class GrantConsentValidator : AbstractValidator<GrantConsent>
{
    public GrantConsentValidator()
    {
        RuleFor(c => c.PurposeCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.CaptureChannel).NotEmpty().MaximumLength(15);
        RuleFor(c => c.EvidenceRef).MaximumLength(200);
    }
}

public sealed class WithdrawConsentValidator : AbstractValidator<WithdrawConsent>
{
    public WithdrawConsentValidator()
    {
        RuleFor(c => c.PurposeCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.CaptureChannel).NotEmpty().MaximumLength(15);
        RuleFor(c => c.EvidenceRef).MaximumLength(200);
    }
}

internal static class Parties
{
    public static async Task<Result<Domain.Parties.Party>> Load(IPartyRepository parties, Guid id, long? expected, CancellationToken cancellationToken)
    {
        var party = await parties.Get(id, cancellationToken);
        if (party is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        return expected is { } v && party.RowVersion != v
            ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The party has changed since it was read.")
            : party;
    }

    /// <summary>Runs a change on a loaded party and returns its next row version.</summary>
    public static async Task<Result<long>> Change(IPartyRepository parties, Guid id, long expected, Func<Domain.Parties.Party, Result> change,
        CancellationToken cancellationToken)
    {
        var party = await Load(parties, id, expected, cancellationToken);
        if (party.IsFailure)
        {
            return party.Error;
        }

        var result = change(party.Value);
        return result.IsFailure ? result.Error : party.Value.RowVersion + 1;
    }

    public static Result<Guid> User(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This change is made by an identified user.");
}

internal sealed class RegisterPartyHandler(IPartyRepository parties, IClock clock, IAuditTrail audit) : ICommandHandler<RegisterParty, Guid>
{
    public Task<Result<Guid>> Handle(RegisterParty command, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var party = command.Individual is { } individual
            ? Domain.Parties.Party.RegisterIndividual(id, individual, clock.UtcNow)
            : Domain.Parties.Party.RegisterOrganisation(id, command.Organisation!, clock.UtcNow);
        parties.Add(party);
        audit.Record(new AuditEntry("PARTY", "RegisterParty", nameof(Domain.Parties.Party), id.ToString()));
        return Task.FromResult(Result.Success(id));
    }
}

internal sealed class UpdateIndividualDetailsHandler(IPartyRepository parties, IClock clock, IAuditTrail audit) : ICommandHandler<UpdateIndividualDetails, long>
{
    public Task<Result<long>> Handle(UpdateIndividualDetails command, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry("PARTY", "UpdateDetails", nameof(Domain.Parties.Party), command.PartyId.ToString()));
        return Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.UpdateDetails(command.Details, clock.UtcNow), cancellationToken);
    }
}

internal sealed class ChangePreferredLanguageHandler(IPartyRepository parties, ILanguageRegister languages, IClock clock, IAuditTrail audit)
    : ICommandHandler<ChangePreferredLanguage, long>
{
    public async Task<Result<long>> Handle(ChangePreferredLanguage command, CancellationToken cancellationToken)
    {
        if (command.Language is { } language && !await languages.Exists(language, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{language} is not a language in the register.");
        }

        audit.Record(new AuditEntry("PARTY", "ChangePreferredLanguage", nameof(Domain.Parties.Party), command.PartyId.ToString()));
        return await Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.ChangePreferredLanguage(command.Language, clock.UtcNow), cancellationToken);
    }
}

internal sealed class UpdateOrganisationDetailsHandler(IPartyRepository parties, IClock clock, IAuditTrail audit) : ICommandHandler<UpdateOrganisationDetails, long>
{
    public Task<Result<long>> Handle(UpdateOrganisationDetails command, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry("PARTY", "UpdateDetails", nameof(Domain.Parties.Party), command.PartyId.ToString()));
        return Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.UpdateDetails(command.Details, clock.UtcNow), cancellationToken);
    }
}

internal sealed class AddAddressHandler(IPartyRepository parties, IClock clock) : ICommandHandler<AddAddress, long>
{
    public Task<Result<long>> Handle(AddAddress command, CancellationToken cancellationToken) =>
        Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.AddAddress(command.Address, clock.UtcNow), cancellationToken);
}

internal sealed class AddContactPointHandler(IPartyRepository parties, IClock clock) : ICommandHandler<AddContactPoint, long>
{
    public Task<Result<long>> Handle(AddContactPoint command, CancellationToken cancellationToken) =>
        Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.AddContactPoint(command.ChannelType, command.Value, command.IsPrimary, clock.UtcNow),
            cancellationToken);
}

/// <summary>AddIdentifier: unique per document type and country in the tenant, checked by keyed hash (and by uq_party_identifier_1).</summary>
internal sealed class AddIdentifierHandler(IPartyRepository parties, IPartyCodes codes, IFieldProtector protector, IClock clock, IAuditTrail audit)
    : ICommandHandler<AddIdentifier, Guid>
{
    public async Task<Result<Guid>> Handle(AddIdentifier command, CancellationToken cancellationToken)
    {
        var party = await Parties.Load(parties, command.PartyId, command.ExpectedVersion, cancellationToken);
        if (party.IsFailure)
        {
            return party.Error;
        }

        var (exists, pattern) = await codes.IdentityDocumentType(command.Country, command.DocType, clock.UtcToday, cancellationToken);
        var number = command.Number.Trim();
        if (!exists)
        {
            return Error.Validation(PartyErrorCodes.CodeNotInForce, $"{command.DocType} is not an identity document type in force for {command.Country}.");
        }

        if (!string.IsNullOrEmpty(pattern) && !Regex.IsMatch(number, $"^(?:{pattern})$", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            return Error.Validation(PartyErrorCodes.IdentifierInvalid, $"The number does not have the {command.DocType} format.");
        }

        var protectedValue = protector.Protect(number);
        if (await parties.IdentifierExists(command.DocType, command.Country, protectedValue.Hash, cancellationToken))
        {
            return Error.Conflict(PartyErrorCodes.IdentifierInUse, $"Another party in the tenant has this {command.DocType} number.");
        }

        audit.Record(new AuditEntry("PARTY", "AddIdentifier", nameof(Domain.Parties.Party), command.PartyId.ToString(),
            new Dictionary<string, string?> { ["docType"] = command.DocType, ["country"] = command.Country }));
        return party.Value.AddIdentifier(command.DocType, command.Country, protectedValue.Encrypted, protectedValue.Hash, clock.UtcNow);
    }
}

internal sealed class VerifyIdentifierHandler(IPartyRepository parties, IClock clock) : ICommandHandler<VerifyIdentifier, long>
{
    public Task<Result<long>> Handle(VerifyIdentifier command, CancellationToken cancellationToken) =>
        Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.VerifyIdentifier(command.IdentifierId, command.VerificationRef, clock.UtcNow),
            cancellationToken);
}

internal sealed class RecordRelationshipHandler(IPartyRepository parties, IPartyCodes codes, IClock clock) : ICommandHandler<RecordRelationship, long>
{
    public async Task<Result<long>> Handle(RecordRelationship command, CancellationToken cancellationToken)
    {
        if (!await codes.RelationshipTypeExists(command.RelationshipCode, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(PartyErrorCodes.CodeNotInForce, $"{command.RelationshipCode} is not a relationship type in force.");
        }

        if (await parties.Get(command.ToPartyId, cancellationToken) is null)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The related party was not found.");
        }

        return await Parties.Change(parties, command.PartyId, command.ExpectedVersion,
            p => p.RecordRelationship(command.ToPartyId, command.RelationshipCode, command.ValidFrom, clock.UtcNow), cancellationToken);
    }
}

internal sealed class AssignPartyRoleHandler(IPartyRepository parties, IPartyCodes codes, IClock clock) : ICommandHandler<AssignPartyRole, long>
{
    public async Task<Result<long>> Handle(AssignPartyRole command, CancellationToken cancellationToken)
    {
        var party = await Parties.Load(parties, command.PartyId, command.ExpectedVersion, cancellationToken);
        if (party.IsFailure)
        {
            return party.Error;
        }

        if (!await codes.RoleAllowed(command.RoleCode, party.Value.Kind, command.Domain, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(PartyErrorCodes.CodeNotInForce,
                $"{command.RoleCode} is not a role type in force for an {party.Value.Kind.ToString().ToLowerInvariant()} in {command.Domain}.");
        }

        var result = party.Value.AssignRole(command.RoleCode, command.Domain, command.ContextType, command.ContextId, command.ValidFrom, clock.UtcNow);
        return result.IsFailure ? result.Error : party.Value.RowVersion + 1;
    }
}

internal sealed class RecordDeathHandler(IPartyRepository parties, IClock clock, IAuditTrail audit) : ICommandHandler<RecordDeath, long>
{
    public Task<Result<long>> Handle(RecordDeath command, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry("PARTY", "RecordDeath", nameof(Domain.Parties.Party), command.PartyId.ToString()));
        return Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.RecordDeath(command.DeceasedDate, clock.UtcToday, clock.UtcNow), cancellationToken);
    }
}

internal sealed class MergePartiesHandler(IPartyRepository parties, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<MergeParties, Guid>
{
    public async Task<Result<Guid>> Handle(MergeParties command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var survivor = await parties.Get(command.SurvivorId, cancellationToken);
        var merged = await parties.Get(command.MergedId, cancellationToken);
        if (survivor is null || merged is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "Both parties must exist.");
        }

        var snapshot = JsonSerializer.Serialize(new
        {
            kind = merged.Kind.ToString(),
            displayName = merged.DisplayName,
            status = merged.Status.ToString(),
            roles = merged.Roles.Count,
            relationships = merged.Relationships.Count,
            identifiers = merged.Identifiers.Select(i => new { i.DocType, i.Country }),
        }, JsonSerializerOptions.Web);
        audit.Record(new AuditEntry("PARTY", "MergeParties", nameof(Domain.Parties.Party), merged.Id.ToString(),
            new Dictionary<string, string?> { ["survivor"] = survivor.Id.ToString() }));
        var result = merged.MergeInto(survivor, user.Value, command.Reason, snapshot, clock.UtcNow);
        return result.IsFailure ? result.Error : merged.Id;
    }
}

internal sealed class AnonymisePartyHandler(IPartyRepository parties, IClock clock, IAuditTrail audit) : ICommandHandler<AnonymiseParty, long>
{
    public Task<Result<long>> Handle(AnonymiseParty command, CancellationToken cancellationToken)
    {
        // TODO(spec): no retention period for parties is defined; the caller decides that retention has ended (OQ-054).
        audit.Record(new AuditEntry("PARTY", "AnonymiseParty", nameof(Domain.Parties.Party), command.PartyId.ToString()));
        return Parties.Change(parties, command.PartyId, command.ExpectedVersion, p => p.AnonymiseParty(clock.UtcNow), cancellationToken);
    }
}

internal sealed class GrantConsentHandler(IPartyRepository parties, IPartyCodes codes, IClock clock) : ICommandHandler<GrantConsent, IReadOnlyList<ConsentView>>
{
    public Task<Result<IReadOnlyList<ConsentView>>> Handle(GrantConsent command, CancellationToken cancellationToken) =>
        Consents.Change(parties, codes, clock, command.PartyId, command.PurposeCode,
            ledger => ledger.GrantConsent(command.PurposeCode, command.CaptureChannel, command.EvidenceRef, clock.UtcNow), cancellationToken);
}

internal sealed class WithdrawConsentHandler(IPartyRepository parties, IPartyCodes codes, IClock clock) : ICommandHandler<WithdrawConsent, IReadOnlyList<ConsentView>>
{
    public Task<Result<IReadOnlyList<ConsentView>>> Handle(WithdrawConsent command, CancellationToken cancellationToken) =>
        Consents.Change(parties, codes, clock, command.PartyId, command.PurposeCode,
            ledger => ledger.WithdrawConsent(command.PurposeCode, command.CaptureChannel, command.EvidenceRef, clock.UtcNow), cancellationToken);
}

internal static class Consents
{
    public static async Task<Result<IReadOnlyList<ConsentView>>> Change(IPartyRepository parties, IPartyCodes codes, IClock clock, Guid partyId, string purpose,
        Func<ConsentLedger, ConsentRecord?> change, CancellationToken cancellationToken)
    {
        if (await parties.Get(partyId, cancellationToken) is not { } party)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        if (party.Status is PartyStatus.Merged or PartyStatus.Anonymised)
        {
            return Error.Conflict(PartyErrorCodes.PartyReadOnly, "Consent is recorded on an active party.");
        }

        if (!await codes.ConsentPurposeExists(purpose, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(PartyErrorCodes.CodeNotInForce, $"{purpose} is not a consent purpose in force.");
        }

        var ledger = new ConsentLedger(partyId, await parties.Consents(partyId, cancellationToken));
        if (change(ledger) is { } record)
        {
            parties.Add(record);
        }

        return Views(ledger);
    }

    public static List<ConsentView> Views(ConsentLedger ledger) =>
        ledger.Current.Values.OrderBy(r => r.PurposeCode, StringComparer.Ordinal)
            .Select(r => new ConsentView(r.PurposeCode, r.Granted, r.CapturedAt, r.CaptureChannel, r.EvidenceRef, r.WithdrawnAt)).ToList();
}

internal sealed class GetPartyHandler(IPartyRepository parties) : IQueryHandler<GetParty, PartyView>
{
    public async Task<Result<PartyView>> Handle(GetParty query, CancellationToken cancellationToken) =>
        await parties.Get(query.PartyId, cancellationToken) is { } party ? PartyView.From(party) : Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
}

internal sealed class GetConsentsHandler(IPartyRepository parties) : IQueryHandler<GetConsents, IReadOnlyList<ConsentView>>
{
    public async Task<Result<IReadOnlyList<ConsentView>>> Handle(GetConsents query, CancellationToken cancellationToken) =>
        await parties.Get(query.PartyId, cancellationToken) is null
            ? Error.NotFound(ErrorCodes.NotFound, "The party was not found.")
            : Consents.Views(new ConsentLedger(query.PartyId, await parties.Consents(query.PartyId, cancellationToken)));
}
