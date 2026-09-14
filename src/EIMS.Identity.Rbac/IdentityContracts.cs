using System.Security.Claims;
using EIMS.Authority.Recovery;

namespace EIMS.Identity.Rbac;

public enum DirectoryPersonStatus
{
    Active,
    Inactive
}

public sealed record PersonDirectoryEntry(
    string PersonId,
    string NetworkAccount,
    DirectoryPersonStatus Status);

public sealed record RoleAssignmentEntry(
    string AssignmentId,
    string PersonId,
    string RoleCode,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    bool Revoked);

public sealed record IdentityResolutionRequest(
    string? NetworkIdentity,
    string? AssignmentId,
    string? RequestedScope,
    DateTimeOffset AsOfUtc);

public sealed record IdentityResolutionResult(
    int HttpStatus,
    string Code,
    AuthorityActor? Actor,
    string? Detail = null)
{
    public bool Allowed => HttpStatus is >= 200 and < 300 && Actor is not null;

    public static IdentityResolutionResult Deny(int status, string code, string? detail = null) =>
        new(status, code, null, detail);

    public static IdentityResolutionResult Allow(AuthorityActor actor) =>
        new(200, "P3_IDENTITY_AUTHORITY_RESOLVED", actor);
}

public interface IIdentityDirectoryStore
{
    ValueTask<IReadOnlyCollection<PersonDirectoryEntry>> FindPersonsByNetworkAccountAsync(
        string networkAccount,
        CancellationToken cancellationToken = default);

    ValueTask<RoleAssignmentEntry?> FindAssignmentAsync(
        string assignmentId,
        CancellationToken cancellationToken = default);
}

public static class WindowsPrincipalIdentitySource
{
    public static string? GetAuthenticatedNetworkName(
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? clientHeaders = null)
    {
        var identity = principal.Identity;
        if (identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(identity.Name))
            return null;

        var name = identity.Name.Trim();
        return IsCanonicalWindowsAccount(name) ? name : null;
    }

    public static bool IsCanonicalWindowsAccount(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            return false;

        var slash = value.IndexOf('\\');
        return slash > 0
            && slash == value.LastIndexOf('\\')
            && slash < value.Length - 1;
    }
}

public sealed class WindowsIdentityRbacResolver(IIdentityDirectoryStore store)
{
    public async ValueTask<IdentityResolutionResult> ResolveAsync(
        IdentityResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.NetworkIdentity)
            || !WindowsPrincipalIdentitySource.IsCanonicalWindowsAccount(request.NetworkIdentity.Trim()))
            return IdentityResolutionResult.Deny(401, "P3_WINDOWS_IDENTITY_REQUIRED");

        var networkIdentity = request.NetworkIdentity.Trim();
        var persons = await store.FindPersonsByNetworkAccountAsync(networkIdentity, cancellationToken);

        if (persons.Count == 0)
            return IdentityResolutionResult.Deny(403, "P3_PERSON_NOT_MAPPED");

        if (persons.Count != 1)
            return IdentityResolutionResult.Deny(409, "P3_NETWORK_IDENTITY_NOT_UNIQUE");

        var person = persons.Single();
        if (person.Status != DirectoryPersonStatus.Active)
            return IdentityResolutionResult.Deny(403, "P3_PERSON_INACTIVE");

        if (string.IsNullOrWhiteSpace(request.AssignmentId))
            return IdentityResolutionResult.Deny(400, "P3_ASSIGNMENT_REQUIRED");

        var assignment = await store.FindAssignmentAsync(request.AssignmentId.Trim(), cancellationToken);
        if (assignment is null)
            return IdentityResolutionResult.Deny(403, "P3_ASSIGNMENT_NOT_FOUND");

        if (!string.Equals(assignment.PersonId, person.PersonId, StringComparison.OrdinalIgnoreCase))
            return IdentityResolutionResult.Deny(403, "P3_ASSIGNMENT_PERSON_MISMATCH");

        if (assignment.Revoked)
            return IdentityResolutionResult.Deny(403, "P3_ASSIGNMENT_REVOKED");

        if (request.AsOfUtc < assignment.EffectiveFromUtc)
            return IdentityResolutionResult.Deny(403, "P3_ASSIGNMENT_NOT_YET_EFFECTIVE");

        if (assignment.EffectiveToUtc is not null && request.AsOfUtc >= assignment.EffectiveToUtc.Value)
            return IdentityResolutionResult.Deny(403, "P3_ASSIGNMENT_EXPIRED");

        if (string.IsNullOrWhiteSpace(assignment.RoleCode))
            return IdentityResolutionResult.Deny(403, "P3_ASSIGNMENT_ROLE_MISSING");

        var scopes = assignment.Scopes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (scopes.Length == 0)
            return IdentityResolutionResult.Deny(403, "P3_ASSIGNMENT_SCOPE_MISSING");

        if (!string.IsNullOrWhiteSpace(request.RequestedScope)
            && !scopes.Contains(request.RequestedScope.Trim(), StringComparer.OrdinalIgnoreCase))
            return IdentityResolutionResult.Deny(403, "P3_SCOPE_DENIED");

        var actor = new AuthorityActor(
            person.PersonId,
            networkIdentity,
            "WINDOWS_PRINCIPAL",
            assignment.AssignmentId,
            new[] { assignment.RoleCode.Trim() },
            scopes);

        return IdentityResolutionResult.Allow(actor);
    }
}
