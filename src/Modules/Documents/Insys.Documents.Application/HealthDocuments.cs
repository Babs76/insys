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
using System.Globalization;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Health.Contracts;

namespace Insys.Documents.Application;

/// <summary>
/// Documents' reaction to Health's payment runs: a released run's remittance advices, one document per provider, from the
/// remittance advice format's template (health.cfg_remittance_advice_formats.template, "TPL-RA" or "TPL-RA v2"; the
/// template version in force is used). Health attaches each to its remittance on DocumentGenerated. A failure is retried
/// and then dead-lettered; the run is never rolled back.
/// </summary>
public sealed class HealthDocumentPolicies(DocumentGenerator generator, DocumentLanguages languages) : IIntegrationEventHandler<ProviderPaymentRunReleased>
{
    public async Task Handle(ProviderPaymentRunReleased released, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (released.RemittanceTemplate is not { } configured)
        {
            return;
        }

        var code = configured.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        foreach (var remittance in released.Remittances)
        {
            // In the provider's language by the chain; a provider organisation has none, so the tenant default (addendum §5).
            var template = await languages.Template(code, await languages.Chain(remittance.ProviderPartyId, cancellationToken), cancellationToken)
                           ?? throw new InvalidOperationException($"Remittance advice template {code} is not in force in the tenant's languages; run {released.RunCode}'s advices were not generated.");
            var data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["run_code"] = released.RunCode, ["provider_id"] = remittance.ProviderId, ["amount"] = remittance.Amount, ["currency"] = released.Currency,
                ["released_on"] = released.ReleasedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }, JsonSerializerOptions.Web);
            var document = await generator.Generate(new GenerateDocument(template.TemplateCode, template.Language, template.DocumentType, HealthReferences.RemittanceRefType,
                remittance.RemittanceId, null, null, data), cancellationToken);
            if (document.IsFailure)
            {
                throw new InvalidOperationException($"The remittance advice for {remittance.ProviderId} in run {released.RunCode} was not generated: {document.Error.Message}");
            }
        }
    }
}
