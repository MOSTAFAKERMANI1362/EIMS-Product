namespace EIMS.PilotAssembly.Host;

public sealed class SubmitObservationRequestDto
{
}

public sealed record SubmitObservationResponseDto(
    string ObservationId,
    string Status,
    long Version,
    string G01AssignmentId,
    string G01AssignmentStatus);

public static class ObservationApiContract
{
    public const string SubmissionRoute =
        "/api/v1/observations/{observationId}/submission";

    public const string ExpectedVersionHeader = "If-Match";

    public const string IdempotencyHeader = "Idempotency-Key";
}
