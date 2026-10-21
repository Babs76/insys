using Insys.Integration.Domain.DeadLetters;

namespace Insys.Integration.Application.DeadLetters;

public interface IDeadLetterRepository
{
    Task<DeadLetter?> Get(Guid id, CancellationToken cancellationToken);

    void Add(DeadLetter deadLetter);
}

public sealed record DeadLetterSummary(Guid Id, string Source, string MessageRef, string Status, short Attempts, DateTimeOffset FirstFailedAt);

public sealed record DeadLetterDetail(
    Guid Id, string Source, string MessageRef, string Status, short Attempts, DateTimeOffset FirstFailedAt, string Error,
    System.Text.Json.JsonElement Payload, Guid? ResolvedBy, long RowVersion);

public interface IDeadLetterQueries
{
    Task<IReadOnlyList<DeadLetterSummary>> List(DeadLetterStatus? status, string? source, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<DeadLetterDetail?> Get(Guid id, CancellationToken cancellationToken);
}
