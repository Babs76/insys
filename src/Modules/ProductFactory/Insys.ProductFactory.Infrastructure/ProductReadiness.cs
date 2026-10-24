/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory
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

namespace Insys.ProductFactory.Infrastructure;

/// <summary>
/// Onboarding readiness for PRODUCT_FACTORY: PRODUCT_PUBLISHED, a product version published and sellable (with the
/// tenant's availability, checked by Tenancy); PRODUCT_TEMPLATES, a document template assigned to every sellable
/// product (the document types a product issues are not specified, so at least one is required).
/// </summary>
internal sealed class ProductReadiness(IPublishedProducts published, ISellableProducts products) : IReadinessCheck
{
    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var sellable = await published.Sellable(context.TenantId, context.Day, cancellationToken);
        var untemplated = new List<string>();
        foreach (var (code, _) in sellable)
        {
            if ((await products.Templates(context.TenantId, code, context.Day, cancellationToken)).Count == 0)
            {
                untemplated.Add($"{code}: product.cfg_product_template_assignments");
            }
        }

        return
        [
            ReadinessResult.Of("PRODUCT_FACTORY", "PRODUCT_PUBLISHED", "A product published and made available to the tenant",
                sellable.Count > 0 ? [] : ["a product version published and in its sale window"]),
            ReadinessResult.Of("PRODUCT_FACTORY", "PRODUCT_TEMPLATES", "A document template assigned to every published product", untemplated),
        ];
    }
}
