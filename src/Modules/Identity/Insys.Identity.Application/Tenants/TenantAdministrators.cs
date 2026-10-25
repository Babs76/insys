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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Identity.Application.Users;
using Insys.Identity.Contracts;
using Insys.Identity.Domain.Tenants;
using Insys.Identity.Domain.Users;
using Insys.Tenancy.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Options;

namespace Insys.Identity.Application.Tenants;

/// <summary>
/// Command ProvisionTenantAdministrator (OQ-067, GAP-019): a platform administrator asks for a new tenant's first
/// administrator while the tenant is Onboarding. The user signs in with the identity provider's subject, whose token
/// carries the new tenant's tenant_id. Permissions are the TENANT_ADMIN role's permissions in the new tenant.
/// </summary>
/// <param name="Email">For INSYS's own realm (idpCode KEYCLOAK): INSYS creates the Keycloak user on approval and sends the invitation here.</param>
public sealed record ProvisionTenantAdministrator(Guid TargetTenantId, string Username, string DisplayName, string IdpCode, string? ExternalSubject,
    bool MfaEnrolled, string[] Permissions, string? Email = null) : ICommand<Guid>;

public sealed class ProvisionTenantAdministratorValidator : AbstractValidator<ProvisionTenantAdministrator>
{
    public ProvisionTenantAdministratorValidator()
    {
        RuleFor(c => c.TargetTenantId).NotEmpty();
        RuleFor(c => c.Username).NotEmpty().MaximumLength(120);
        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(c => c.IdpCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ExternalSubject).NotEmpty().MaximumLength(120).When(c => c.IdpCode != UserAccount.OwnRealm);
        RuleFor(c => c.ExternalSubject).Empty().When(c => c.IdpCode == UserAccount.OwnRealm)
            .WithMessage("INSYS creates the Keycloak user of its own realm; give its email, not an external subject.");
        RuleFor(c => c.Email).NotEmpty().MaximumLength(254).EmailAddress().When(c => c.IdpCode == UserAccount.OwnRealm);
        RuleFor(c => c.Permissions).NotEmpty();
        RuleForEach(c => c.Permissions).NotEmpty().MaximumLength(80);
    }
}

/// <summary>A request for a new tenant's first administrator and its decision.</summary>
public sealed record TenantAdministratorRequestView(Guid Id, Guid TargetTenantId, string Username, string DisplayName, string IdpCode, string? ExternalSubject,
    bool MfaEnrolled, string RoleCode, string[] Permissions, Guid RequestedBy, TenantAdministratorRequestStatus Status, Guid? DecidedBy,
    DateTimeOffset? DecidedAt, Guid? UserId, long RowVersion)
{
    public static TenantAdministratorRequestView From(TenantAdministratorRequest r) => new(r.Id, r.TargetTenantId, r.Username, r.DisplayName, r.IdpCode,
        r.ExternalSubject, r.MfaEnrolled, r.Role, r.Permissions, r.RequestedBy, r.Status, r.DecidedBy, r.DecidedAt, r.UserId, r.RowVersion);
}

public sealed record GetTenantAdministratorRequest(Guid RequestId) : IQuery<TenantAdministratorRequestView>;

public sealed record ListTenantAdministratorRequests(Guid? TargetTenantId) : IQuery<IReadOnlyList<TenantAdministratorRequestView>>;

/// <summary>The operator tenant's requests, and the new tenant's users and TENANT_ADMIN role (written as that tenant).</summary>
public interface ITenantAdministrators
{
    Task<TenantAdministratorRequest?> Get(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantAdministratorRequest>> List(Guid? targetTenantId, CancellationToken cancellationToken);

    Task<bool> HasPendingRequest(Guid targetTenantId, CancellationToken cancellationToken);

    void Add(TenantAdministratorRequest request);

    /// <summary>Whether the tenant already has any user, read as that tenant.</summary>
    Task<bool> TenantHasUsers(Guid targetTenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates, as the new tenant and in the caller's transaction, the TENANT_ADMIN role definition (approved by the
    /// checker), the user and its active assignment. Returns the new user's ID.
    /// </summary>
    Task<Guid> Provision(TenantAdministratorRequest request, Guid checkerId, CancellationToken cancellationToken);
}

internal sealed class ProvisionTenantAdministratorHandler(ITenantAdministrators administrators, ITenantProfiles tenants, IExecutionContext execution,
    ITenantContext tenant, IOptions<PlatformOptions> platform, IClock clock, IAuditTrail audit, Users.IFunctionCoverage coverage)
    : ICommandHandler<ProvisionTenantAdministrator, Guid>
{
    public async Task<Result<Guid>> Handle(ProvisionTenantAdministrator command, CancellationToken cancellationToken)
    {
        if (tenant.TenantId != platform.Value.OperatorTenantId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only the platform operator provisions a tenant's first administrator.");
        }

        var requester = Administrator.Require(execution);
        if (requester.IsFailure)
        {
            return requester.Error;
        }

        if (await tenants.Get(command.TargetTenantId, cancellationToken) is not { } target)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The tenant was not found.");
        }

        if (target.Status != "Onboarding")
        {
            return Error.Conflict(IdentityErrorCodes.TenantNotOnboarding, $"Tenant {target.TenantCode} is {target.Status}; its first administrator is provisioned during onboarding.");
        }

        if (await administrators.HasPendingRequest(command.TargetTenantId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.DuplicateRecord, $"A first administrator for {target.TenantCode} is already waiting for approval.");
        }

        if (await administrators.TenantHasUsers(command.TargetTenantId, cancellationToken))
        {
            return Error.Conflict(IdentityErrorCodes.TenantHasUsers, $"Tenant {target.TenantCode} already has users; its administrators add the others.");
        }

        // ADR-026: the role is given the functions that need only these permissions, so each must be needed by such a function.
        if (await coverage.Uncovered(command.Permissions, clock.UtcToday, cancellationToken) is { Count: > 0 } uncovered)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Every permission must be one a function grants with the others (ADR-026).",
                new Dictionary<string, string[]> { ["permissions"] = [$"No function needs only these permissions, so the role cannot hold: {string.Join(", ", uncovered)}."] });
        }

        var request = TenantAdministratorRequest.Create(command.TargetTenantId, command.Username, command.DisplayName, command.IdpCode, command.ExternalSubject,
            command.MfaEnrolled, command.Permissions, requester.Value, clock.UtcNow, command.Email);
        administrators.Add(request);
        audit.Record(new AuditEntry("IDENTITY", "ProvisionTenantAdministrator", nameof(TenantAdministratorRequest), request.Id.ToString(),
            new Dictionary<string, string?> { ["tenant"] = target.TenantCode, ["username"] = command.Username }));
        return request.Id;
    }
}

internal sealed class GetTenantAdministratorRequestHandler(ITenantAdministrators administrators)
    : IQueryHandler<GetTenantAdministratorRequest, TenantAdministratorRequestView>
{
    public async Task<Result<TenantAdministratorRequestView>> Handle(GetTenantAdministratorRequest query, CancellationToken cancellationToken) =>
        await administrators.Get(query.RequestId, cancellationToken) is { } request
            ? TenantAdministratorRequestView.From(request)
            : Error.NotFound(ErrorCodes.NotFound, "The request was not found.");
}

internal sealed class ListTenantAdministratorRequestsHandler(ITenantAdministrators administrators)
    : IQueryHandler<ListTenantAdministratorRequests, IReadOnlyList<TenantAdministratorRequestView>>
{
    public async Task<Result<IReadOnlyList<TenantAdministratorRequestView>>> Handle(ListTenantAdministratorRequests query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<TenantAdministratorRequestView>>(
            (await administrators.List(query.TargetTenantId, cancellationToken)).Select(TenantAdministratorRequestView.From).ToList());
}

/// <summary>
/// Identity's policy for Workflow's decision on a first-administrator request (OQ-067): granted, the user, the role
/// and the assignment are created in the new tenant in this same transaction; rejected, nothing is created.
/// </summary>
public sealed class TenantAdministratorApprovalPolicy(ITenantAdministrators administrators, IClock clock) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>
{
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.CheckerId, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid requestId, bool approved, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType != IdentityApprovals.TenantAdministratorBusinessRefType
            || await administrators.Get(requestId, cancellationToken) is not { Status: TenantAdministratorRequestStatus.PendingApproval } request)
        {
            return;
        }

        var userId = approved ? await administrators.Provision(request, checkerId, cancellationToken) : (Guid?)null;
        request.Decide(approved, checkerId, userId, clock.UtcNow);
    }
}
