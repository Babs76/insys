namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// Platform-wide error codes (max 30 characters, the size of error_code in integration.ref_error_code_catalogue).
/// TODO(spec): the catalogue has no seed rows; these codes should be loaded into it in Phase 1.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";
    public const string NotFound = "NOT_FOUND";
    public const string Forbidden = "FORBIDDEN";
    public const string TenantContextMissing = "TENANT_CONTEXT_MISSING";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string PreconditionFailed = "PRECONDITION_FAILED";
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string DuplicateRecord = "DUPLICATE_RECORD";
    public const string DatabaseRuleViolated = "DATABASE_RULE_VIOLATED";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string MakerCheckerSamePerson = "MAKER_CHECKER_SAME_PERSON";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";
}
