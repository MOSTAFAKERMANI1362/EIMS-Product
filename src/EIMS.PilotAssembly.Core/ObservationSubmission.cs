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

    public ObservationSubmissionWithG01Result SubmitObservationWithG01Assignment(
        Observation observation,
        G01WorkAssignment? existingAssignment = null)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Status == ObservationStatus.SubmittedForG01)
        {
            var assignment = existingAssignment ?? CreateG01Assignment(observation);
            return new ObservationSubmissionWithG01Result(observation, assignment);
        }

        var submitted = SubmitObservation(observation);
        return new ObservationSubmissionWithG01Result(
            submitted,
            existingAssignment ?? CreateG01Assignment(submitted));
    }

    public G01WorkAssignment AcceptG01Assignment(G01WorkAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.Status != G01WorkAssignmentStatus.Pending)
        {
            throw new ObservationDomainException(
                "EIMS_INVALID_TRANSITION",
                "Only a PENDING G01 assignment can be accepted.");
        }

        return assignment with { Status = G01WorkAssignmentStatus.Accepted };
    }

    public G01WorkAssignment StartG01Assignment(G01WorkAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.Status != G01WorkAssignmentStatus.Accepted)
        {
            throw new ObservationDomainException(
                "EIMS_INVALID_TRANSITION",
                "Only an ACCEPTED G01 assignment can be started.");
        }

        return assignment with { Status = G01WorkAssignmentStatus.InProgress };
    }

    private static G01WorkAssignment CreateG01Assignment(Observation observation) =>
        new(
            $"G01-{observation.Id}",
            observation.Id,
            "INTAKE_STEWARD",
            G01WorkAssignmentStatus.Pending);
}
