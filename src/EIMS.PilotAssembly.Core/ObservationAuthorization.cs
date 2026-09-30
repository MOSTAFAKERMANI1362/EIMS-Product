namespace EIMS.PilotAssembly.Core;

public sealed record ObservationSecurityContext(
    string PrincipalId,
    IReadOnlyCollection<string> ActiveRoles,
    IReadOnlyCollection<string> EffectiveCapabilities,
    IReadOnlyCollection<string> EffectiveScopes);

public sealed class ObservationAuthorizationException : Exception
{
    public string Code { get; }

    public ObservationAuthorizationException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

public sealed class ObservationAuthorizationService
{
    public bool CanSubmit(ObservationSecurityContext context, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.EffectiveCapabilities.Contains("CREATE", StringComparer.Ordinal)
            && context.EffectiveScopes.Contains(requiredScope, StringComparer.Ordinal);
    }

    public void AuthorizeSubmit(
        ObservationSecurityContext context,
        string requiredScope,
        string? clientRole = null,
        string? clientCapability = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.EffectiveScopes.Contains(requiredScope, StringComparer.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_SCOPE_VIOLATION",
                "The requested scope is not present in the server-derived security context.");
        }

        if (!context.EffectiveCapabilities.Contains("CREATE", StringComparer.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_FORBIDDEN",
                "The authenticated principal does not have the CREATE capability.");
        }

        _ = clientRole;
        _ = clientCapability;
    }
}

public sealed class G01DecisionAuthorizationService
{
    public void Authorize(
        ObservationSecurityContext context,
        Observation observation,
        G01WorkAssignment assignment,
        string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredScope);

        if (string.IsNullOrWhiteSpace(context.PrincipalId))
        {
            throw new ObservationAuthorizationException(
                "EIMS_AUTHENTICATED_PRINCIPAL_REQUIRED",
                "G01 decision authorization requires a server-authenticated principal.");
        }

        if (!context.ActiveRoles.Contains("INTAKE_STEWARD", StringComparer.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_ROLE_REQUIRED",
                "G01 decision authorization requires the INTAKE_STEWARD role.");
        }

        if (!context.EffectiveCapabilities.Contains("G01.DECIDE", StringComparer.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_DECIDE_CAPABILITY_REQUIRED",
                "G01 decision authorization requires G01.DECIDE.");
        }

        if (!context.EffectiveScopes.Contains(requiredScope, StringComparer.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_SCOPE_VIOLATION",
                "The requested G01 scope is not present in the server-derived security context.");
        }

        if (observation.Status != ObservationStatus.SubmittedForG01)
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_BUSINESS_STATE_INELIGIBLE",
                "Only an observation submitted for G01 may receive a G01 decision.");
        }

        if (!string.Equals(assignment.ObservationId, observation.Id, StringComparison.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_ASSIGNMENT_OBSERVATION_MISMATCH",
                "The G01 work assignment is not bound to the target observation.");
        }

        if (!string.Equals(assignment.AssigneeRole, "INTAKE_STEWARD", StringComparison.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_ASSIGNMENT_ROLE_REQUIRED",
                "The G01 work assignment must be assigned to INTAKE_STEWARD.");
        }

        if (assignment.Status != G01WorkAssignmentStatus.InProgress)
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_ASSIGNMENT_NOT_IN_PROGRESS",
                "The G01 work assignment must be IN_PROGRESS.");
        }

        if (string.IsNullOrWhiteSpace(assignment.AssignedPrincipalId))
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_ASSIGNMENT_PRINCIPAL_REQUIRED",
                "The G01 work assignment must have a server-owned assigned principal.");
        }

        if (!string.Equals(
            assignment.AssignedPrincipalId,
            context.PrincipalId,
            StringComparison.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_ASSIGNMENT_PRINCIPAL_MISMATCH",
                "The authenticated server principal does not own the G01 work assignment.");
        }

        if (!string.IsNullOrWhiteSpace(observation.SubmittedByPersonId)
            && string.Equals(
                observation.SubmittedByPersonId,
                context.PrincipalId,
                StringComparison.Ordinal))
        {
            throw new ObservationAuthorizationException(
                "EIMS_G01_SELF_APPROVAL_FORBIDDEN",
                "The observation submitter cannot act as the G01 decision actor.");
        }
    }
}
