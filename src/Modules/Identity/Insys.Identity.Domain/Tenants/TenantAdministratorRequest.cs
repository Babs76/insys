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

/// <summary>iam.tenant_administrator_request.status (V0026).</summary>
public enum TenantAdministratorRequestStatus
{
    PendingApproval,
    Provisioned,
    Rejected,
}

/// <summary>
/// A platform operator's request for a new tenant's first administrator (OQ-067, GAP-019), held in the operator tenant.
/// A platform administrator names the user and the permissions of the tenant's TENANT_ADMIN role; a second platform
/// administrator approves it in Workflow (SRS-002). On approval the user, the role and an active assignment are created
/// in the new tenant, and the request records the new user.
/// </summary>
public sealed class TenantAdministratorRequest : AggregateRoot<Guid>
{
    /// <summary>The role the first administrator holds. Its permissions are given in the request (OQ-067).</summary>
    public const string RoleCode = "TENANT_ADMIN";

    private TenantAdministratorRequest()
    {
    }

    public Guid TargetTenantId { get; private set; }

    public string Username { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    public string IdpCode { get; private set; } = "";

    /// <summary>The identity provider's subject for the user (Keycloak: the user's ID), whose token carries the new tenant's tenant_id.</summary>
    /// <summary>The subject at a federated identity provider; null for INSYS's own realm, where INSYS creates the Keycloak user.</summary>
    public string? ExternalSubject { get; private set; }

    /// <summary>For INSYS's own realm: where Keycloak sends the invitation (keycloak-user-provisioning §1).</summary>
    public string? Email { get; private set; }

    public bool MfaEnrolled { get; private set; }

    public string Role { get; private set; } = RoleCode;

    /// <summary>The functions the tenant's TENANT_ADMIN role is granted (ADR-026, OQ-099).</summary>
    public string[] Functions { get; private set; } = [];

    public Guid RequestedBy { get; private set; }

    public TenantAdministratorRequestStatus Status { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>The user created in the new tenant, once provisioned.</summary>
    public Guid? UserId { get; private set; }

    public static TenantAdministratorRequest Create(Guid targetTenantId, string username, string displayName, string idpCode, string? externalSubject,
        bool mfaEnrolled, IEnumerable<string> functions, Guid requestedBy, DateTimeOffset now, string? email = null)
    {
        var request = new TenantAdministratorRequest
        {
            Id = Guid.CreateVersion7(),
            TargetTenantId = Guard.NotEmpty(targetTenantId),
            Username = Guard.MaxLength(Guard.NotNullOrWhiteSpace(username), 120),
            DisplayName = Guard.MaxLength(Guard.NotNullOrWhiteSpace(displayName), 120),
            IdpCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(idpCode), 15),
            ExternalSubject = externalSubject is null ? null : Guard.MaxLength(Guard.NotNullOrWhiteSpace(externalSubject), 120),
            Email = email is null ? null : Guard.MaxLength(Guard.NotNullOrWhiteSpace(email), 254),
            MfaEnrolled = mfaEnrolled,
            Functions = [.. functions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            RequestedBy = Guard.NotEmpty(requestedBy),
            Status = TenantAdministratorRequestStatus.PendingApproval,
        };
        request.Raise(new TenantAdministratorRequested(request.Id, targetTenantId, username, requestedBy, now));
        return request;
    }

    /// <summary>The second platform administrator's decision. The checker is never the requester (SRS-002).</summary>
    public Result Decide(bool approved, Guid checkerId, Guid? userId, DateTimeOffset now)
    {
        if (Status != TenantAdministratorRequestStatus.PendingApproval)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The request has already been decided.");
        }

        if (checkerId != Guid.Empty && checkerId == RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RequestedBy.ToString(), checkerId.ToString());
        }

        if (approved && checkerId == Guid.Empty)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A request is approved by a checker.");
        }

        // Every ending other than approval (rejected, expired, withdrawn) rejects it; with no checker nobody is recorded.
        Status = approved ? TenantAdministratorRequestStatus.Provisioned : TenantAdministratorRequestStatus.Rejected;
        DecidedBy = checkerId == Guid.Empty ? null : checkerId;
        DecidedAt = now;
        UserId = approved ? Guard.NotEmpty(userId ?? Guid.Empty) : null;
        if (approved)
        {
            Raise(new TenantAdministratorProvisioned(Id, TargetTenantId, UserId!.Value, Role, now));
        }

        MarkChanged();
        return Result.Success();
    }
}

/// <summary>Asks Workflow for the second platform administrator's approval (ADR-009).</summary>
public sealed record TenantAdministratorRequested(Guid RequestId, Guid TargetTenantId, string Username, Guid RequestedBy, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The new tenant's first administrator exists; Tenancy completes the ORGANISATION_AND_ACCESS onboarding step.</summary>
public sealed record TenantAdministratorProvisioned(Guid RequestId, Guid TargetTenantId, Guid UserId, string RoleCode, DateTimeOffset OccurredAt) : IDomainEvent;
