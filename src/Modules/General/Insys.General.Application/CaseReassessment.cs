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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.General.Domain.Claims;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.General.Application;

/// <summary>
/// A claim dispute or appeal decided upheld or partly upheld (F011, F012, F013, OQ-103, addenda/cases.md §2): each
/// General claim it links is reopened for reassessment, as ReopenClaim does, with the case number as the reason on the
/// audit trail. A claim already open (a redelivery, or one still being assessed) is left as it is; a link to another
/// domain's claim is not General's and is passed over.
/// </summary>
public sealed class ReopenClaimsOnUpheldCase(IGeneralRepository general, IAuditTrail audit, ILogger<ReopenClaimsOnUpheldCase> logger) : IIntegrationEventHandler<CaseDecided>
{
    public async Task Handle(CaseDecided decided, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!CaseReassessments.Reopens(decided))
        {
            return;
        }

        foreach (var link in decided.Links.Where(l => l.LinkType == CaseLinkTypes.Claim))
        {
            if (await general.Claim(link.RecordId, cancellationToken) is not { } claim)
            {
                continue;
            }

            if (claim.ReopenForCase(decided.CaseNumber))
            {
                audit.Record(new AuditEntry("GENERAL", "ReopenClaim", nameof(GeneralClaim), claim.Id.ToString(),
                    new Dictionary<string, string?> { ["case"] = decided.CaseNumber, ["outcome"] = decided.Outcome }));
            }
            else
            {
                logger.LogInformation("Claim {ClaimId} is {Status}; case {CaseId} leaves it as it is", claim.Id, claim.Status, decided.CaseId);
            }
        }
    }
}

/// <summary>Which decided cases reopen their claims (OQ-103): a dispute or appeal upheld or partly upheld.</summary>
public static class CaseReassessments
{
    public static bool Reopens(CaseDecided decided) =>
        decided.Family is CaseFamilies.Dispute or CaseFamilies.Appeal && decided.Outcome is CaseOutcomes.Upheld or CaseOutcomes.PartlyUpheld;
}
