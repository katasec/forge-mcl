namespace ForgeMission.Core.Tools;

// Queryable for the lifetime of the Client Runtime; records are not persisted.
public sealed class InMemoryCapabilityAuditLog : ICapabilityAuditLog
{
    private readonly Lock _gate = new();
    private readonly List<CapabilityAuditRecord> _records = [];

    public IReadOnlyList<CapabilityAuditRecord> Records
    {
        get
        {
            lock (_gate)
                return _records.ToList();
        }
    }

    public void Record(CapabilityAuditRecord record)
    {
        lock (_gate)
            _records.Add(record);
    }
}
