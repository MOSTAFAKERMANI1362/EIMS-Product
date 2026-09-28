namespace EIMS.PilotAssembly.Core;

public sealed class ObservationApplicationService
{
    private readonly ObservationAuthorizationService _authorization;
    private readonly ObservationSubmissionService _domain;
    private readonly IObservationTransaction _transaction;

    public ObservationApplicationService(
        ObservationAuthorizationService? authorization = null,
        ObservationSubmissionService? domain = null,
        IObservationTransaction? transaction = null)
    {
        _authorization = authorization ?? new ObservationAuthorizationService();
        _domain = domain ?? new ObservationSubmissionService();
        _transaction = transaction ?? new ObservationTransaction();
    }

    public Observation SubmitObservation(
        Observation observation,
        ObservationSecurityContext securityContext,
        string requiredScope,
        string? clientRole = null,
        string? clientCapability = null,
        long? expectedVersion = null)
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

        return _transaction.Execute(() =>
        {
            if (expectedVersion.HasValue && observation.Version != expectedVersion.Value)
            {
                throw new ObservationDomainException(
                    "EIMS_CONCURRENCY_CONFLICT",
                    "The observation version does not match expectedVersion.");
            }

            return _domain.SubmitObservationWithG01Assignment(observation);
        });
    }
}
