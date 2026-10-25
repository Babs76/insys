/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Communications.Contracts;
using Insys.Health.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Communications.Application.Triggers;

/// <summary>The merge fields of the case notices (addenda/cases.md §7).</summary>
public static class CaseMergeFields
{
    public const string CaseNumber = "case_number";
    public const string CaseType = "case_type";
    public const string ExpectedDate = "expected_date";
    public const string DecisionReasons = "decision_reasons";
    public const string ReviewOptions = "review_options";
    public const string OmbudDetails = "ombud_details";
    public const string RepresentationsDeadline = "representations_deadline";

    public static readonly string[] All = [CaseNumber, CaseType, ExpectedDate, DecisionReasons, ReviewOptions, OmbudDetails, RepresentationsDeadline];
}

/// <summary>
/// The case notices (addenda/cases.md §7): the merge values come from Workflow's notice data (ICaseNotices), never from
/// the event, so a notice shows the case as it is when it is queued. A value the case does not have is left out.
/// </summary>
public static class CaseNotices
{
    public static NotificationRequest For(string typeCode, Guid caseId, CaseNoticeData data, Guid recipientPartyId, Func<DateTimeOffset, DateOnly> dateOf)
    {
        var merge = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CaseMergeFields.CaseNumber] = data.CaseNumber,
            [CaseMergeFields.CaseType] = data.CaseTypeCode,
        };
        Add(merge, CaseMergeFields.ExpectedDate, data.ExpectedBy is { } expected ? TriggerFormats.Date(dateOf(expected)) : null);
        Add(merge, CaseMergeFields.DecisionReasons, data.DecisionReasons);
        Add(merge, CaseMergeFields.ReviewOptions, data.ReviewOptions);
        Add(merge, CaseMergeFields.OmbudDetails, data.OmbudDetails);
        Add(merge, CaseMergeFields.RepresentationsDeadline, data.RepresentationsDeadline is { } deadline ? TriggerFormats.Date(dateOf(deadline)) : null);

        // TODO(spec): the case's product and branch (F103) are not in Workflow's notice data; the notice is tenant-wide until they are.
        return new NotificationRequest(typeCode, recipientPartyId, TriggerSources.Case, caseId, merge);
    }

    private static void Add(Dictionary<string, string> merge, string field, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            merge[field] = value;
        }
    }
}

/// <summary>
/// CASE_ACKNOWLEDGED, CASE_UPDATE and CASE_DECISION (addenda/cases.md §7, OQ-107): to the complainant, and to the
/// provider for a dispute it raised. A case with no notice data or no recipient queues nothing and is logged by its ID
/// only; a type that is not configured parks the delivery (NOTIFICATION_TYPE_NOT_CONFIGURED, ADR-022).
/// TODO(spec): the broker that raised a case on a client's behalf is told as well (§7), but an intermediary has no party
/// to send to yet (commission.mst_intermediary_register has no party, and Distribution has no query for one).
/// </summary>
public sealed class CaseNoticeTrigger(ICaseNotices cases, IProviderDirectory providers, NotificationQueue queue, ITenantContext tenant, IBusinessDate businessDate,
    ILogger<CaseNoticeTrigger> logger) :
    IIntegrationEventHandler<CaseAcknowledged>,
    IIntegrationEventHandler<CaseUpdated>,
    IIntegrationEventHandler<CaseDecided>
{
    public Task Handle(CaseAcknowledged e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(NotificationTypes.CaseAcknowledged, e.CaseId, envelope, cancellationToken);

    public Task Handle(CaseUpdated e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(NotificationTypes.CaseUpdate, e.CaseId, envelope, cancellationToken);

    public Task Handle(CaseDecided e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(NotificationTypes.CaseDecision, e.CaseId, envelope, cancellationToken);

    private async Task Notify(string typeCode, Guid caseId, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await cases.ForNotice(caseId, cancellationToken) is not { } data)
        {
            logger.LogInformation("No notice data for {SourceType} {SourceId}; nothing queued", TriggerSources.Case, caseId);
            return;
        }

        var recipients = new List<Guid>();
        if (data.ComplainantPartyId is { } complainant)
        {
            recipients.Add(complainant);
        }

        if (data.RaisedByProviderId is { } providerId
            && await providers.PartyOf(tenant.RequireTenantId(), providerId, businessDate.Today, cancellationToken) is { } provider && !recipients.Contains(provider))
        {
            recipients.Add(provider);
        }

        if (recipients.Count == 0)
        {
            logger.LogInformation("No notice recipient for {SourceType} {SourceId}; nothing queued", TriggerSources.Case, caseId);
            return;
        }

        foreach (var party in recipients)
        {
            await queue.Queue(CaseNotices.For(typeCode, caseId, data, party, businessDate.DateOf), envelope.EventId, cancellationToken);
        }
    }
}
