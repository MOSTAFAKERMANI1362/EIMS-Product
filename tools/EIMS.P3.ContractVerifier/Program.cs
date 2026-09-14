using System.Text.Json;

const string defaultPath = "recovery/p3/P3_WINDOWS_IDENTITY_RBAC_RECOVERY_CONTRACT_v1.0.json";
var path = args.Length > 0 ? args[0] : defaultPath;
if (!File.Exists(path))
{
    Console.Error.WriteLine($"FAIL P3R-00 contract not found: {path}");
    return 2;
}

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
var root = doc.RootElement;
var pass = new List<string>();
var fail = new List<string>();
void Check(string id, bool ok, string message) => (ok ? pass : fail).Add($"{(ok ? "PASS" : "FAIL")} {id} {message}");
string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) ? p.GetString() ?? string.Empty : string.Empty;
bool B(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
bool F(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.False;

Check("P3R-01", S(root, "schema") == "EIMS-P3-WINDOWS-IDENTITY-RBAC-RECOVERY-1.0", "P3 recovery contract identity");
Check("P3R-02", S(root, "recoveryStatus") == "LOGICAL_RUNTIME_RECOVERED_LIVE_DOMAIN_EVIDENCE_PENDING", "live Domain evidence is not fabricated");
Check("P3R-03", root.GetProperty("sourceProduct").GetProperty("sha256").GetString() == "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a", "frozen product SHA bound");

var chain = root.GetProperty("identityChain").EnumerateArray().Select(x => x.GetString()).ToArray();
Check("P3R-04", chain.SequenceEqual(new[] { "AuthenticatedWindowsPrincipal", "NormalizedNetworkAccount", "UniqueActivePersonId", "ExactEffectiveAssignment", "ServerLoadedRoleScope", "AuthorityActor" }), "identity-to-authority chain is explicit");

var hosting = root.GetProperty("hostingContract");
Check("P3R-05", S(hosting, "mode") == "IIS_WINDOWS_AUTH" && F(hosting, "anonymousAllowed") && F(hosting, "clientIdentityHeadersTrusted"), "IIS Windows Authentication boundary is fail closed");
Check("P3R-06", B(hosting, "liveDomainEvidenceRequired"), "live Domain evidence remains mandatory");

var identity = root.GetProperty("identityRules");
Check("P3R-07", S(identity, "networkAccountFormat") == "DOMAIN\\user" && B(identity, "networkAccountCaseInsensitive"), "Windows account normalization contract pinned");
Check("P3R-08", B(identity, "exactlyOnePersonRequired") && B(identity, "inactivePersonDenied") && F(identity, "clientSuppliedPersonIdTrusted"), "Person resolution is unique, active and server-side");

var assignment = root.GetProperty("assignmentRules");
Check("P3R-09", B(assignment, "assignmentIdRequiredForMutationContext") && B(assignment, "assignmentMustBelongToResolvedPerson"), "exact assignment ownership is required");
Check("P3R-10", B(assignment, "revokedAssignmentDenied") && B(assignment, "notYetEffectiveAssignmentDenied") && B(assignment, "expiredAssignmentDenied"), "assignment lifecycle is enforced");
Check("P3R-11", B(assignment, "roleMustBeServerLoaded") && B(assignment, "scopesMustBeServerLoaded") && B(assignment, "requestedScopeMustBeAssigned"), "Role/Scope are server-authoritative");
Check("P3R-12", B(assignment, "authorityActorUsesExactAssignmentOnly") && F(assignment, "unionAllUserRolesAcrossAssignments") && F(assignment, "unionAllUserScopesAcrossAssignments"), "least privilege blocks cross-assignment authority union");

var headers = root.GetProperty("headerTrust");
Check("P3R-13", new[] { "X-EIMS-Pilot-User", "X-User", "X-Role", "X-Scope" }.All(x => F(headers, x)), "client identity/authority headers are untrusted");

var p4 = root.GetProperty("p4Boundary");
Check("P3R-14", B(p4, "personDirectoryMayBePopulatedFromApprovedP4Activation") && B(p4, "networkAccountMayBePopulatedFromApprovedP4Activation"), "approved P4 data may populate identity directory");
Check("P3R-15", F(p4, "p4MayCreateRoleAssignment") && F(p4, "p4MayGrantScope"), "P4 cannot create authority");

var output = root.GetProperty("authorityOutput");
Check("P3R-16", S(output, "type") == "EIMS.Authority.Recovery.AuthorityActor" && S(output, "identitySource") == "WINDOWS_PRINCIPAL", "P3 output composes with P1 AuthorityActor");

var admin = root.GetProperty("roleAssignmentAdministration");
Check("P3R-17", B(admin, "separateControlledBoundaryRequired") && B(admin, "auditRequired") && F(admin, "automaticFromJobTitle") && F(admin, "automaticFromOrgUnit"), "Role Assignment administration is separate and audited");

var readiness = root.GetProperty("readiness");
Check("P3R-18", S(readiness, "logicalResolverStatus") == "READY_FOR_COMPOSITION", "logical resolver is composition-ready");
Check("P3R-19", S(readiness, "p3.windows.live") == "TEST_REQUIRED", "live Windows gate remains open");

var impl = root.GetProperty("implementation");
Check("P3R-20", S(impl, "sdk") == "10.0.302" && impl.GetProperty("minimumContractTests").GetInt32() >= 25, "P3 executable evidence gate is pinned");

foreach (var line in pass) Console.WriteLine(line);
foreach (var line in fail) Console.Error.WriteLine(line);
Console.WriteLine($"RESULT {pass.Count}/{pass.Count + fail.Count} PASS");
return fail.Count == 0 ? 0 : 1;
