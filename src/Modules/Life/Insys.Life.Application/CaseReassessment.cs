/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Life.Domain.Claims;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Life.Application;

/// <summary>
/// A claim dispute or appeal decided upheld or partly upheld (F011, F012, F013, OQ-103, addenda/cases.md §2): each Life
/// claim it links that was declined (or closed unpaid) is assessed again, with the case number as the reason on the audit
/// trail. A claim already open is a redelivery or still being assessed, and is left as it is; another domain's claim is
/// passed over.
/// </summary>
public sealed class ReopenLifeClaimsOnUpheldCase(ILifeRepository life, IAuditTrail audit, ILogger<ReopenLifeClaimsOnUpheldCase> logger) : IIntegrationEventHandler<CaseDecided>
{
    public async Task Handle(CaseDecided decided, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (decided.Family is not (CaseFamilies.Dispute or CaseFamilies.Appeal) || decided.Outcome is not (CaseOutcomes.Upheld or CaseOutcomes.PartlyUpheld))
        {
            return;
        }

        foreach (var link in decided.Links.Where(l => l.LinkType == CaseLinkTypes.Claim))
        {
            if (await life.Claim(link.RecordId, cancellationToken) is not { } claim)
            {
                continue;
            }

            if (claim.ReopenForCase(decided.CaseNumber))
            {
                audit.Record(new AuditEntry("LIFE", "ReopenClaim", nameof(LifeClaim), claim.Id.ToString(),
                    new Dictionary<string, string?> { ["case"] = decided.CaseNumber, ["outcome"] = decided.Outcome }));
            }
            else
            {
                logger.LogInformation("Claim {ClaimId} is {Status}; case {CaseId} leaves it as it is", claim.Id, claim.Status, decided.CaseId);
            }
        }
    }
}
