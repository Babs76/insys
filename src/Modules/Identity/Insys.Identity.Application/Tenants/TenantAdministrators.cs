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
/// One person a tenant administrator request names: a federated person gives their externalSubject; a person of INSYS's own
/// realm (idpCode KEYCLOAK) gives an email instead.
/// </summary>
/// <param name="IdpCode">The identity provider's code, from iam/static/identity-provider-configurations (KEYCLOAK for INSYS's own realm).</param>
/// <param name="Email">For INSYS's own realm (idpCode KEYCLOAK): INSYS creates the Keycloak user on approval and sends the invitation here.</param>
public sealed record TenantAdministratorPersonInput(string Username, string DisplayName, string IdpCode, string? ExternalSubject, bool MfaEnrolled,
    string? Email = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TenantAdministratorPersonDetails Details => new(Username, DisplayName, IdpCode, ExternalSubject, MfaEnrolled, Email);
}

public sealed class TenantAdministratorPersonValidator : AbstractValidator<TenantAdministratorPersonInput>
{
    public TenantAdministratorPersonValidator()
    {
        RuleFor(c => c.Username).NotEmpty().MaximumLength(120);
        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(120);
        // BR-006: INSYS's own realm takes the name to Keycloak, whose person-name validator refuses some characters.
        RuleFor(c => c.DisplayName).Must(name => KeycloakNames.Refused(name).Count == 0).When(c => c.IdpCode == UserAccount.OwnRealm)
            .WithMessage(c => $"Keycloak does not accept these characters in a name: {string.Join(" ", KeycloakNames.Refused(c.DisplayName))}.");
        RuleFor(c => c.IdpCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ExternalSubject).NotEmpty().MaximumLength(120).When(c => c.IdpCode != UserAccount.OwnRealm);
        RuleFor(c => c.ExternalSubject).Empty().When(c => c.IdpCode == UserAccount.OwnRealm)
            .WithMessage("INSYS creates the Keycloak user of its own realm; give its email, not an external subject.");
        RuleFor(c => c.Email).NotEmpty().MaximumLength(254).EmailAddress().When(c => c.IdpCode == UserAccount.OwnRealm);
    }
}

/// <summary>
/// Command ProvisionTenantAdministrators (OQ-067, GAP-019, BR-013): a platform administrator asks for a new tenant's two
/// first administrators while the tenant is Onboarding, with the functions of its TENANT_ADMIN role (ADR-026, OQ-099).
/// </summary>
public sealed record ProvisionTenantAdministrators(Guid TargetTenantId, IReadOnlyList<TenantAdministratorPersonInput> Administrators, string[] Functions)
    : ICommand<Guid>;

public sealed class ProvisionTenantAdministratorsValidator : AbstractValidator<ProvisionTenantAdministrators>
{
    public ProvisionTenantAdministratorsValidator()
    {
        RuleFor(c => c.TargetTenantId).NotEmpty();
        RuleFor(c => c.Administrators).NotNull().Must(a => a?.Count == TenantAdministratorRequest.FirstAdministratorCount)
            .WithMessage($"Give exactly {TenantAdministratorRequest.FirstAdministratorCount} administrators (BR-013).");
        RuleForEach(c => c.Administrators).SetValidator(new TenantAdministratorPersonValidator());
        RuleFor(c => c.Functions).NotEmpty();
        RuleForEach(c => c.Functions).NotEmpty().MaximumLength(80);
    }
}

/// <summary>Command RequestAdditionalAdministrator (BR-013): one more administrator for an existing tenant, in its TENANT_ADMIN role.</summary>
public sealed record RequestAdditionalAdministrator(Guid TargetTenantId, TenantAdministratorPersonInput Administrator) : ICommand<Guid>;

public sealed class RequestAdditionalAdministratorValidator : AbstractValidator<RequestAdditionalAdministrator>
{
    public RequestAdditionalAdministratorValidator()
    {
        RuleFor(c => c.TargetTenantId).NotEmpty();
        RuleFor(c => c.Administrator).NotNull().SetValidator(new TenantAdministratorPersonValidator());
    }
}

/// <summary>
/// Command RetryAdministratorIdentity (BR-018): after the cause of a failed or parked identity is fixed, the operator
/// retries the creation of each identity not yet linked; the request reads AwaitingIdentity again.
/// </summary>
public sealed record RetryAdministratorIdentity(Guid RequestId) : ICommand<int>;

/// <summary>
/// The state of a request as people see it (BR-018): an approved request is Provisioned only once every administrator's
/// identity is linked, and usable. Until then it awaits the identities, or shows that one is parked or has failed.
/// </summary>
public enum TenantAdministratorRequestState
{
    PendingApproval,
    AwaitingIdentity,
    IdentityParked,
    IdentityFailed,
    Provisioned,
    Rejected,
}

/// <summary>One administrator's identity: not created yet, parked (retried by the parking job), failed (needs a fix and a retry) or linked.</summary>
public enum AdministratorIdentityState
{
    Pending,
    Parked,
    Failed,
    Linked,
}

/// <param name="UserId">The administrator's user in the tenant; null until the request is approved.</param>
/// <param name="IdentityState">Whether the user's sign-in identity is usable; null until the request is approved (no user yet) and on a rejected request.</param>
/// <param name="IdentityReason">
/// Why the identity is parked or failed, for display: the parking code, then the item (for example "KEYCLOAK_USER_CONFLICT username"). The code
/// is a parking code (the error code catalogue); null unless Parked or Failed.
/// </param>
public sealed record TenantAdministratorPersonView(string Username, string DisplayName, string IdpCode, string? ExternalSubject, bool MfaEnrolled, Guid? UserId,
    [property: BuildingBlocks.Contracts.AlwaysPresent] AdministratorIdentityState? IdentityState,
    [property: BuildingBlocks.Contracts.AlwaysPresent] string? IdentityReason);

/// <summary>A tenant administrator request, its decision and its administrators' identities.</summary>
public sealed record TenantAdministratorRequestView(Guid Id, Guid TargetTenantId, TenantAdministratorRequestKind Kind, string RoleCode, string[] Functions,
    Guid RequestedBy, TenantAdministratorRequestState Status, Guid? DecidedBy, DateTimeOffset? DecidedAt, IReadOnlyList<TenantAdministratorPersonView> Administrators,
    long RowVersion, [property: BuildingBlocks.Contracts.AlwaysPresent] LatestRejection? LatestRejection = null);

/// <summary>An administrator's user in the tenant: whether Keycloak's identity is linked yet.</summary>
public sealed record AdministratorUser(Guid UserId, bool Linked);

public sealed record GetTenantAdministratorRequest(Guid RequestId) : IQuery<TenantAdministratorRequestView>;

public sealed record ListTenantAdministratorRequests(Guid? TargetTenantId) : IQuery<IReadOnlyList<TenantAdministratorRequestView>>;

/// <summary>The operator tenant's requests, and the tenant's users and TENANT_ADMIN role (written and read as that tenant).</summary>
public interface ITenantAdministrators
{
    Task<TenantAdministratorRequest?> Get(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantAdministratorRequest>> List(Guid? targetTenantId, CancellationToken cancellationToken);

    Task<bool> HasPendingRequest(Guid targetTenantId, CancellationToken cancellationToken);

    void Add(TenantAdministratorRequest request);

    /// <summary>Whether the tenant already has any user, read as that tenant.</summary>
    Task<bool> TenantHasUsers(Guid targetTenantId, CancellationToken cancellationToken);

    /// <summary>Whether the tenant's TENANT_ADMIN role is in force today, read as that tenant (an additional administrator joins it).</summary>
    Task<bool> TenantHasAdministratorRole(Guid targetTenantId, CancellationToken cancellationToken);

    /// <summary>Which of the usernames the tenant already has, read as that tenant.</summary>
    Task<IReadOnlyList<string>> UsernamesTaken(Guid targetTenantId, IEnumerable<string> usernames, CancellationToken cancellationToken);

    /// <summary>The tenant's users with these IDs, and whether each has its identity linked, read as that tenant.</summary>
    Task<IReadOnlyList<AdministratorUser>> Users(Guid targetTenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>
    /// Creates, as the tenant and in the caller's transaction: for a new tenant the TENANT_ADMIN role definition and its
    /// function grants (approved by the checker); for each person the user and an active TENANT_ADMIN assignment. Returns
    /// the users' IDs, by position.
    /// </summary>
    Task<IReadOnlyList<Guid>> Provision(TenantAdministratorRequest request, Guid checkerId, CancellationToken cancellationToken);
}

/// <summary>The Keycloak identity delivery of a user (keycloak-user-provisioning): its consumer and record in the delivery ledger.</summary>
public static class AdministratorIdentity
{
    public const string Consumer = "iam.KeycloakProvisioning.UserIdentityRequested";
    public const string AggregateType = nameof(UserAccount);

    /// <summary>Parking codes a retry alone will not clear: the identity failed and someone must fix its cause first.</summary>
    private static readonly HashSet<string> NeedsAFix =
        new([PrerequisiteCodes.KeycloakUserConflict, PrerequisiteCodes.KeycloakUserRefused], StringComparer.Ordinal);

    public static async Task<TenantAdministratorRequestView> View(TenantAdministratorRequest request, ITenantAdministrators administrators,
        Integration.Contracts.IRecordDeliveries deliveries, CancellationToken cancellationToken)
    {
        var people = request.People.OrderBy(p => p.Position).ToList();
        var states = new Dictionary<Guid, (AdministratorIdentityState State, string? Reason)>();
        if (request.Status == TenantAdministratorRequestStatus.Approved)
        {
            var users = (await administrators.Users(request.TargetTenantId, [.. people.Where(p => p.UserId is not null).Select(p => p.UserId!.Value)], cancellationToken))
                .ToDictionary(u => u.UserId);
            foreach (var userId in people.Where(p => p.UserId is not null).Select(p => p.UserId!.Value))
            {
                if (users.TryGetValue(userId, out var user) && user.Linked)
                {
                    states[userId] = (AdministratorIdentityState.Linked, null);
                    continue;
                }

                var open = await deliveries.Open(request.TargetTenantId, Consumer, AggregateType, userId, cancellationToken);
                var reason = open?.Code is null ? null : $"{open.Code}{(open.Item is null ? "" : " " + open.Item)}";
                states[userId] = open switch
                {
                    { Status: "DEAD_LETTERED" } => (AdministratorIdentityState.Failed, reason ?? "DEAD_LETTERED"),
                    { Status: "PARKED", Code: { } code } when NeedsAFix.Contains(code) => (AdministratorIdentityState.Failed, reason),
                    { Status: "PARKED" } => (AdministratorIdentityState.Parked, reason),
                    _ => (AdministratorIdentityState.Pending, null),
                };
            }
        }

        var state = request.Status switch
        {
            TenantAdministratorRequestStatus.PendingApproval => TenantAdministratorRequestState.PendingApproval,
            TenantAdministratorRequestStatus.Rejected => TenantAdministratorRequestState.Rejected,
            _ when states.Values.Any(s => s.State == AdministratorIdentityState.Failed) => TenantAdministratorRequestState.IdentityFailed,
            _ when states.Values.Any(s => s.State == AdministratorIdentityState.Parked) => TenantAdministratorRequestState.IdentityParked,
            _ when states.Count == people.Count && states.Values.All(s => s.State == AdministratorIdentityState.Linked) => TenantAdministratorRequestState.Provisioned,
            _ => TenantAdministratorRequestState.AwaitingIdentity,
        };
        return new TenantAdministratorRequestView(request.Id, request.TargetTenantId, request.Kind, request.Role, request.Functions, request.RequestedBy, state,
            request.DecidedBy, request.DecidedAt,
            [.. people.Select(p => p.UserId is { } id && states.TryGetValue(id, out var s)
                ? new TenantAdministratorPersonView(p.Username, p.DisplayName, p.IdpCode, p.ExternalSubject, p.MfaEnrolled, id, s.State, s.Reason)
                : new TenantAdministratorPersonView(p.Username, p.DisplayName, p.IdpCode, p.ExternalSubject, p.MfaEnrolled, p.UserId, null, null))],
            request.RowVersion);
    }
}

internal sealed class ProvisionTenantAdministratorsHandler(ITenantAdministrators administrators, ITenantProfiles tenants, IExecutionContext execution,
    ITenantContext tenant, IOptions<PlatformOptions> platform, IClock clock, IBusinessDate businessDate, IAuditTrail audit, Users.IFunctionCatalogueQuery catalogue)
    : ICommandHandler<ProvisionTenantAdministrators, Guid>,
      ICommandHandler<RequestAdditionalAdministrator, Guid>
{
    public async Task<Result<Guid>> Handle(ProvisionTenantAdministrators command, CancellationToken cancellationToken)
    {
        var checks = await Common(command.TargetTenantId, command.Administrators.Select(a => a.Username), cancellationToken);
        if (checks.IsFailure)
        {
            return checks.Error;
        }

        var (requester, target) = checks.Value;
        if (target.Status != "Onboarding")
        {
            return Error.Conflict(IdentityErrorCodes.TenantNotOnboarding,
                $"Tenant {target.TenantCode} is {target.Status}; its first administrators are provisioned during onboarding. Ask for an additional administrator instead.");
        }

        if (await administrators.TenantHasUsers(command.TargetTenantId, cancellationToken))
        {
            return Error.Conflict(IdentityErrorCodes.TenantHasUsers, $"Tenant {target.TenantCode} already has users; ask for an additional administrator instead.");
        }

        // ADR-026: each function must be in force in the catalogue and one staff use (OQ-099).
        if (await catalogue.NotForStaff(command.Functions, businessDate.Today, cancellationToken) is { Count: > 0 } unknown)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Every function must be in force in the catalogue and for staff (ADR-026).",
                new Dictionary<string, string[]> { ["functions"] = [$"Not a staff function in force: {string.Join(", ", unknown)}."] });
        }

        var created = TenantAdministratorRequest.ForNewTenant(command.TargetTenantId, [.. command.Administrators.Select(a => a.Details)], command.Functions,
            requester, clock.UtcNow);
        return Record(created, target.TenantCode, "ProvisionTenantAdministrators");
    }

    public async Task<Result<Guid>> Handle(RequestAdditionalAdministrator command, CancellationToken cancellationToken)
    {
        var checks = await Common(command.TargetTenantId, [command.Administrator.Username], cancellationToken);
        if (checks.IsFailure)
        {
            return checks.Error;
        }

        var (requester, target) = checks.Value;
        if (target.Status is not ("Active" or "Onboarding"))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Tenant {target.TenantCode} is {target.Status}; it takes no new administrators.");
        }

        if (!await administrators.TenantHasAdministratorRole(command.TargetTenantId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition,
                $"Tenant {target.TenantCode} has no TENANT_ADMIN role in force; provision its first administrators instead.");
        }

        var created = TenantAdministratorRequest.ForExistingTenant(command.TargetTenantId, command.Administrator.Details, requester, clock.UtcNow);
        return Record(created, target.TenantCode, "RequestAdditionalAdministrator");
    }

    /// <summary>The operator alone asks, for a tenant that exists, with no other request waiting, and usernames the tenant does not have.</summary>
    private async Task<Result<(Guid Requester, TenantProfile Target)>> Common(Guid targetTenantId, IEnumerable<string> usernames, CancellationToken cancellationToken)
    {
        if (tenant.TenantId != platform.Value.OperatorTenantId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only the platform operator asks for a tenant's administrators.");
        }

        var requester = Administrator.Require(execution);
        if (requester.IsFailure)
        {
            return requester.Error;
        }

        if (await tenants.Get(targetTenantId, cancellationToken) is not { } target)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The tenant was not found.");
        }

        if (await administrators.HasPendingRequest(targetTenantId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.DuplicateRecord, $"A request for {target.TenantCode}'s administrators is already waiting for approval.");
        }

        if (await administrators.UsernamesTaken(targetTenantId, usernames, cancellationToken) is { Count: > 0 } taken)
        {
            return Error.Conflict(ErrorCodes.DuplicateRecord, $"Tenant {target.TenantCode} already has a user named {string.Join(", ", taken)}.");
        }

        return (requester.Value, target);
    }

    private Result<Guid> Record(Result<TenantAdministratorRequest> created, string tenantCode, string operation)
    {
        if (created.IsFailure)
        {
            return created.Error;
        }

        var request = created.Value;
        administrators.Add(request);
        audit.Record(new AuditEntry("IDENTITY", operation, nameof(TenantAdministratorRequest), request.Id.ToString(),
            new Dictionary<string, string?> { ["tenant"] = tenantCode, ["usernames"] = string.Join(", ", request.People.Select(p => p.Username)) }));
        return request.Id;
    }
}

internal sealed class RetryAdministratorIdentityHandler(ITenantAdministrators administrators, Integration.Contracts.IRecordDeliveries deliveries,
    ITenantContext tenant, IOptions<PlatformOptions> platform, IAuditTrail audit) : ICommandHandler<RetryAdministratorIdentity, int>
{
    public async Task<Result<int>> Handle(RetryAdministratorIdentity command, CancellationToken cancellationToken)
    {
        if (tenant.TenantId != platform.Value.OperatorTenantId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only the platform operator retries a tenant administrator's identity.");
        }

        if (await administrators.Get(command.RequestId, cancellationToken) is not { } request)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The request was not found.");
        }

        var view = await AdministratorIdentity.View(request, administrators, deliveries, cancellationToken);
        var retry = view.Administrators.Where(a => a.UserId is not null && a.IdentityState is AdministratorIdentityState.Failed or AdministratorIdentityState.Parked).ToList();
        if (retry.Count == 0)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No administrator's identity is parked or failed.");
        }

        var rearmed = 0;
        foreach (var person in retry)
        {
            rearmed += await deliveries.Retry(request.TargetTenantId, AdministratorIdentity.Consumer, AdministratorIdentity.AggregateType, person.UserId!.Value,
                cancellationToken);
        }

        audit.Record(new AuditEntry("IDENTITY", "RetryAdministratorIdentity", nameof(TenantAdministratorRequest), request.Id.ToString(),
            new Dictionary<string, string?> { ["retried"] = rearmed.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return rearmed;
    }
}

internal sealed class GetTenantAdministratorRequestHandler(ITenantAdministrators administrators, Integration.Contracts.IRecordDeliveries deliveries,
    IApprovalHistory history) : IQueryHandler<GetTenantAdministratorRequest, TenantAdministratorRequestView>
{
    public async Task<Result<TenantAdministratorRequestView>> Handle(GetTenantAdministratorRequest query, CancellationToken cancellationToken) =>
        await administrators.Get(query.RequestId, cancellationToken) is { } request
            ? await AdministratorIdentity.View(request, administrators, deliveries, cancellationToken) with
            {
                LatestRejection = await history.Latest(IdentityApprovals.TenantAdministratorBusinessRefType, request.Id, cancellationToken),
            }
            : Error.NotFound(ErrorCodes.NotFound, "The request was not found.");
}

internal sealed class ListTenantAdministratorRequestsHandler(ITenantAdministrators administrators, Integration.Contracts.IRecordDeliveries deliveries)
    : IQueryHandler<ListTenantAdministratorRequests, IReadOnlyList<TenantAdministratorRequestView>>
{
    public async Task<Result<IReadOnlyList<TenantAdministratorRequestView>>> Handle(ListTenantAdministratorRequests query, CancellationToken cancellationToken)
    {
        var views = new List<TenantAdministratorRequestView>();
        foreach (var request in await administrators.List(query.TargetTenantId, cancellationToken))
        {
            views.Add(await AdministratorIdentity.View(request, administrators, deliveries, cancellationToken));
        }

        return views;
    }
}

/// <summary>
/// Identity's policy for Workflow's decision on a first-administrator request (OQ-067): granted, the user, the role
/// and the assignment are created in the new tenant in this same transaction; rejected, nothing is created. A decision
/// the request will not take parks the delivery (D-5, ADR-022): an ending (APPROVAL_ENDING_NOT_APPLIED), or a grant
/// (APPROVAL_NOT_APPLIED) on a request already decided otherwise or for a tenant that has had users since it was asked for;
/// Workflow then marks the approval not applied and tells the requester.
/// </summary>
public sealed class TenantAdministratorApprovalPolicy(ITenantAdministrators administrators, IClock clock) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>
{
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.ApprovalId, granted.CheckerId, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.ApprovalId, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid requestId, bool approved, Guid approvalId, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType != IdentityApprovals.TenantAdministratorBusinessRefType || await administrators.Get(requestId, cancellationToken) is not { } request)
        {
            return;
        }

        // A redelivery changes nothing: every ending other than approval (rejected, expired, withdrawn) already rejected it,
        // or this checker's approval already provisioned it.
        if (approved ? request.Status == TenantAdministratorRequestStatus.Approved && request.DecidedBy == checkerId
                : request.Status == TenantAdministratorRequestStatus.Rejected)
        {
            return;
        }

        if (request.Status != TenantAdministratorRequestStatus.PendingApproval)
        {
            throw NotApplied(approved, approvalId);
        }

        // A new tenant that has had users since the request was made, an existing tenant whose TENANT_ADMIN role is no longer
        // in force, or a username the tenant now has: the grant cannot be applied as asked.
        if (approved && (request.Kind == TenantAdministratorRequestKind.FirstAdministrators
                ? await administrators.TenantHasUsers(request.TargetTenantId, cancellationToken)
                : !await administrators.TenantHasAdministratorRole(request.TargetTenantId, cancellationToken)
                  || (await administrators.UsernamesTaken(request.TargetTenantId, request.People.Select(p => p.Username), cancellationToken)).Count > 0))
        {
            throw NotApplied(approved, approvalId);
        }

        var userIds = approved ? await administrators.Provision(request, checkerId, cancellationToken) : null;
        if (request.Decide(approved, checkerId, userIds, clock.UtcNow).IsFailure)
        {
            throw NotApplied(approved, approvalId);
        }
    }

    private static PrerequisiteMissingException NotApplied(bool approved, Guid approvalId) =>
        new(approved ? PrerequisiteCodes.ApprovalNotApplied : PrerequisiteCodes.ApprovalEndingNotApplied,
            $"{IdentityApprovals.TenantAdministratorApprovalType}:{approvalId}");
}
