/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Audit
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
using FluentValidation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;

namespace Insys.Audit.Application;

public sealed record AuditEventView(
    Guid Id, string EventType, [property: OneOf(AuditActorTypes.User, AuditActorTypes.Service)] string ActorType, string ActorId, string Operation, string TargetType, string TargetId, string Outcome,
    DateTimeOffset OccurredAt, Guid CorrelationId, JsonElement? Details);

public interface IAuditEventQueries
{
    Task<IReadOnlyList<AuditEventView>> List(string? targetType, string? targetId, string? actorId, Guid? afterId, int limit, CancellationToken cancellationToken);
}

/// <summary>Reads the tenant's audit trail (IAM-006), newest first.</summary>
public sealed record ListAuditEvents(string? TargetType, string? TargetId, string? ActorId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<AuditEventView>>;

public sealed class ListAuditEventsValidator : AbstractValidator<ListAuditEvents>
{
    public ListAuditEventsValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 200);
        RuleFor(q => q.TargetType).MaximumLength(30);
        RuleFor(q => q.TargetId).MaximumLength(80);
        RuleFor(q => q.ActorId).MaximumLength(80);
    }
}

internal sealed class ListAuditEventsHandler(IAuditEventQueries queries) : IQueryHandler<ListAuditEvents, IReadOnlyList<AuditEventView>>
{
    public async Task<Result<IReadOnlyList<AuditEventView>>> Handle(ListAuditEvents query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.TargetType, query.TargetId, query.ActorId, query.AfterId, query.Limit, cancellationToken));
}

/// <summary>
/// Who acted (BR-023): a person (USER, staff, platform operator or portal user, whose ActorId is their user ID) or INSYS
/// itself (SERVICE: a job, an event consumer or an integration, whose ActorId names it).
/// </summary>
public static class AuditActorTypes
{
    public const string User = "USER";

    public const string Service = "SERVICE";
}
