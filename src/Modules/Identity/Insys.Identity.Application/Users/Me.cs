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
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Identity.Domain.Users;
using Insys.StaticData.Contracts;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.Options;

namespace Insys.Identity.Application.Users;

/// <summary>The caller's tenant.</summary>
/// <param name="TimeZone">The IANA time zone the tenant's business dates follow (ADR-025).</param>
/// <param name="BusinessDate">Today in that time zone: the date to default date fields to.</param>
public sealed record MeTenant(Guid Id, string TenantCode, string Name, [property: OneOf("Onboarding", "Active", "Suspended", "Terminated")] string Status,
    bool IsPlatformOperator, string TimeZone, DateOnly BusinessDate);

/// <summary>
/// BR-022: how to format dates and numbers for this caller. Tag is a BCP 47 tag of this request's language and the
/// tenant's country (en-ZA), null when the tenant has no locale defaults in force; then a client falls back to its own
/// configuration. Dates follow the tenant's time zone (MeTenant.TimeZone); money is shown in its own currency.
/// </summary>
public sealed record MeLocale([property: BuildingBlocks.Contracts.AlwaysPresent] string? Tag, [property: BuildingBlocks.Contracts.AlwaysPresent] string? Country,
    [property: BuildingBlocks.Contracts.AlwaysPresent] string? BaseCurrency);

/// <summary>The caller's preferred language, this request's language, and the tenant's languages (OQ-068).</summary>
public sealed record MeLanguage(string? Preferred, string Request, string TenantDefault, IReadOnlyList<string> Supported);

/// <summary>
/// One of the caller's role assignments: its status, dates and scopes, whether the role is privileged, requires MFA and
/// needs a second person's approval to assign (OQ-038; every privileged role does), and the permissions it gives while active.
/// </summary>
public sealed record MeRole(string RoleCode, AssignmentStatus Status, DateOnly ValidFrom, DateOnly? ValidTo, string[]? ScopeProducts, string[]? ScopeBranches,
    bool InEffect, bool Privileged, bool RequiresMfa, bool RequiresApproval, IReadOnlyList<string> Permissions);

/// <summary>A scope a permission is held in: the products and branches of the assignment that gives it (null = all).</summary>
public sealed record PermissionScope(string RoleCode, string[]? Products, string[]? Branches);

/// <summary>An effective permission and every scope the caller holds it in.</summary>
public sealed record MePermission(string Permission, IReadOnlyList<PermissionScope> Scopes);

/// <summary>GET /api/v1/identity/me (GAP-003): who the caller is, in which tenant and language, and what they may do.</summary>
/// <param name="ScopesEnforced">
/// True: product and organisation scopes are enforced (F103), so a permission held only through scoped assignments
/// reaches only the records in those scopes. Kept for clients that checked it during the interim control.
/// </param>
/// <param name="External">For an external user, the party, intermediary or provider they are (external-user access §1); null for staff.</param>
public sealed record MeView(Guid UserId, string Username, string DisplayName, UserStatus Status, bool MfaEnrolled, MeTenant Tenant, MeLanguage Language,
    IReadOnlyList<MeRole> Roles, IReadOnlyList<MePermission> Permissions, long RowVersion, MeExternalSubject? External, bool ScopesEnforced = true,
    [property: BuildingBlocks.Contracts.AlwaysPresent] MeLocale? Locale = null);

/// <summary>What an external user's approved link names.</summary>
public sealed record MeExternalSubject(SubjectKind Kind, Guid? PartyId, string? IntermediaryCode, string? ProviderId);

public sealed record GetMe : IQuery<MeView>;

/// <summary>The caller sets or clears their own preferred language (an ISO 639-1 code in the language register).</summary>
public sealed record ChangeMyPreferredLanguage(string? Language, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed class ChangeMyPreferredLanguageValidator : AbstractValidator<ChangeMyPreferredLanguage>
{
    public ChangeMyPreferredLanguageValidator() =>
        RuleFor(c => c.Language).Matches("^[a-z]{2}$").WithMessage("language must be a two-letter lower-case ISO 639-1 code.").When(c => c.Language is not null);
}

internal sealed class GetMeHandler(IUserRepository users, IRolePolicyReader roles, ITenantProfiles tenants, ILanguageSettings languages, RequestLanguage request,
    IExecutionContext execution, ITenantContext tenant, IOptions<PlatformOptions> platform, IBusinessDate businessDate, ITenantFormatting formatting)
    : IQueryHandler<GetMe, MeView>
{
    public async Task<Result<MeView>> Handle(GetMe query, CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out var userId) || await users.Get(userId, cancellationToken) is not { } user)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a user of this tenant has a profile here.");
        }

        var tenantId = tenant.RequireTenantId();
        var today = businessDate.Today;
        var facts = (await roles.Roles(user.RoleAssignments.Select(a => a.RoleCode), today, cancellationToken)).ToDictionary(r => r.RoleCode, StringComparer.Ordinal);
        var assignments = user.RoleAssignments.OrderBy(a => a.ValidFrom).ThenBy(a => a.RoleCode, StringComparer.Ordinal).Select(a =>
        {
            var role = facts[a.RoleCode];
            var inEffect = a.Status == AssignmentStatus.Active && a.ValidFrom <= today && (a.ValidTo is null || a.ValidTo > today) && role.Exists;
            return new MeRole(a.RoleCode, a.Status, a.ValidFrom, a.ValidTo, a.ScopeProducts, a.ScopeBranches, inEffect, role.Privileged, role.RequiresMfa, role.Privileged,
                [.. role.Permissions.Order(StringComparer.Ordinal)]);
        }).ToList();
        var permissions = assignments.Where(a => a.InEffect)
            .SelectMany(a => a.Permissions.Select(p => (Permission: p, Scope: new PermissionScope(a.RoleCode, a.ScopeProducts, a.ScopeBranches))))
            .GroupBy(p => p.Permission, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MePermission(g.Key, [.. g.Select(p => p.Scope)])).ToList();

        var profile = await tenants.Get(tenantId, cancellationToken);
        var tenantLanguages = await languages.For(tenantId, cancellationToken);
        var locale = await formatting.Formatting(tenantId, businessDate.Today, cancellationToken);
        return new MeView(user.Id, user.Username, user.DisplayName, user.Status, user.MfaEnrolled,
            new MeTenant(tenantId, profile?.TenantCode ?? "", profile?.LegalName ?? "", profile?.Status ?? "", tenantId == platform.Value.OperatorTenantId,
                businessDate.Zone.Id, businessDate.Today),
            new MeLanguage(user.PreferredLanguage, request.Language, tenantLanguages.Default, tenantLanguages.Supported),
            assignments, permissions, user.RowVersion,
            user.SubjectLinks.SingleOrDefault(l => l.Status == SubjectLinkStatus.Active) is { } link
                ? new MeExternalSubject(link.Kind, link.PartyId, link.IntermediaryCode, link.ProviderId)
                : null)
        {
            Locale = new MeLocale(locale is { Country.Length: 2 } ? $"{request.Language}-{locale.Country.ToUpperInvariant()}" : null, locale?.Country, locale?.BaseCurrency),
        };
    }
}

internal sealed class ChangeMyPreferredLanguageHandler(IUserRepository users, ILanguageRegister register, IExecutionContext execution, IBusinessDate businessDate, IAuditTrail audit)
    : ICommandHandler<ChangeMyPreferredLanguage, long>
{
    public async Task<Result<long>> Handle(ChangeMyPreferredLanguage command, CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out var userId))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a user of this tenant has a profile here.");
        }

        if (command.Language is { } language && !await register.Exists(language, businessDate.Today, cancellationToken))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{language} is not a language in the register.");
        }

        var user = await Administrator.Load(users, userId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error;
        }

        audit.Record(new AuditEntry("IDENTITY", "ChangeMyPreferredLanguage", nameof(UserAccount), userId.ToString()));
        var changed = user.Value.ChangePreferredLanguage(command.Language);
        return changed.IsFailure ? changed.Error : user.Value.RowVersion + 1;
    }
}
