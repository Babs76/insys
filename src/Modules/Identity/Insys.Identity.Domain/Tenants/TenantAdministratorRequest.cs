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
using Insys.BuildingBlocks.Domain;

namespace Insys.Identity.Domain.Tenants;

/// <summary>iam.tenant_administrator_request.status (V0026, V0062): the decision. An approved request's identities are read separately (BR-018).</summary>
public enum TenantAdministratorRequestStatus
{
    PendingApproval,
    Approved,
    Rejected,
}

/// <summary>What the request is for (V0062, BR-013).</summary>
public enum TenantAdministratorRequestKind
{
    /// <summary>A new tenant's first administrators: two people, while the tenant is onboarding and has no users.</summary>
    FirstAdministrators,

    /// <summary>One more administrator for an existing tenant, in its existing TENANT_ADMIN role.</summary>
    AdditionalAdministrator,
}

/// <summary>A person a request names (iam.tenant_administrator_person).</summary>
public sealed class TenantAdministratorPerson : Entity<Guid>, ITenantScoped
{
    private TenantAdministratorPerson()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RequestId { get; private set; }

    public short Position { get; private set; }

    public string Username { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    public string IdpCode { get; private set; } = "";

    /// <summary>The subject at a federated identity provider; null for INSYS's own realm, where INSYS creates the Keycloak user.</summary>
    public string? ExternalSubject { get; private set; }

    /// <summary>For INSYS's own realm: where Keycloak sends the invitation (keycloak-user-provisioning §1).</summary>
    public string? Email { get; private set; }

    public bool MfaEnrolled { get; private set; }

    /// <summary>The user created in the tenant, once approved.</summary>
    public Guid? UserId { get; private set; }

    internal static TenantAdministratorPerson Create(Guid requestId, short position, TenantAdministratorPersonDetails details) => new()
    {
        Id = Guid.CreateVersion7(),
        RequestId = requestId,
        Position = position,
        Username = Guard.MaxLength(Guard.NotNullOrWhiteSpace(details.Username), 120),
        DisplayName = Guard.MaxLength(Guard.NotNullOrWhiteSpace(details.DisplayName), 120),
        IdpCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(details.IdpCode), 15),
        ExternalSubject = details.ExternalSubject is null ? null : Guard.MaxLength(Guard.NotNullOrWhiteSpace(details.ExternalSubject), 120),
        Email = details.Email is null ? null : Guard.MaxLength(Guard.NotNullOrWhiteSpace(details.Email), 254),
        MfaEnrolled = details.MfaEnrolled,
    };

    internal void Provisioned(Guid userId) => UserId = Guard.NotEmpty(userId);
}

/// <summary>Who a request names: the user's details, as the tenant will hold them.</summary>
public sealed record TenantAdministratorPersonDetails(string Username, string DisplayName, string IdpCode, string? ExternalSubject, bool MfaEnrolled, string? Email);

/// <summary>
/// A platform operator's request for a tenant's administrators (OQ-067, GAP-019, BR-013), held in the operator tenant.
/// For a new tenant it names two people and the functions of the tenant's TENANT_ADMIN role, so that from the start one
/// administrator can approve what the other does; for an existing tenant it names one more administrator for the
/// existing role. A second platform administrator approves it in Workflow (SRS-002); a platform administrator never
/// approves anything inside the tenant. On approval the users (and, for a new tenant, the role) are created in the tenant.
/// </summary>
public sealed class TenantAdministratorRequest : AggregateRoot<Guid>
{
    /// <summary>The role the administrators hold. A new tenant's request gives its functions (OQ-067, OQ-099).</summary>
    public const string RoleCode = "TENANT_ADMIN";

    /// <summary>A new tenant's first administrators: two, so that neither is left alone with a privileged role (BR-013).</summary>
    public const int FirstAdministratorCount = 2;

    private readonly List<TenantAdministratorPerson> _people = [];

    private TenantAdministratorRequest()
    {
    }

    public Guid TargetTenantId { get; private set; }

    public TenantAdministratorRequestKind Kind { get; private set; }

    public IReadOnlyList<TenantAdministratorPerson> People => _people;

    public string Role { get; private set; } = RoleCode;

    /// <summary>The functions the tenant's TENANT_ADMIN role is granted (ADR-026, OQ-099); none for an additional administrator.</summary>
    public string[] Functions { get; private set; } = [];

    public Guid RequestedBy { get; private set; }

    public TenantAdministratorRequestStatus Status { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>A new tenant's first administrators: exactly two people, with distinct usernames, and at least one function.</summary>
    public static Result<TenantAdministratorRequest> ForNewTenant(Guid targetTenantId, IReadOnlyList<TenantAdministratorPersonDetails> people,
        IEnumerable<string> functions, Guid requestedBy, DateTimeOffset now)
    {
        if (people.Count != FirstAdministratorCount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"A new tenant's request names {FirstAdministratorCount} administrators (BR-013).",
                new Dictionary<string, string[]> { ["administrators"] = [$"Give exactly {FirstAdministratorCount} administrators."] });
        }

        var granted = functions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (granted.Length == 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The TENANT_ADMIN role needs at least one function (OQ-099).",
                new Dictionary<string, string[]> { ["functions"] = ["Give at least one function."] });
        }

        return Create(targetTenantId, TenantAdministratorRequestKind.FirstAdministrators, people, granted, requestedBy, now);
    }

    /// <summary>One more administrator for an existing tenant, in its TENANT_ADMIN role (BR-013).</summary>
    public static Result<TenantAdministratorRequest> ForExistingTenant(Guid targetTenantId, TenantAdministratorPersonDetails person, Guid requestedBy,
        DateTimeOffset now) =>
        Create(targetTenantId, TenantAdministratorRequestKind.AdditionalAdministrator, [person], [], requestedBy, now);

    private static Result<TenantAdministratorRequest> Create(Guid targetTenantId, TenantAdministratorRequestKind kind,
        IReadOnlyList<TenantAdministratorPersonDetails> people, string[] functions, Guid requestedBy, DateTimeOffset now)
    {
        if (people.Select(p => p.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != people.Count)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Each administrator needs a username of their own.",
                new Dictionary<string, string[]> { ["administrators"] = ["The usernames must differ."] });
        }

        var request = new TenantAdministratorRequest
        {
            Id = Guid.CreateVersion7(),
            TargetTenantId = Guard.NotEmpty(targetTenantId),
            Kind = kind,
            Functions = functions,
            RequestedBy = Guard.NotEmpty(requestedBy),
            Status = TenantAdministratorRequestStatus.PendingApproval,
        };
        short position = 0;
        foreach (var person in people)
        {
            request._people.Add(TenantAdministratorPerson.Create(request.Id, ++position, person));
        }

        request.Raise(new TenantAdministratorRequested(request.Id, targetTenantId, string.Join(", ", people.Select(p => p.Username)), requestedBy, now));
        return request;
    }

    /// <summary>
    /// The second platform administrator's decision. The checker is never the requester (SRS-002). Approved, each person
    /// has the user created for them (<paramref name="userIds"/>, by position).
    /// </summary>
    public Result Decide(bool approved, Guid checkerId, IReadOnlyList<Guid>? userIds, DateTimeOffset now)
    {
        if (Status != TenantAdministratorRequestStatus.PendingApproval)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The request has already been decided.");
        }

        if (checkerId != Guid.Empty && checkerId == RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RequestedBy.ToString(), checkerId.ToString());
        }

        if (approved && (checkerId == Guid.Empty || userIds is null || userIds.Count != _people.Count))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A request is approved by a checker, with a user for each person it names.");
        }

        // Every ending other than approval (rejected, expired, withdrawn) rejects it; with no checker nobody is recorded.
        Status = approved ? TenantAdministratorRequestStatus.Approved : TenantAdministratorRequestStatus.Rejected;
        DecidedBy = checkerId == Guid.Empty ? null : checkerId;
        DecidedAt = now;
        if (approved)
        {
            foreach (var (person, userId) in _people.OrderBy(p => p.Position).Zip(userIds!))
            {
                person.Provisioned(userId);
                Raise(new TenantAdministratorProvisioned(Id, TargetTenantId, userId, Role, now));
            }
        }

        MarkChanged();
        return Result.Success();
    }
}

/// <summary>Asks Workflow for the second platform administrator's approval (ADR-009).</summary>
public sealed record TenantAdministratorRequested(Guid RequestId, Guid TargetTenantId, string Usernames, Guid RequestedBy, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An administrator was created in the tenant; Tenancy checks the ORGANISATION_AND_ACCESS onboarding step.</summary>
public sealed record TenantAdministratorProvisioned(Guid RequestId, Guid TargetTenantId, Guid UserId, string RoleCode, DateTimeOffset OccurredAt) : IDomainEvent;
