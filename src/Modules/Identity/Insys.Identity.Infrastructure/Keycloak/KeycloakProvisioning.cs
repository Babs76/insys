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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Identity.Application.Users;
using Insys.Identity.Domain.Users;
using Insys.Tenancy.Contracts;

namespace Insys.Identity.Infrastructure.Keycloak;

/// <summary>
/// Identity's consumer for the users of INSYS's own realm (keycloak-user-provisioning §4, §5):
/// <list type="bullet">
/// <item>creates the Keycloak user (or links the one a retry finds, with the same username and tenant), records its id
/// as the external subject and asks for the invitation, in the consumer's own transaction (INT-005);</item>
/// <item>sends the invitation email as its own step;</item>
/// <item>disables the Keycloak user and ends its sessions when the user is locked or disabled, and enables it again on unlock.</item>
/// </list>
/// Keycloak unreachable parks the delivery (KEYCLOAK_UNAVAILABLE); a username or email of another tenant's user parks it
/// for the operator (KEYCLOAK_USER_CONFLICT); any other refusal retrying cannot fix (a 4xx) parks it with Keycloak's reason
/// (KEYCLOAK_USER_REFUSED, BR-006), never as unavailable; an email that cannot be sent parks only the email (KEYCLOAK_EMAIL_UNSENT).
/// Keycloak usernames are tenant-qualified, "&lt;tenant code&gt;.&lt;username&gt;", as one realm serves every tenant.
/// </summary>
public sealed class KeycloakProvisioning(IUserRepository users, IKeycloakAdmin keycloak, ITenantProfiles tenants, ITenantContext tenant, IClock clock,
    IAuditTrail audit, PermissionCache permissions) :
    IIntegrationEventHandler<Contracts.UserIdentityRequested>,
    IIntegrationEventHandler<Contracts.UserInvitationRequested>,
    IIntegrationEventHandler<Contracts.UserDisabled>,
    IIntegrationEventHandler<Contracts.UserReenabled>
{
    public const string Realm = "insys";

    public async Task Handle(Contracts.UserIdentityRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await users.Get(requested.UserId, cancellationToken) is not { Status: UserStatus.PendingIdentity } user)
        {
            return;
        }

        var tenantId = tenant.RequireTenantId();
        var code = (await tenants.Get(tenantId, cancellationToken))?.TenantCode
                   ?? throw new InvalidOperationException($"Tenant {tenantId} has no profile; its user cannot be created in Keycloak.");
        var username = Username(code, user.Username);
        var subject = await Keycloak(async () =>
        {
            // A retry after Keycloak created the user but before INSYS recorded it finds it here: the same username and tenant.
            if (await keycloak.FindByUsername(username, cancellationToken) is { } existing)
            {
                return existing.TenantId == tenantId.ToString()
                    ? existing.Id
                    : throw new PrerequisiteMissingException(PrerequisiteCodes.KeycloakUserConflict, "username");
            }

            var (first, last) = Names(user.DisplayName);
            return await keycloak.Create(username, user.Email!, first, last, tenantId, cancellationToken);
        });

        var linked = user.LinkIdentity(subject, clock.UtcNow);
        if (linked.IsFailure)
        {
            throw new InvalidOperationException($"User {user.Id} cannot be linked to its Keycloak user: {linked.Error.Code}");
        }

        // BR-018: the user's next request resolves afresh, with no wait for a cached entry to expire.
        permissions.Evict(tenantId, subject);

        audit.Record(new AuditEntry("IDENTITY", "CreateKeycloakUser", nameof(UserAccount), user.Id.ToString(),
            new Dictionary<string, string?> { ["realm"] = Realm }));
    }

    public async Task Handle(Contracts.UserInvitationRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await users.Get(requested.UserId, cancellationToken) is not { IdpCode: UserAccount.OwnRealm, ExternalSubject: { } subject, Status: UserStatus.Invited })
        {
            return;
        }

        try
        {
            await Keycloak(async () =>
            {
                await keycloak.SendActions(subject, requested.Actions, cancellationToken);
                return true;
            });
        }
        catch (KeycloakEmailException)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.KeycloakEmailUnsent, Realm);
        }

        audit.Record(new AuditEntry("IDENTITY", "SendInvitation", nameof(UserAccount), requested.UserId.ToString(),
            new Dictionary<string, string?> { ["actions"] = string.Join(",", requested.Actions) }));
    }

    public Task Handle(Contracts.UserDisabled disabled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        SetEnabled(disabled.UserId, false, cancellationToken);

    public Task Handle(Contracts.UserReenabled reenabled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        SetEnabled(reenabled.UserId, true, cancellationToken);

    /// <summary>The Keycloak username in the shared realm: the tenant code and the INSYS username, lower case (Keycloak keeps usernames in lower case).</summary>
    public static string Username(string tenantCode, string username) => $"{tenantCode}.{username}".ToLowerInvariant();

    private async Task SetEnabled(Guid userId, bool enabled, CancellationToken cancellationToken)
    {
        if (await users.Get(userId, cancellationToken) is not { IdpCode: UserAccount.OwnRealm, ExternalSubject: { } subject })
        {
            return;
        }

        await Keycloak(async () =>
        {
            await keycloak.SetEnabled(subject, enabled, cancellationToken);
            if (!enabled)
            {
                await keycloak.Logout(subject, cancellationToken);
            }

            return true;
        });
        audit.Record(new AuditEntry("IDENTITY", enabled ? "EnableKeycloakUser" : "DisableKeycloakUser", nameof(UserAccount), userId.ToString(),
            new Dictionary<string, string?> { ["realm"] = Realm }));
    }

    private static async Task<T> Keycloak<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (KeycloakUnavailableException)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.KeycloakUnavailable, Realm);
        }
        catch (KeycloakConflictException conflict)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.KeycloakUserConflict, conflict.Field);
        }
        catch (KeycloakRefusedException refused)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.KeycloakUserRefused, refused.Reason);
        }
    }

    private static (string First, string Last) Names(string displayName)
    {
        var parts = displayName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (parts[0], parts[0]);
    }
}
