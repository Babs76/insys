/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.BuildingBlocks.Application;
using Insys.ProductFactory.Contracts;
using Insys.Rules.Contracts;

namespace Insys.General.Infrastructure;

/// <summary>
/// Onboarding readiness GENERAL_REFERRAL_AUTHORITY (PRODUCT_FACTORY): every sellable General product has a role whose
/// underwriting authority (rules.cfg_authority_matrices) may decide its referrals. Without one, a referred quote of the
/// product can be decided by nobody, and its referral task waits in the default queue.
/// </summary>
internal sealed class GeneralReferralReadiness(IPublishedProducts products, IProductDomains domains, IUnderwritingAuthorities authorities) : IReadinessCheck
{
    public IReadOnlyCollection<string> Steps { get; } = ["PRODUCT_FACTORY"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var codes = (await products.Sellable(context.TenantId, context.Day, cancellationToken)).Select(p => p.ProductCode).Distinct(StringComparer.Ordinal).ToList();
        var domainOf = await domains.DomainsOf(context.TenantId, codes, cancellationToken);
        var unauthorised = new List<string>();
        foreach (var code in codes.Where(c => domainOf.GetValueOrDefault(c) == "GENERAL").Order(StringComparer.Ordinal))
        {
            if (!(await authorities.ForProduct(context.TenantId, code, context.Day, cancellationToken)).Any(a => a.DecisionTypes.Count > 0))
            {
                unauthorised.Add(code);
            }
        }

        return [ReadinessResult.Of("PRODUCT_FACTORY", "GENERAL_REFERRAL_AUTHORITY",
            "Every sellable General product has a role with underwriting authority to decide its referrals (product listed when not)", unauthorised)];
    }
}
