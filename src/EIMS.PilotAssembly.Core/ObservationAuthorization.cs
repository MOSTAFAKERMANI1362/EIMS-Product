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

        // Client-supplied role/capability values are intentionally ignored.
        _ = clientRole;
        _ = clientCapability;
    }
}
