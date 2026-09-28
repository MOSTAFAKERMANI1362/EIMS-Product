namespace EIMS.PilotAssembly.Core;

public interface IObservationRepository
{
    Observation? GetById(string observationId);

    void Save(Observation observation);

    bool SaveIfVersion(Observation observation, long expectedVersion);
}

public sealed class InMemoryObservationRepository : IObservationRepository
{
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public Observation? GetById(string observationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);

        lock (_lock)
        {
            return _observations.TryGetValue(observationId, out var observation)
                ? observation
                : null;
        }
    }

    public void Save(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_lock)
        {
            _observations[observation.Id] = observation;
        }
    }

    public bool SaveIfVersion(Observation observation, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_lock)
        {
            if (!_observations.TryGetValue(observation.Id, out var current))
            {
                return false;
            }

            if (current.Version != expectedVersion)
            {
                return false;
            }

            _observations[observation.Id] = observation;
            return true;
        }
    }
}
