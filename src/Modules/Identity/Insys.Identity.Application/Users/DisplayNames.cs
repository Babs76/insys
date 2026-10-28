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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Microsoft.Extensions.Options;

namespace Insys.Identity.Application.Users;

// BR-021 (wave2-3-proposals.md §3, decided by Ade on 28 October 2026): staff see who did what by name. The views return
// user IDs; one lookup per screen turns them into display names, so names stay in Identity and stay current.

/// <param name="DisplayName">The user's display name; "Platform operator" for a platform operator who acted in the tenant.</param>
public sealed record DisplayNameView(Guid Id, string DisplayName);

/// <summary>
/// Query LookupDisplayNames: up to 100 user IDs. Users of the caller's own tenant, active or not, come back with their
/// display name; a platform operator's ID with the label "Platform operator", never a personal name; any other ID is left
/// out, never an error, so the lookup does not say whether an ID exists elsewhere.
/// </summary>
public sealed record LookupDisplayNames(IReadOnlyList<Guid> Ids) : IQuery<IReadOnlyList<DisplayNameView>>;

public sealed class LookupDisplayNamesValidator : AbstractValidator<LookupDisplayNames>
{
    public const int MaxIds = 100;

    public LookupDisplayNamesValidator() =>
        RuleFor(q => q.Ids).NotNull().Must(ids => ids.Count is > 0 and <= MaxIds).WithMessage($"Give between 1 and {MaxIds} user IDs.");
}

public interface IDisplayNames
{
    /// <summary>The display names of these IDs among the tenant's users, of any status.</summary>
    Task<IReadOnlyDictionary<Guid, string>> OfUsers(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Which of these IDs are users of the operator tenant.</summary>
    Task<IReadOnlySet<Guid>> PlatformOperators(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

internal sealed class LookupDisplayNamesHandler(IDisplayNames names, ITenantContext tenant, IOptions<PlatformOptions> platform)
    : IQueryHandler<LookupDisplayNames, IReadOnlyList<DisplayNameView>>
{
    public const string PlatformOperator = "Platform operator";

    public async Task<Result<IReadOnlyList<DisplayNameView>>> Handle(LookupDisplayNames query, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var ids = query.Ids.Distinct().ToList();
        var named = await names.OfUsers(tenantId, ids, cancellationToken);
        var rest = ids.Where(id => !named.ContainsKey(id)).ToList();
        var operators = rest.Count == 0 || tenantId == platform.Value.OperatorTenantId
            ? new HashSet<Guid>()
            : await names.PlatformOperators(rest, cancellationToken);
        return ids.Where(id => named.ContainsKey(id) || operators.Contains(id))
            .Select(id => new DisplayNameView(id, named.TryGetValue(id, out var name) ? name : PlatformOperator)).ToList();
    }
}
