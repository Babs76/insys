using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;

namespace Insys.Audit.Application;

public sealed record AuditEventView(
    Guid Id, string EventType, string ActorType, string ActorId, string Operation, string TargetType, string TargetId, string Outcome,
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
