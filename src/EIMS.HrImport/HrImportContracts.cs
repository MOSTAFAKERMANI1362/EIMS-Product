namespace EIMS.HrImport;

public enum CanonicalEmploymentStatus
{
    ACTIVE,
    INACTIVE
}

public enum ImportIssueSeverity
{
    Warning,
    Error
}

public enum ReconciliationAction
{
    Create,
    Update,
    StatusChange,
    NoChange,
    MissingFromSnapshotNoChange,
    BlockedIdentityCollision
}

public sealed record HrOrgRecord(
    string PersonId,
    string EmployeeNumber,
    string NetworkAccount,
    string DisplayName,
    string OrgUnitCode,
    string OrgUnitName,
    string? ManagerPersonId,
    CanonicalEmploymentStatus EmploymentStatus,
    DateOnly EffectiveFrom,
    int SourceRowNumber);

public sealed record HrImportBatchMetadata(
    string BatchId,
    string SchemaVersion,
    string SourceSystem,
    string ImportMode,
    DateTimeOffset ExtractedAtUtc,
    string FileSha256);

public sealed record ImportIssue(
    int? RowNumber,
    string Code,
    ImportIssueSeverity Severity,
    string? Field = null);

public sealed record HrImportValidationResult(
    HrImportBatchMetadata Metadata,
    IReadOnlyCollection<HrOrgRecord> Records,
    IReadOnlyCollection<ImportIssue> Issues)
{
    public bool IsValid => Issues.All(x => x.Severity != ImportIssueSeverity.Error);
    public int ErrorCount => Issues.Count(x => x.Severity == ImportIssueSeverity.Error);
    public int WarningCount => Issues.Count(x => x.Severity == ImportIssueSeverity.Warning);
}

public sealed record ReconciliationItem(
    string PersonId,
    ReconciliationAction Action,
    bool RequiresApproval,
    string ReasonCode);

public sealed record HrImportPlan(
    HrImportBatchMetadata Metadata,
    IReadOnlyCollection<ReconciliationItem> Items,
    IReadOnlyCollection<ImportIssue> Issues)
{
    public bool CanActivate =>
        Issues.All(x => x.Severity != ImportIssueSeverity.Error)
        && Items.All(x => x.Action != ReconciliationAction.BlockedIdentityCollision);

    public int CreateCount => Items.Count(x => x.Action == ReconciliationAction.Create);
    public int UpdateCount => Items.Count(x => x.Action == ReconciliationAction.Update);
    public int StatusChangeCount => Items.Count(x => x.Action == ReconciliationAction.StatusChange);
    public int NoChangeCount => Items.Count(x => x.Action == ReconciliationAction.NoChange);
    public int MissingNoChangeCount => Items.Count(x => x.Action == ReconciliationAction.MissingFromSnapshotNoChange);
    public int BlockedCount => Items.Count(x => x.Action == ReconciliationAction.BlockedIdentityCollision);
}

public static class P4CanonicalContract
{
    public const string SchemaVersion = "EIMS-HR-ORG-OFFLINE-1.0";
    public const string ImportMode = "FULL_SNAPSHOT";
    public const string DefaultSourceSystem = "ORACLE_HR_OFFLINE_EXPORT";

    public static readonly string[] Columns =
    {
        "PersonId",
        "EmployeeNumber",
        "NetworkAccount",
        "DisplayName",
        "OrgUnitCode",
        "OrgUnitName",
        "ManagerPersonId",
        "EmploymentStatus",
        "EffectiveFrom"
    };
}
