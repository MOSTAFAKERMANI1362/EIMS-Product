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
    long Version,
    string? SubmittedByPersonId = null);

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
    public Observation SubmitObservation(
        Observation observation,
        string? submittedByPersonId = null)
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
            Version = checked(observation.Version + 1),
            SubmittedByPersonId = string.IsNullOrWhiteSpace(submittedByPersonId)
                ? observation.SubmittedByPersonId
                : submittedByPersonId.Trim()
        };
    }

    public ObservationSubmissionWithG01Result SubmitObservationWithG01Assignment(
        Observation observation,
        G01WorkAssignment? existingAssignment = null,
        string? submittedByPersonId = null)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Status == ObservationStatus.SubmittedForG01)
        {
            var assignment = existingAssignment ?? CreateG01Assignment(observation);
            return new ObservationSubmissionWithG01Result(observation, assignment);
        }

        var submitted = SubmitObservation(observation, submittedByPersonId);
        return new ObservationSubmissionWithG01Result(
            submitted,
            existingAssignment ?? CreateG01Assignment(submitted));
    }

    public G01WorkAssignment AcceptG01Assignment(G01WorkAssignment assignment) =>
        AcceptG01Assignment(assignment, null);

    public G01WorkAssignment AcceptG01Assignment(
        G01WorkAssignment assignment,
        string? acceptingPrincipalId)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.Status != G01WorkAssignmentStatus.Pending)
        {
            throw new ObservationDomainException(
                "EIMS_INVALID_TRANSITION",
                "Only a PENDING G01 assignment can be accepted.");
        }

        var principal = string.IsNullOrWhiteSpace(acceptingPrincipalId)
            ? assignment.AssignedPrincipalId
            : acceptingPrincipalId.Trim();

        return assignment with
        {
            Status = G01WorkAssignmentStatus.Accepted,
            AssignedPrincipalId = principal
        };
    }

    public G01WorkAssignment StartG01Assignment(G01WorkAssignment assignment) =>
        StartG01Assignment(assignment, assignment?.AssignedPrincipalId);

    public G01WorkAssignment StartG01Assignment(
        G01WorkAssignment assignment,
        string? startingPrincipalId)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.Status != G01WorkAssignmentStatus.Accepted)
        {
            throw new ObservationDomainException(
                "EIMS_INVALID_TRANSITION",
                "Only an ACCEPTED G01 assignment can be started.");
        }

        if (string.IsNullOrWhiteSpace(startingPrincipalId))
        {
            throw new ObservationDomainException(
                "EIMS_G01_ASSIGNMENT_PRINCIPAL_REQUIRED",
                "A server-bound assignment principal is required to start the assignment.");
        }

        if (!string.Equals(
            assignment.AssignedPrincipalId,
            startingPrincipalId.Trim(),
            StringComparison.Ordinal))
        {
            throw new ObservationDomainException(
                "EIMS_G01_ASSIGNMENT_PRINCIPAL_MISMATCH",
                "Only the server-bound assignment principal can start the assignment.");
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
