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

    public ObservationSubmissionWithG01Result SubmitObservationWithG01Assignment(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Status == ObservationStatus.SubmittedForG01)
        {
            return new ObservationSubmissionWithG01Result(
                observation,
                CreateG01Assignment(observation));
        }

        var submitted = SubmitObservation(observation);
        return new ObservationSubmissionWithG01Result(
            submitted,
            CreateG01Assignment(submitted));
    }

    private static G01WorkAssignment CreateG01Assignment(Observation observation) =>
        new(
            $"G01-{observation.Id}",
            observation.Id,
            "INTAKE_STEWARD",
            G01WorkAssignmentStatus.Pending);
}
