namespace EIMS.PilotAssembly.Core;

public enum G01WorkAssignmentStatus
{
    Pending,
    Accepted,
    InProgress,
    Completed,
    Rejected,
    Cancelled,
    Expired,
    Reassigned
}

public sealed record G01WorkAssignment(
    string Id,
    string ObservationId,
    string AssigneeRole,
    G01WorkAssignmentStatus Status,
    string? AssignedPrincipalId = null);

public sealed record ObservationSubmissionWithG01Result(
    Observation Observation,
    G01WorkAssignment Assignment);
