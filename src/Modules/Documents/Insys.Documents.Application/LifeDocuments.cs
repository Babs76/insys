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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Life.Contracts;
using Insys.ProductFactory.Contracts;

namespace Insys.Documents.Application;

/// <summary>
/// Documents' reaction to Life's policies (process "Life proposal to policy": GenerateDocument, policy schedule), with
/// the template assigned to the product. A failure is retried and then dead-lettered; the policy is never rolled back.
/// </summary>
public sealed class LifeDocumentPolicies(DocumentGenerator generator, ISellableProducts products, DocumentLanguages languages, ITenantContext tenant, IBusinessDate businessDate)
    : IIntegrationEventHandler<LifePolicyIssued>
{
    public const string PolicySchedule = "POLICY_SCHEDULE";

    public async Task Handle(LifePolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // In the policyholder's language by the chain (docs/specs/addenda/localisation.md §5).
        var chain = await languages.Chain(issued.PolicyholderPartyId, cancellationToken);
        var template = DocumentLanguages.Pick(await products.Templates(tenant.RequireTenantId(), issued.ProductCode, businessDate.Today, cancellationToken), PolicySchedule, chain)
            ?? throw new PrerequisiteMissingException(PrerequisiteCodes.DocumentTemplateNotInForce, $"{issued.ProductCode}:{PolicySchedule}");
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["policy_number"] = issued.PolicyNumber, ["product_code"] = issued.ProductCode, ["product_version"] = issued.ProductVersion,
            ["inception_date"] = issued.InceptionDate.ToString("yyyy-MM-dd"), ["term_end_date"] = issued.TermEndDate?.ToString("yyyy-MM-dd"),
            ["premium"] = issued.Premium, ["frequency"] = issued.BillingFrequency, ["sum_assured"] = issued.SumAssured, ["currency"] = issued.Currency,
        }, JsonSerializerOptions.Web);
        var document = await generator.Generate(new GenerateDocument(template.TemplateCode, template.Language, template.DocumentType, LifeDocuments.PolicyRefType,
            issued.PolicyId, $"{issued.ProductCode} v{issued.ProductVersion}", null, data), cancellationToken);
        if (document.IsFailure)
        {
            throw new InvalidOperationException($"{PolicySchedule} for Life policy {issued.PolicyNumber} was not generated: {document.Error.Message}");
        }
    }
}
