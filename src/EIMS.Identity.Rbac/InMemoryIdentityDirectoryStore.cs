namespace EIMS.Identity.Rbac;

public sealed class InMemoryIdentityDirectoryStore : IIdentityDirectoryStore
{
    private readonly IReadOnlyCollection<PersonDirectoryEntry> _persons;
    private readonly IReadOnlyDictionary<string, RoleAssignmentEntry> _assignments;

    public InMemoryIdentityDirectoryStore(
        IEnumerable<PersonDirectoryEntry> persons,
        IEnumerable<RoleAssignmentEntry> assignments)
    {
        _persons = Array.AsReadOnly(persons.ToArray());
        _assignments = assignments.ToDictionary(x => x.AssignmentId, StringComparer.OrdinalIgnoreCase);
    }

    public ValueTask<IReadOnlyCollection<PersonDirectoryEntry>> FindPersonsByNetworkAccountAsync(
        string networkAccount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<PersonDirectoryEntry> matches = Array.AsReadOnly(
            _persons
                .Where(x => string.Equals(x.NetworkAccount, networkAccount, StringComparison.OrdinalIgnoreCase))
                .ToArray());
        return ValueTask.FromResult(matches);
    }

    public ValueTask<RoleAssignmentEntry?> FindAssignmentAsync(
        string assignmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_assignments.TryGetValue(assignmentId, out var assignment) ? assignment : null);
    }
}
