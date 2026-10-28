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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Documents.Contracts;
using Insys.General.Contracts;
using Insys.General.Domain.Policies;
using Insys.Reinsurance.Contracts;

namespace Insys.General.Application;

/// <summary>
/// BR-038: records what a policy's issue sets off elsewhere, as each arrives: Billing's invoices, Documents' documents and
/// Reinsurance's cessions for a General policy. The policy read lists them. An event about another domain's policy, or a
/// General policy General does not have, is not recorded.
/// </summary>
public sealed class PolicyEffectsRecorder(IGeneralRepository general) :
    IIntegrationEventHandler<InvoiceIssued>,
    IIntegrationEventHandler<DocumentGenerated>,
    IIntegrationEventHandler<CessionCalculated>
{
    public Task Handle(InvoiceIssued issued, EventEnvelope envelope, CancellationToken cancellationToken) =>
        issued.SourceDomain == GeneralRatingSubjects.SourceDomain
            ? Record(issued.PolicyRef, PolicyEffect.Invoice, issued.InvoiceId, cancellationToken)
            : Task.CompletedTask;

    public Task Handle(DocumentGenerated generated, EventEnvelope envelope, CancellationToken cancellationToken) =>
        generated.BusinessRefType == GeneralDocuments.PolicyRefType
            ? Record(generated.BusinessRefId, PolicyEffect.Document, generated.DocumentId, cancellationToken)
            : Task.CompletedTask;

    public Task Handle(CessionCalculated calculated, EventEnvelope envelope, CancellationToken cancellationToken) =>
        calculated.SourceDomain == GeneralRatingSubjects.SourceDomain
            ? Record(calculated.PolicyRef, PolicyEffect.Cession, calculated.CessionId, cancellationToken)
            : Task.CompletedTask;

    private async Task Record(Guid policyId, string effectType, Guid recordId, CancellationToken cancellationToken)
    {
        if (await general.Policy(policyId, cancellationToken) is not null)
        {
            await general.AddEffect(PolicyEffect.Record(policyId, effectType, recordId), cancellationToken);
        }
    }
}
