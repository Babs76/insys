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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Microsoft.Extensions.Options;

namespace Insys.Identity.Application.Users;

/// <summary>An action under a screen of the menu (ADR-026 §3). The label key is the front end's translation key; the label is in the request's language.</summary>
public sealed record MenuAction(string FunctionCode, string LabelKey, string Label);

/// <summary>A screen of the menu: its route and the actions on it the user holds.</summary>
public sealed record MenuScreen(string FunctionCode, string LabelKey, string Label, string? RouteKey, IReadOnlyList<MenuAction> Actions);

public sealed record MenuSubDomain(string Code, string LabelKey, string Label, string? Icon, IReadOnlyList<MenuScreen> Screens);

public sealed record MenuDomain(string Code, string LabelKey, string Label, string? Icon, IReadOnlyList<MenuSubDomain> SubDomains);

/// <summary>The signed-in user's menu: the domains, sub-domains and screens they may use, in the catalogue's order.</summary>
/// <param name="Audience">The audience the menu is for: CUSTOMER, BROKER or PROVIDER for an external user, PLATFORM in the operator tenant, else STAFF.</param>
public sealed record MenuView([property: OneOf("PLATFORM", "STAFF", "BROKER", "PROVIDER", "CUSTOMER")] string Audience, IReadOnlyList<MenuDomain> Domains);

/// <summary>Query GetMyMenu (ADR-026 §3).</summary>
public sealed record GetMyMenu : IQuery<MenuView>;

/// <summary>Builds a menu from the catalogue in force: the usable functions of an audience, labelled in a language chain.</summary>
public interface IMenuBuilder
{
    Task<MenuView> Build(IReadOnlySet<string> usable, string audience, IReadOnlyList<string> languages, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// The menu is the functions the user may use now (granted, available, enabled, entitled, and open while the tenant is
/// Onboarding), for their audience; a screen appears with its sub-domain and domain, an action only under a screen they hold.
/// </summary>
internal sealed class GetMyMenuHandler(ExecutionScope scope, ITenantContext tenant, IOptions<PlatformOptions> platform, RequestLanguage language, IMenuBuilder menu,
    IBusinessDate businessDate) : IQueryHandler<GetMyMenu, MenuView>
{
    public async Task<Result<MenuView>> Handle(GetMyMenu query, CancellationToken cancellationToken)
    {
        var audience = scope.External?.ChannelAudience ?? (tenant.TenantId == platform.Value.OperatorTenantId ? "PLATFORM" : "STAFF");
        return await menu.Build(scope.Functions?.Usable ?? new HashSet<string>(), audience, language.Chain, businessDate.Today, cancellationToken);
    }
}
