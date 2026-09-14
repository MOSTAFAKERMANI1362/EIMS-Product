using System.Text.Json;

const string defaultPath = "recovery/p4/P4_HR_ORG_OFFLINE_IMPORT_CONTRACT_v1.0.json";
var path = args.Length > 0 ? args[0] : defaultPath;
if (!File.Exists(path))
{
    Console.Error.WriteLine($"FAIL P4R-00 contract not found: {path}");
    return 2;
}

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
var root = doc.RootElement;
var pass = new List<string>();
var fail = new List<string>();

void Check(string id, bool ok, string message) =>
    (ok ? pass : fail).Add($"{(ok ? "PASS" : "FAIL")} {id} {message}");
string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) ? p.GetString() ?? string.Empty : string.Empty;
bool B(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
bool F(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.False;

Check("P4R-01", S(root, "schema") == "EIMS-P4-HR-ORG-OFFLINE-IMPORT-1.0", "P4 contract identity");
Check("P4R-02", S(root, "sourceSystem") == "ORACLE_HR_OFFLINE_EXPORT" && F(root, "liveOracleDependency"), "Pilot import is offline from Oracle HR");

var canonical = root.GetProperty("canonicalExchange");
Check("P4R-03", S(canonical, "format") == "UTF-8 CSV" && F(canonical, "excelDirectIngestion"), "canonical exchange is deterministic CSV rather than direct spreadsheet trust");
Check("P4R-04", S(canonical, "importMode") == "FULL_SNAPSHOT" && S(canonical, "schemaVersion") == "EIMS-HR-ORG-OFFLINE-1.0", "Pilot schema and import mode are pinned");

var columns = root.GetProperty("columns").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
Check("P4R-05", columns.Length == 9 && columns.Contains("PersonId") && columns.Contains("NetworkAccount") && columns.Contains("OrgUnitCode"), "minimal canonical identity/org columns are present");
Check("P4R-06", !columns.Any(x => new[] { "NationalId", "Salary", "DateOfBirth", "PersonalPhone", "BankAccount" }.Contains(x, StringComparer.OrdinalIgnoreCase)), "sensitive HR fields are excluded from canonical schema");

var statuses = root.GetProperty("canonicalEmploymentStatus").EnumerateArray().Select(x => x.GetString()).ToHashSet();
Check("P4R-07", statuses.SetEquals(new[] { "ACTIVE", "INACTIVE" }), "canonical employment status is intentionally narrow");

var privacy = root.GetProperty("privacy");
Check("P4R-08", B(privacy, "dataMinimizationRequired") && F(privacy, "realPersonnelDataAllowedInRepository") && F(privacy, "validationOutputMayEchoRawRowValues"), "privacy and non-sensitive diagnostics are enforced");
Check("P4R-09", privacy.GetProperty("prohibitedFields").GetArrayLength() >= 7, "unneeded personal fields are explicitly prohibited");

var identity = root.GetProperty("identityIntegrity");
Check("P4R-10", B(identity, "uniquePersonId") && B(identity, "uniqueEmployeeNumber") && B(identity, "uniqueNetworkAccountCaseInsensitive"), "person, employee and Windows identities are unique");
Check("P4R-11", S(identity, "networkAccountRebindDifferentPerson") == "BLOCK" && S(identity, "employeeNumberRebindDifferentPerson") == "BLOCK", "identity rebind collisions fail closed");

var org = root.GetProperty("organizationIntegrity");
Check("P4R-12", F(org, "managerSelfReferenceAllowed") && F(org, "managerGraphCyclesAllowed") && B(org, "managerMustResolveInPilotFullSnapshot"), "manager graph integrity is enforced");
Check("P4R-13", F(org, "orgUnitCodeConflictingNamesAllowed"), "OrgUnitCode mapping is consistent within a batch");

var lifecycle = root.GetProperty("lifecycle");
Check("P4R-14", F(lifecycle, "uploadDirectlyMutatesDirectory") && B(lifecycle, "approvalRequiredBeforeActivation"), "import is two-phase and approval gated");
Check("P4R-15", F(lifecycle, "absenceFromSnapshotMeansDeactivation") && B(lifecycle, "deactivationRequiresExplicitInactiveStatus") && B(lifecycle, "reactivationRequiresExplicitActiveStatus"), "absence never becomes an implicit employment decision");
Check("P4R-16", F(lifecycle, "hardDeletePersonAllowed"), "historical Person references cannot be hard-deleted");

var rbac = root.GetProperty("rbacBoundary");
Check("P4R-17", F(rbac, "hrImportMayGrantRole") && F(rbac, "hrImportMayGrantScope") && F(rbac, "jobTitleMayAutomaticallyGrantRole") && F(rbac, "orgUnitMayAutomaticallyGrantRole"), "P4 cannot grant EIMS authority");
Check("P4R-18", S(rbac, "roleAssignmentAuthority") == "P3_WINDOWS_IDENTITY_RBAC", "P3 remains the role/scope authority");

var evidence = root.GetProperty("evidence");
Check("P4R-19", B(evidence, "fileSha256Required") && B(evidence, "batchIdRequired") && B(evidence, "schemaVersionRequired") && B(evidence, "sourceSystemRequired"), "import evidence is traceable");

var activation = root.GetProperty("activationPersistence");
Check("P4R-20", S(activation, "status") == "TBD_AFTER_P2_ORACLE_PHYSICAL_BINDING" && B(activation, "atomicBatchActivationRequired"), "activation remains blocked until P2 Oracle physical binding");

var impl = root.GetProperty("implementation");
Check("P4R-21", S(impl, "sdk") == "10.0.302" && impl.GetProperty("minimumContractTests").GetInt32() >= 26, "P4 executable evidence gate is pinned");

foreach (var line in pass) Console.WriteLine(line);
foreach (var line in fail) Console.Error.WriteLine(line);
Console.WriteLine($"RESULT {pass.Count}/{pass.Count + fail.Count} PASS");
return fail.Count == 0 ? 0 : 1;
