namespace Insys.BuildingBlocks.Application.Auditing;

public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
}

/// <summary>
/// A sensitive operation to audit (IAM-006). Actor, tenant, time and correlation are added by the writer.
/// Details must already be masked; use <see cref="AuditMask"/>.
/// </summary>
public sealed record AuditEntry(
    string EventType,
    string Operation,
    string TargetType,
    string TargetId,
    IReadOnlyDictionary<string, string?>? Details = null);

/// <summary>
/// Collects audit entries during a command. The transaction pipeline writes them to audit.audit_event in the
/// command's transaction when it succeeds, and in a separate transaction with outcome Failure when it fails.
/// </summary>
public interface IAuditTrail
{
    void Record(AuditEntry entry);
}

public static class AuditMask
{
    /// <summary>Keeps the last four characters, for example "****1234".</summary>
    public static string? Last4(string? value) =>
        value is null ? null : value.Length <= 4 ? new string('*', value.Length) : $"****{value[^4..]}";
}
