namespace EIMS.HrImport;

public sealed class HrOrgImportService
{
    public HrImportValidationResult ValidateCanonicalCsv(
        string csv,
        string? batchId = null,
        string sourceSystem = P4CanonicalContract.DefaultSourceSystem,
        DateTimeOffset? extractedAtUtc = null)
    {
        var metadata = new HrImportBatchMetadata(
            batchId ?? $"HRB-{Guid.NewGuid():N}",
            P4CanonicalContract.SchemaVersion,
            sourceSystem,
            P4CanonicalContract.ImportMode,
            extractedAtUtc ?? DateTimeOffset.UtcNow,
            CanonicalCsv.Sha256(csv));

        IReadOnlyCollection<HrOrgRecord> records;
        IReadOnlyCollection<ImportIssue> parseIssues;
        try
        {
            (records, parseIssues) = CanonicalCsv.ParseCanonical(csv);
        }
        catch (FormatException)
        {
            return new HrImportValidationResult(
                metadata,
                Array.Empty<HrOrgRecord>(),
                new[] { new ImportIssue(null, "P4_CSV_MALFORMED", ImportIssueSeverity.Error) });
        }

        var issues = parseIssues.Concat(ValidateRecords(records)).ToArray();
        return new HrImportValidationResult(metadata, records, Array.AsReadOnly(issues));
    }

    public HrImportPlan BuildPlan(
        string incomingCsv,
        string currentDirectoryCsv,
        string? batchId = null,
        string sourceSystem = P4CanonicalContract.DefaultSourceSystem,
        DateTimeOffset? extractedAtUtc = null)
    {
        var incoming = ValidateCanonicalCsv(incomingCsv, batchId, sourceSystem, extractedAtUtc);
        var current = ValidateCanonicalCsv(currentDirectoryCsv, "CURRENT-DIRECTORY", "EIMS_CURRENT_DIRECTORY", extractedAtUtc);

        var issues = new List<ImportIssue>();
        issues.AddRange(incoming.Issues);
        if (!current.IsValid)
            issues.Add(new ImportIssue(null, "P4_CURRENT_DIRECTORY_INVALID", ImportIssueSeverity.Error));

        if (!incoming.IsValid || !current.IsValid)
            return new HrImportPlan(incoming.Metadata, Array.Empty<ReconciliationItem>(), Array.AsReadOnly(issues.ToArray()));

        var incomingByPerson = incoming.Records.ToDictionary(x => x.PersonId, StringComparer.OrdinalIgnoreCase);
        var currentByPerson = current.Records.ToDictionary(x => x.PersonId, StringComparer.OrdinalIgnoreCase);
        var currentByAccount = current.Records.ToDictionary(x => x.NetworkAccount, StringComparer.OrdinalIgnoreCase);
        var currentByEmployee = current.Records.ToDictionary(x => x.EmployeeNumber, StringComparer.OrdinalIgnoreCase);

        var items = new List<ReconciliationItem>();
        foreach (var row in incoming.Records)
        {
            if (currentByAccount.TryGetValue(row.NetworkAccount, out var accountOwner)
                && !string.Equals(accountOwner.PersonId, row.PersonId, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new ReconciliationItem(row.PersonId, ReconciliationAction.BlockedIdentityCollision, true, "NETWORK_ACCOUNT_BOUND_TO_DIFFERENT_PERSON"));
                issues.Add(new ImportIssue(row.SourceRowNumber, "P4_NETWORK_ACCOUNT_IDENTITY_COLLISION", ImportIssueSeverity.Error, "NetworkAccount"));
                continue;
            }

            if (currentByEmployee.TryGetValue(row.EmployeeNumber, out var employeeOwner)
                && !string.Equals(employeeOwner.PersonId, row.PersonId, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new ReconciliationItem(row.PersonId, ReconciliationAction.BlockedIdentityCollision, true, "EMPLOYEE_NUMBER_BOUND_TO_DIFFERENT_PERSON"));
                issues.Add(new ImportIssue(row.SourceRowNumber, "P4_EMPLOYEE_NUMBER_IDENTITY_COLLISION", ImportIssueSeverity.Error, "EmployeeNumber"));
                continue;
            }

            if (!currentByPerson.TryGetValue(row.PersonId, out var existing))
            {
                items.Add(new ReconciliationItem(row.PersonId, ReconciliationAction.Create, true, "NEW_PERSON_EXPLICIT_HR_RECORD"));
                continue;
            }

            if (existing.EmploymentStatus != row.EmploymentStatus)
            {
                items.Add(new ReconciliationItem(row.PersonId, ReconciliationAction.StatusChange, true,
                    row.EmploymentStatus == CanonicalEmploymentStatus.ACTIVE ? "EXPLICIT_REACTIVATION" : "EXPLICIT_DEACTIVATION"));
                continue;
            }

            if (!SameDirectoryAttributes(existing, row))
            {
                items.Add(new ReconciliationItem(row.PersonId, ReconciliationAction.Update, true, "DIRECTORY_ATTRIBUTES_CHANGED"));
                continue;
            }

            items.Add(new ReconciliationItem(row.PersonId, ReconciliationAction.NoChange, false, "UNCHANGED"));
        }

        foreach (var existing in current.Records)
        {
            if (incomingByPerson.ContainsKey(existing.PersonId))
                continue;

            items.Add(new ReconciliationItem(existing.PersonId, ReconciliationAction.MissingFromSnapshotNoChange, false, "ABSENCE_IS_NOT_DEACTIVATION"));
            issues.Add(new ImportIssue(null, "P4_MISSING_FROM_SNAPSHOT_NO_CHANGE", ImportIssueSeverity.Warning));
        }

        return new HrImportPlan(incoming.Metadata, Array.AsReadOnly(items.ToArray()), Array.AsReadOnly(issues.ToArray()));
    }

    public IReadOnlyCollection<ImportIssue> ValidateRecords(IReadOnlyCollection<HrOrgRecord> records)
    {
        var issues = new List<ImportIssue>();
        foreach (var row in records)
        {
            Required(row.PersonId, row.SourceRowNumber, "PersonId", issues);
            Required(row.EmployeeNumber, row.SourceRowNumber, "EmployeeNumber", issues);
            Required(row.NetworkAccount, row.SourceRowNumber, "NetworkAccount", issues);
            Required(row.DisplayName, row.SourceRowNumber, "DisplayName", issues);
            Required(row.OrgUnitCode, row.SourceRowNumber, "OrgUnitCode", issues);
            Required(row.OrgUnitName, row.SourceRowNumber, "OrgUnitName", issues);

            if (!IsCanonicalNetworkAccount(row.NetworkAccount))
                issues.Add(new ImportIssue(row.SourceRowNumber, "P4_INVALID_NETWORK_ACCOUNT", ImportIssueSeverity.Error, "NetworkAccount"));

            if (!string.IsNullOrWhiteSpace(row.ManagerPersonId)
                && string.Equals(row.PersonId, row.ManagerPersonId, StringComparison.OrdinalIgnoreCase))
                issues.Add(new ImportIssue(row.SourceRowNumber, "P4_MANAGER_SELF_REFERENCE", ImportIssueSeverity.Error, "ManagerPersonId"));
        }

        AddDuplicateIssues(records, x => x.PersonId, "P4_DUPLICATE_PERSON_ID", "PersonId", issues, StringComparer.OrdinalIgnoreCase);
        AddDuplicateIssues(records, x => x.EmployeeNumber, "P4_DUPLICATE_EMPLOYEE_NUMBER", "EmployeeNumber", issues, StringComparer.OrdinalIgnoreCase);
        AddDuplicateIssues(records, x => x.NetworkAccount, "P4_DUPLICATE_NETWORK_ACCOUNT", "NetworkAccount", issues, StringComparer.OrdinalIgnoreCase);

        var personIds = new HashSet<string>(records.Select(x => x.PersonId), StringComparer.OrdinalIgnoreCase);
        foreach (var row in records.Where(x => !string.IsNullOrWhiteSpace(x.ManagerPersonId)))
        {
            if (!personIds.Contains(row.ManagerPersonId!))
                issues.Add(new ImportIssue(row.SourceRowNumber, "P4_MANAGER_NOT_IN_FULL_SNAPSHOT", ImportIssueSeverity.Error, "ManagerPersonId"));
        }

        foreach (var group in records.GroupBy(x => x.OrgUnitCode, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(x => x.OrgUnitName).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                foreach (var row in group)
                    issues.Add(new ImportIssue(row.SourceRowNumber, "P4_ORG_UNIT_NAME_CONFLICT", ImportIssueSeverity.Error, "OrgUnitName"));
            }
        }

        if (HasManagerCycle(records))
            issues.Add(new ImportIssue(null, "P4_MANAGER_GRAPH_CYCLE", ImportIssueSeverity.Error, "ManagerPersonId"));

        return Array.AsReadOnly(issues.ToArray());
    }

    private static bool SameDirectoryAttributes(HrOrgRecord a, HrOrgRecord b) =>
        string.Equals(a.EmployeeNumber, b.EmployeeNumber, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.NetworkAccount, b.NetworkAccount, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
        && string.Equals(a.OrgUnitCode, b.OrgUnitCode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.OrgUnitName, b.OrgUnitName, StringComparison.Ordinal)
        && string.Equals(a.ManagerPersonId ?? string.Empty, b.ManagerPersonId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && a.EffectiveFrom == b.EffectiveFrom;

    private static bool IsCanonicalNetworkAccount(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return false;
        var slash = value.IndexOf('\\');
        return slash > 0 && slash == value.LastIndexOf('\\') && slash < value.Length - 1;
    }

    private static void Required(string value, int row, string field, List<ImportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new ImportIssue(row, "P4_REQUIRED_FIELD_MISSING", ImportIssueSeverity.Error, field));
    }

    private static void AddDuplicateIssues(
        IReadOnlyCollection<HrOrgRecord> records,
        Func<HrOrgRecord, string> selector,
        string code,
        string field,
        List<ImportIssue> issues,
        StringComparer comparer)
    {
        foreach (var duplicate in records.GroupBy(selector, comparer).Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Count() > 1))
        {
            foreach (var row in duplicate)
                issues.Add(new ImportIssue(row.SourceRowNumber, code, ImportIssueSeverity.Error, field));
        }
    }

    private static bool HasManagerCycle(IReadOnlyCollection<HrOrgRecord> records)
    {
        var managerByPerson = records
            .Where(x => !string.IsNullOrWhiteSpace(x.ManagerPersonId))
            .ToDictionary(x => x.PersonId, x => x.ManagerPersonId!, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string personId)
        {
            if (state.TryGetValue(personId, out var current))
                return current == 1;

            state[personId] = 1;
            if (managerByPerson.TryGetValue(personId, out var manager) && managerByPerson.ContainsKey(manager) && Visit(manager))
                return true;
            state[personId] = 2;
            return false;
        }

        return records.Any(x => Visit(x.PersonId));
    }
}
