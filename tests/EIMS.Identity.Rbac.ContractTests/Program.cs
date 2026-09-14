using System.Security.Claims;
using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;

var now = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
var tests = new List<(string Name, Func<Task> Run)>
{
    ("P3-CT-01 unauthenticated principal ignores forged identity headers", UnauthenticatedHeaderSpoofDenied),
    ("P3-CT-02 authenticated principal wins over forged identity header", AuthenticatedPrincipalWins),
    ("P3-CT-03 noncanonical Windows principal name is rejected", NonCanonicalPrincipalRejected),
    ("P3-CT-04 network account lookup is case insensitive", CaseInsensitiveAccount),
    ("P3-CT-05 unmapped network account denied", PersonNotMapped),
    ("P3-CT-06 duplicate network mapping denied", DuplicateNetworkMapping),
    ("P3-CT-07 inactive person denied", InactivePerson),
    ("P3-CT-08 assignment id is mandatory", AssignmentRequired),
    ("P3-CT-09 unknown assignment denied", AssignmentNotFound),
    ("P3-CT-10 assignment belonging to another person denied", AssignmentPersonMismatch),
    ("P3-CT-11 revoked assignment denied", AssignmentRevoked),
    ("P3-CT-12 assignment before effective date denied", AssignmentNotYetEffective),
    ("P3-CT-13 assignment at expiry boundary denied", AssignmentExpired),
    ("P3-CT-14 missing role fails closed", MissingRole),
    ("P3-CT-15 missing scope fails closed", MissingScope),
    ("P3-CT-16 requested scope outside assignment denied", RequestedScopeDenied),
    ("P3-CT-17 successful resolution creates server authority actor", SuccessfulResolution),
    ("P3-CT-18 exact assignment prevents union of user roles", ExactAssignmentLeastPrivilege),
    ("P3-CT-19 exact assignment prevents union of user scopes", ExactAssignmentScopeIsolation),
    ("P3-CT-20 resolver ignores client supplied role and scope headers", ForgedRoleScopeHeadersIgnored),
    ("P3-CT-21 cancellation causes no resolution", CancellationHonored),
    ("P3-CT-22 P4 person identity alone does not constitute authorization", PersonWithoutAssignmentDenied),
    ("P3-CT-23 resolved actor satisfies P1 identity and assignment shape", P1ActorShape),
    ("P3-CT-24 Windows account canonical validation rejects whitespace and multiple slashes", CanonicalAccountValidation),
    ("P3-CT-25 assignment effective-to null remains active", OpenEndedAssignment)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

ClaimsPrincipal Principal(string name, bool authenticated = true)
{
    var identity = authenticated
        ? new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, "Negotiate")
        : new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) });
    return new ClaimsPrincipal(identity);
}

PersonDirectoryEntry Person(
    string personId = "P-001",
    string account = "DOMAIN\\user1",
    DirectoryPersonStatus status = DirectoryPersonStatus.Active) =>
    new(personId, account, status);

RoleAssignmentEntry Assignment(
    string assignmentId = "ASG-001",
    string personId = "P-001",
    string role = "IDEA_OWNER",
    string[]? scopes = null,
    DateTimeOffset? from = null,
    DateTimeOffset? to = null,
    bool revoked = false) =>
    new(
        assignmentId,
        personId,
        role,
        scopes ?? new[] { "UNIT:RND" },
        from ?? now.AddDays(-1),
        to,
        revoked);

WindowsIdentityRbacResolver Resolver(
    IEnumerable<PersonDirectoryEntry>? persons = null,
    IEnumerable<RoleAssignmentEntry>? assignments = null) =>
    new(new InMemoryIdentityDirectoryStore(
        persons ?? new[] { Person() },
        assignments ?? new[] { Assignment() }));

IdentityResolutionRequest Request(
    string? network = "DOMAIN\\user1",
    string? assignment = "ASG-001",
    string? scope = "UNIT:RND",
    DateTimeOffset? at = null) =>
    new(network, assignment, scope, at ?? now);

Task UnauthenticatedHeaderSpoofDenied()
{
    var headers = new Dictionary<string, string>
    {
        ["X-EIMS-Pilot-User"] = "DOMAIN\\admin",
        ["X-User"] = "DOMAIN\\admin"
    };
    var name = WindowsPrincipalIdentitySource.GetAuthenticatedNetworkName(Principal("DOMAIN\\user1", authenticated: false), headers);
    Null(name);
    return Task.CompletedTask;
}

Task AuthenticatedPrincipalWins()
{
    var headers = new Dictionary<string, string> { ["X-EIMS-Pilot-User"] = "DOMAIN\\admin" };
    var name = WindowsPrincipalIdentitySource.GetAuthenticatedNetworkName(Principal("DOMAIN\\user1"), headers);
    Eq("DOMAIN\\user1", name!);
    return Task.CompletedTask;
}

Task NonCanonicalPrincipalRejected()
{
    Null(WindowsPrincipalIdentitySource.GetAuthenticatedNetworkName(Principal("user1@example.com")));
    return Task.CompletedTask;
}

async Task CaseInsensitiveAccount()
{
    var result = await Resolver().ResolveAsync(Request(network: "domain\\USER1"));
    True(result.Allowed); Eq("P-001", result.Actor!.PersonId);
}

async Task PersonNotMapped()
{
    var result = await Resolver(Array.Empty<PersonDirectoryEntry>(), Array.Empty<RoleAssignmentEntry>()).ResolveAsync(Request());
    Denied(result, 403, "P3_PERSON_NOT_MAPPED");
}

async Task DuplicateNetworkMapping()
{
    var persons = new[]
    {
        Person("P-001", "DOMAIN\\user1"),
        Person("P-002", "domain\\USER1")
    };
    var result = await Resolver(persons, new[] { Assignment() }).ResolveAsync(Request());
    Denied(result, 409, "P3_NETWORK_IDENTITY_NOT_UNIQUE");
}

async Task InactivePerson()
{
    var result = await Resolver(new[] { Person(status: DirectoryPersonStatus.Inactive) }, new[] { Assignment() }).ResolveAsync(Request());
    Denied(result, 403, "P3_PERSON_INACTIVE");
}

async Task AssignmentRequired()
{
    var result = await Resolver().ResolveAsync(Request(assignment: null));
    Denied(result, 400, "P3_ASSIGNMENT_REQUIRED");
}

async Task AssignmentNotFound()
{
    var result = await Resolver(new[] { Person() }, Array.Empty<RoleAssignmentEntry>()).ResolveAsync(Request());
    Denied(result, 403, "P3_ASSIGNMENT_NOT_FOUND");
}

async Task AssignmentPersonMismatch()
{
    var result = await Resolver(new[] { Person() }, new[] { Assignment(personId: "P-OTHER") }).ResolveAsync(Request());
    Denied(result, 403, "P3_ASSIGNMENT_PERSON_MISMATCH");
}

async Task AssignmentRevoked()
{
    var result = await Resolver(assignments: new[] { Assignment(revoked: true) }).ResolveAsync(Request());
    Denied(result, 403, "P3_ASSIGNMENT_REVOKED");
}

async Task AssignmentNotYetEffective()
{
    var result = await Resolver(assignments: new[] { Assignment(from: now.AddMinutes(1)) }).ResolveAsync(Request());
    Denied(result, 403, "P3_ASSIGNMENT_NOT_YET_EFFECTIVE");
}

async Task AssignmentExpired()
{
    var result = await Resolver(assignments: new[] { Assignment(to: now) }).ResolveAsync(Request());
    Denied(result, 403, "P3_ASSIGNMENT_EXPIRED");
}

async Task MissingRole()
{
    var result = await Resolver(assignments: new[] { Assignment(role: " ") }).ResolveAsync(Request());
    Denied(result, 403, "P3_ASSIGNMENT_ROLE_MISSING");
}

async Task MissingScope()
{
    var result = await Resolver(assignments: new[] { Assignment(scopes: Array.Empty<string>()) }).ResolveAsync(Request(scope: null));
    Denied(result, 403, "P3_ASSIGNMENT_SCOPE_MISSING");
}

async Task RequestedScopeDenied()
{
    var result = await Resolver(assignments: new[] { Assignment(scopes: new[] { "UNIT:RND" }) }).ResolveAsync(Request(scope: "UNIT:FIN"));
    Denied(result, 403, "P3_SCOPE_DENIED");
}

async Task SuccessfulResolution()
{
    var result = await Resolver().ResolveAsync(Request());
    True(result.Allowed); Eq(200, result.HttpStatus); Eq("P3_IDENTITY_AUTHORITY_RESOLVED", result.Code);
    var actor = result.Actor!;
    Eq("P-001", actor.PersonId);
    Eq("DOMAIN\\user1", actor.NetworkIdentity);
    Eq("WINDOWS_PRINCIPAL", actor.IdentitySource);
    Eq("ASG-001", actor.AssignmentId);
    True(actor.Roles.SequenceEqual(new[] { "IDEA_OWNER" }));
    True(actor.Scopes.SequenceEqual(new[] { "UNIT:RND" }));
}

async Task ExactAssignmentLeastPrivilege()
{
    var assignments = new[]
    {
        Assignment("ASG-IDEA", role: "IDEA_OWNER", scopes: new[] { "UNIT:RND" }),
        Assignment("ASG-ADMIN", role: "ADMIN", scopes: new[] { "GLOBAL" })
    };
    var result = await Resolver(assignments: assignments).ResolveAsync(Request(assignment: "ASG-IDEA", scope: "UNIT:RND"));
    True(result.Allowed);
    True(result.Actor!.Roles.SequenceEqual(new[] { "IDEA_OWNER" }));
    False(result.Actor.Roles.Contains("ADMIN"));
}

async Task ExactAssignmentScopeIsolation()
{
    var assignments = new[]
    {
        Assignment("ASG-RND", role: "IDEA_OWNER", scopes: new[] { "UNIT:RND" }),
        Assignment("ASG-FIN", role: "FINANCIAL_ASSESSOR", scopes: new[] { "UNIT:FIN" })
    };
    var denied = await Resolver(assignments: assignments).ResolveAsync(Request(assignment: "ASG-RND", scope: "UNIT:FIN"));
    Denied(denied, 403, "P3_SCOPE_DENIED");
}

async Task ForgedRoleScopeHeadersIgnored()
{
    var headers = new Dictionary<string, string>
    {
        ["X-Role"] = "ADMIN",
        ["X-Scope"] = "GLOBAL",
        ["X-EIMS-Pilot-User"] = "DOMAIN\\admin"
    };
    var network = WindowsPrincipalIdentitySource.GetAuthenticatedNetworkName(Principal("DOMAIN\\user1"), headers);
    Eq("DOMAIN\\user1", network!);
    var result = await Resolver().ResolveAsync(Request(network));
    True(result.Allowed);
    False(result.Actor!.Roles.Contains("ADMIN"));
    False(result.Actor.Scopes.Contains("GLOBAL"));
}

async Task CancellationHonored()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var cancelled = false;
    try
    {
        await Resolver().ResolveAsync(Request(), cts.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    True(cancelled);
}

async Task PersonWithoutAssignmentDenied()
{
    var result = await Resolver(new[] { Person() }, Array.Empty<RoleAssignmentEntry>()).ResolveAsync(Request());
    Denied(result, 403, "P3_ASSIGNMENT_NOT_FOUND");
}

async Task P1ActorShape()
{
    var result = await Resolver().ResolveAsync(Request());
    True(result.Allowed);
    var actor = result.Actor!;
    False(string.IsNullOrWhiteSpace(actor.PersonId));
    False(string.IsNullOrWhiteSpace(actor.NetworkIdentity));
    False(string.IsNullOrWhiteSpace(actor.IdentitySource));
    False(string.IsNullOrWhiteSpace(actor.AssignmentId));
    True(actor.Roles.Count == 1);
    True(actor.Scopes.Count >= 1);
}

Task CanonicalAccountValidation()
{
    True(WindowsPrincipalIdentitySource.IsCanonicalWindowsAccount("DOMAIN\\user"));
    False(WindowsPrincipalIdentitySource.IsCanonicalWindowsAccount("DOMAIN\\user\\extra"));
    False(WindowsPrincipalIdentitySource.IsCanonicalWindowsAccount("DOMAIN user"));
    False(WindowsPrincipalIdentitySource.IsCanonicalWindowsAccount("user"));
    return Task.CompletedTask;
}

async Task OpenEndedAssignment()
{
    var result = await Resolver(assignments: new[] { Assignment(to: null) }).ResolveAsync(Request(at: now.AddYears(3)));
    True(result.Allowed);
}

static void Denied(IdentityResolutionResult result, int status, string code)
{
    Eq(status, result.HttpStatus); Eq(code, result.Code); False(result.Allowed); Null(result.Actor);
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Null(object? value) { if (value is not null) throw new InvalidOperationException("Expected null."); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}
