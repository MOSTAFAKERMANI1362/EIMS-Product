namespace EIMS.PilotAssembly.Core;

public sealed class ObservationApplicationService
{
    private readonly ObservationAuthorizationService _authorization;
    private readonly ObservationSubmissionService _domain;
    private readonly IObservationTransaction _transaction;
    private readonly Dictionary<string, IdempotencyRecord> _idempotencyRecords = new(StringComparer.Ordinal);
    private readonly object _idempotencyLock = new();

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
        string? clientCapability = null,
        long? expectedVersion = null,
        string? idempotencyKey = null)
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
            var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
            var fingerprint = key is null ? null : CreateSemanticFingerprint(observation, securityContext, requiredScope);

            if (key is not null)
            {
                lock (_idempotencyLock)
                {
                    if (_idempotencyRecords.TryGetValue(key, out var existing))
                    {
                        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                        {
                            throw new ObservationDomainException(
                                "EIMS_IDEMPOTENCY_CONFLICT",
                                "The idempotency key was already used for a different semantic request.");
                        }

                        return existing.Result;
                    }

                    var result = ExecuteSubmitToG01(observation, expectedVersion);
                    _idempotencyRecords[key] = new IdempotencyRecord(fingerprint!, result);
                    return result;
                }
            }

            return ExecuteSubmitToG01(observation, expectedVersion);
        });
    }

    private ObservationSubmissionWithG01Result ExecuteSubmitToG01(
        Observation observation,
        long? expectedVersion)
    {
        if (expectedVersion.HasValue && observation.Version != expectedVersion.Value)
        {
            throw new ObservationDomainException(
                "EIMS_CONCURRENCY_CONFLICT",
                "The observation version does not match expectedVersion.");
        }

        return _domain.SubmitObservationWithG01Assignment(observation);
    }

    private static string CreateSemanticFingerprint(
        Observation observation,
        ObservationSecurityContext securityContext,
        string requiredScope)
    {
        return string.Join("|",
            observation.Id,
            observation.Title,
            observation.Status,
            observation.Version,
            securityContext.PrincipalId,
            requiredScope);
    }

    private sealed record IdempotencyRecord(
        string Fingerprint,
        ObservationSubmissionWithG01Result Result);
}
