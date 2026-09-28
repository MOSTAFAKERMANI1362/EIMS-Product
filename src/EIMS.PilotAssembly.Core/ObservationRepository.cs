namespace EIMS.PilotAssembly.Core;

public interface IObservationRepository
{
    Observation? GetById(string observationId);

    void Save(Observation observation);

    bool SaveIfVersion(Observation observation, long expectedVersion);

    void Restore(string observationId, Observation? previousObservation);

    bool RestoreIfVersion(string observationId, Observation? previousObservation, long expectedCurrentVersion);
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

    public void Restore(string observationId, Observation? previousObservation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);

        lock (_lock)
        {
            if (previousObservation is null)
            {
                _observations.Remove(observationId);
                return;
            }

            _observations[observationId] = previousObservation;
        }
    }

    public bool RestoreIfVersion(string observationId, Observation? previousObservation, long expectedCurrentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);

        lock (_lock)
        {
            if (!_observations.TryGetValue(observationId, out var current))
            {
                return previousObservation is null && expectedCurrentVersion == 0;
            }

            if (current.Version != expectedCurrentVersion)
            {
                return false;
            }

            if (previousObservation is null)
            {
                _observations.Remove(observationId);
            }
            else
            {
                _observations[observationId] = previousObservation;
            }

            return true;
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
