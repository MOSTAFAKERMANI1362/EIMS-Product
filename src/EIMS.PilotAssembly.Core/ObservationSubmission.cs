namespace EIMS.PilotAssembly.Core;

public enum ObservationStatus
{
    Draft,
    SubmittedForG01
}

public sealed record Observation(
    string Id,
    string Title,
    ObservationStatus Status,
    long Version);

public sealed class ObservationDomainException : Exception
{
    public string Code { get; }

    public ObservationDomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

public sealed class ObservationSubmissionService
{
    public Observation SubmitObservation(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Status != ObservationStatus.Draft)
        {
            throw new ObservationDomainException(
                "EIMS_INVALID_STATE",
                "Only a DRAFT observation can be submitted for G01.");
        }

        return observation with
        {
            Status = ObservationStatus.SubmittedForG01,
            Version = checked(observation.Version + 1)
        };
    }
}
