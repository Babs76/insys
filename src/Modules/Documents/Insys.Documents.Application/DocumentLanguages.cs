/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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
using Insys.Party.Contracts;
using Insys.ProductFactory.Contracts;

namespace Insys.Documents.Application;

/// <summary>
/// The language of a generated document (docs/specs/addenda/localisation.md §5, OQ-068): the first language of the chain
/// "party's preferred language, tenant default, English" that has a template. Without one, generation fails as before.
/// </summary>
public sealed class DocumentLanguages(ILanguageSettings settings, IPartyLanguage parties, IDocumentCatalogue catalogue, ITenantContext tenant, IBusinessDate businessDate)
{
    /// <summary>The chain for a recipient; without a party (or for an organisation) it starts at the tenant default.</summary>
    public async Task<IReadOnlyList<string>> Chain(Guid? partyId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var preferred = partyId is { } id ? await parties.PreferredLanguage(tenantId, id, cancellationToken) : null;
        return LanguageChain.Chain(preferred, await settings.For(tenantId, cancellationToken));
    }

    /// <summary>A named template's version in force in the first language of the chain that has one.</summary>
    public async Task<TemplateDefinition?> Template(string templateCode, IReadOnlyList<string> chain, CancellationToken cancellationToken)
    {
        foreach (var language in chain)
        {
            if (await catalogue.Template(templateCode, language, businessDate.Today, cancellationToken) is { } template)
            {
                return template;
            }
        }

        return null;
    }

    /// <summary>Of a product's template assignments for one document type, the one in the first language of the chain.</summary>
    public static ProductTemplate? Pick(IEnumerable<ProductTemplate> assignments, string documentType, IReadOnlyList<string> chain)
    {
        var candidates = assignments.Where(t => t.DocumentType == documentType).ToList();
        return chain.Select(language => candidates.FirstOrDefault(t => t.Language == language)).FirstOrDefault(t => t is not null);
    }
}
