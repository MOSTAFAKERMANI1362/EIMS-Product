namespace EIMS.PilotAssembly.Core;

public sealed record ObservationAuditRecord(
    string Id,
    string ActorPrincipalId,
    string CommandName,
    string EntityId,
    string Outcome,
    long ResultingVersion)
{
    public ObservationAuditRecord
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new ArgumentException("Audit identifier is required.", nameof(Id));

        if (string.IsNullOrWhiteSpace(ActorPrincipalId))
            throw new ArgumentException("Actor principal identifier is required.", nameof(ActorPrincipalId));

        if (string.IsNullOrWhiteSpace(CommandName))
            throw new ArgumentException("Command name is required.", nameof(CommandName));

        if (string.IsNullOrWhiteSpace(EntityId))
            throw new ArgumentException("Entity identifier is required.", nameof(EntityId));

        if (string.IsNullOrWhiteSpace(Outcome))
            throw new ArgumentException("Audit outcome is required.", nameof(Outcome));

        if (ResultingVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(ResultingVersion));
    }
}

public interface IObservationAuditSink
{
    void Append(ObservationAuditRecord record);
}

public sealed class InMemoryObservationAuditSink : IObservationAuditSink
{
    private readonly List<ObservationAuditRecord> _records = new();
    private readonly object _lock = new();

    public void Append(ObservationAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_lock)
        {
            _records.Add(record);
        }
    }

    public IReadOnlyList<ObservationAuditRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return _records.ToArray();
            }
        }
    }
}
