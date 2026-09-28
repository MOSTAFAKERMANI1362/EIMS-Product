namespace EIMS.PilotAssembly.Core;

public sealed record ObservationAuditRecord
{
    public string Id { get; }
    public string ActorPrincipalId { get; }
    public string CommandName { get; }
    public string EntityId { get; }
    public string Outcome { get; }
    public long ResultingVersion { get; }

    public ObservationAuditRecord(
        string id,
        string actorPrincipalId,
        string commandName,
        string entityId,
        string outcome,
        long resultingVersion)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Audit identifier is required.", nameof(id));

        if (string.IsNullOrWhiteSpace(actorPrincipalId))
            throw new ArgumentException("Actor principal identifier is required.", nameof(actorPrincipalId));

        if (string.IsNullOrWhiteSpace(commandName))
            throw new ArgumentException("Command name is required.", nameof(commandName));

        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("Entity identifier is required.", nameof(entityId));

        if (string.IsNullOrWhiteSpace(outcome))
            throw new ArgumentException("Audit outcome is required.", nameof(outcome));

        if (resultingVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(resultingVersion));

        Id = id;
        ActorPrincipalId = actorPrincipalId;
        CommandName = commandName;
        EntityId = entityId;
        Outcome = outcome;
        ResultingVersion = resultingVersion;
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
