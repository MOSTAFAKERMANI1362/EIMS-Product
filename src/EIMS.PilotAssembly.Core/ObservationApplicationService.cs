namespace EIMS.PilotAssembly.Core;

public sealed class ObservationApplicationService
{
    private readonly ObservationAuthorizationService _authorization;
    private readonly ObservationSubmissionService _domain;

    public ObservationApplicationService(
        ObservationAuthorizationService? authorization = null,
        ObservationSubmissionService? domain = null)
    {
        _authorization = authorization ?? new ObservationAuthorizationService();
        _domain = domain ?? new ObservationSubmissionService();
    }

    public Observation SubmitObservation(
        Observation observation,
        ObservationSecurityContext securityContext,
        string requiredScope,
        string? clientRole = null,
        string? clientCapability = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(securityContext);

        _authorization.AuthorizeSubmit(
            securityContext,
            requiredScope,
            clientRole,
            clientCapability);

        return _domain.SubmitObservation(observation);
    }

    public ObservationSubmissionWithG01Result SubmitObservationWithG01Assignment(
        Observation observation,
        ObservationSecurityContext securityContext,
        string requiredScope,
        string? clientRole = null,
        string? clientCapability = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(securityContext);

        _authorization.AuthorizeSubmit(
            securityContext,
            requiredScope,
            clientRole,
            clientCapability);

        return _domain.SubmitObservationWithG01Assignment(observation);
    }
}
