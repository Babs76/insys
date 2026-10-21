namespace Insys.BuildingBlocks.Domain;

/// <summary>How an error maps to HTTP (PROMPT §4.13).</summary>
public enum ErrorKind
{
    Validation,
    Conflict,
    NotFound,
    Forbidden,
    PreconditionFailed,
    PreconditionRequired,
    Unauthorized,
}

/// <summary>A failure with a stable code from the error code catalogue (integration.ref_error_code_catalogue).</summary>
public sealed record Error(string Code, string Message, ErrorKind Kind)
{
    public IReadOnlyDictionary<string, string[]>? Details { get; init; }

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, ErrorKind.Validation) { Details = details };

    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);

    public static Error PreconditionFailed(string code, string message) => new(code, message, ErrorKind.PreconditionFailed);
}
