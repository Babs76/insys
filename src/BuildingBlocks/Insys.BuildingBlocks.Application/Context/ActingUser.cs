/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Application.Context;

/// <summary>
/// A background job acting for a recorded user (AIA-017): a new pattern, security-reviewed before batch 7.4 merges. Jobs otherwise act as a
/// service (<see cref="ExecutionScope.InitialiseForService"/>).
/// <list type="bullet">
/// <item>Each scope resolves the user afresh, from the same IAM role assignments and cache as a request (<see cref="IPrincipalResolver"/>), so a role
/// revoked since the run started stops it at the next scope: the job opens one when it starts and another when it writes.</item>
/// <item>It acts only for the user who asked, by the token subject they asked with, and only while that subject is still that user, active in an
/// active or onboarding tenant. Never for the system requester (BR-109) or an external user (a portal user never reaches a staff job).</item>
/// <item>The scope carries no client, so channel rows (F005) don't apply, as for any staff caller without one.</item>
/// <item>Each operation the job performs is then checked as its endpoint would be (<see cref="ActingUser.Refusal"/>): the function, then the
/// permission, then the record scope (F103) of that permission.</item>
/// </list>
/// </summary>
public sealed class ActingUserScopes(IServiceScopeFactory scopes)
{
    /// <summary>A scope acting for the user, or null when they may no longer act (unknown, locked or disabled, another user now, or the tenant closed).</summary>
    public async Task<ActingUser?> Begin(Guid tenantId, string subject, Guid userId, Guid? correlationId, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        var principal = await scope.ServiceProvider.GetRequiredService<IPrincipalResolver>().ResolveForJob(tenantId, subject, cancellationToken);
        if (principal is null || principal.UserId != userId || MakerChecker.IsSystem(userId) || principal.External is not null)
        {
            await scope.DisposeAsync();
            return null;
        }

        var execution = scope.ServiceProvider.GetRequiredService<ExecutionScope>();
        execution.InitialiseForUser(tenantId, userId.ToString(), principal.Permissions, correlationId, principal.Grants, clientId: null, principal.RoleCodes,
            external: null, multiFactor: false, principal.ConfigurationOnly, principal.Functions);
        return new ActingUser(scope, execution);
    }
}

/// <summary>A job's scope acting for a user; dispose it when the step is done.</summary>
public sealed class ActingUser(AsyncServiceScope scope, ExecutionScope execution) : IAsyncDisposable
{
    public IServiceProvider Services => scope.ServiceProvider;

    public Guid UserId => Guid.Parse(execution.ActorId);

    /// <summary>
    /// Null when the user may perform the operation now, as its endpoint would decide: one of its functions usable (ADR-026 §2, in the same order
    /// of refusals), then the permission. On success the permission becomes the scope's request permission, so handlers apply its record scope (F103).
    /// </summary>
    /// <param name="functions">The endpoint's functions, any one of which allows it.</param>
    public Error? Refusal(string permission, params string[] functions) => Check(execution, permission, functions);

    /// <summary>The check itself, for any user scope.</summary>
    /// <param name="isRead">A read, whose records outside the permission's scope are not found rather than refused (F103).</param>
    public static Error? Check(ExecutionScope execution, string permission, IReadOnlyList<string> functions, bool isRead = false)
    {
        if (execution.ActorType != ActorType.User)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a user's scope can act for a user.");
        }

        // A user with no resolved functions is refused, never let through: a request would have resolved them.
        if (functions.Count > 0 && (execution.Functions?.Refusal(functions) ?? (execution.Functions is null ? ErrorCodes.FunctionDisabled : null)) is { } code)
        {
            return code == ErrorCodes.TenantNotActive
                ? Error.Conflict(code, "The tenant is still onboarding: business transactions start once it is activated.")
                : Error.Forbidden(code, "The user may not use this function now.");
        }

        if (!execution.HasPermission(permission))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "The user no longer holds the permission this step needs.");
        }

        execution.UseRequest(permission, isRead);
        return null;
    }

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
