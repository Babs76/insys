namespace Insys.BuildingBlocks.Application;

/// <summary>The stored outcome of an idempotent request (INT-003, DBA-008).</summary>
public sealed record IdempotentResponse(int StatusCode, string? ContentType, string? Body, IReadOnlyDictionary<string, string> Headers);

public enum IdempotencyBeginOutcome
{
    Started,
    Replay,
    KeyReusedWithDifferentRequest,
    InProgress,
}

public sealed record IdempotencyBeginResult(IdempotencyBeginOutcome Outcome, IdempotentResponse? Response = null);

/// <summary>Stores idempotency keys in integration.idempotency_key, scoped to the current tenant.</summary>
public interface IIdempotencyStore
{
    Task<IdempotencyBeginResult> Begin(string key, string operation, string requestHash, CancellationToken cancellationToken);

    Task Complete(string key, IdempotentResponse response, CancellationToken cancellationToken);

    /// <summary>Releases the key after a failure that the client may retry (5xx).</summary>
    Task Release(string key, CancellationToken cancellationToken);
}
