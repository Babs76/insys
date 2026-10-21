using Insys.BuildingBlocks.Application.Auditing;

namespace Insys.BuildingBlocks.Infrastructure.Auditing;

/// <summary>Collects the audit entries recorded during one command.</summary>
public sealed class AuditTrail : IAuditTrail
{
    private readonly List<AuditEntry> _entries = [];

    public void Record(AuditEntry entry) => _entries.Add(entry);

    public IReadOnlyList<AuditEntry> TakeAll()
    {
        var entries = _entries.ToArray();
        _entries.Clear();
        return entries;
    }
}
