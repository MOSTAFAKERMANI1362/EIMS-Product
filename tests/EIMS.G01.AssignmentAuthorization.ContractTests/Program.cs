using EIMS.PilotAssembly.Core;

var tests = new List<(string Name, Action Run)>
{
    ("G01-ASG-RED-01 server-owned assignment principal is required", AssignmentPrincipalRequired),
    ("G01-ASG-RED-02 only IN_PROGRESS assignment can authorize", InProgressRequired),
    ("G01-ASG-RED-03 different principal cannot use another person's assignment", PrincipalMustMatchAssignment),
    ("G01-ASG-RED-04 assignment must belong to target observation", ObservationMustMatchAssignment),
    ("G01-ASG-RED-05 role alone is insufficient without G01.DECIDE", CapabilityRequired),
    ("G01-ASG-RED-06 submitter cannot be decision actor", SubmitterCannotDecide),
    ("G01-ASG-RED-07 client actor cannot replace server principal", ServerPrincipalOnly),
    ("G01-ASG-RED-08 valid assignment + authority context authorizes decision", ValidAuthorization),
    ("G01-ASG-RED-09 accepting principal becomes server-owned assignment principal", AcceptBindsPrincipal),
    ("G01-ASG-RED-10 different principal cannot start accepted assignment", StartRequiresOwner),
    ("G01-ASG-RED-11 bound principal can start assignment", OwnerCanStart)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
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

static ObservationSecurityContext Context(
    string principal = "P-REVIEWER",
    string[]? roles = null,
    string[]? capabilities = null,
    string[]? scopes = null) =>
    new(
        principal,
        roles ?? new[] { "INTAKE_STEWARD" },
        capabilities ?? new[] { "G01.DECIDE" },
        scopes ?? new[] { "UNIT:RND" });

static Observation Submitted(string submitter = "P-SUBMITTER") =>
    new("OBS-G01-001", "Observation", ObservationStatus.SubmittedForG01, 2, submitter);

static G01WorkAssignment Assignment(
    string observationId = "OBS-G01-001",
    string? principal = "P-REVIEWER",
    G01WorkAssignmentStatus status = G01WorkAssignmentStatus.InProgress) =>
    new("G01-OBS-G01-001", observationId, "INTAKE_STEWARD", status, principal);

static void AssertAuthorized(ObservationSecurityContext context, Observation observation, G01WorkAssignment assignment)
{
    new G01DecisionAuthorizationService().Authorize(context, observation, assignment, "UNIT:RND");
}

static void AssertDenied(Action action, string expectedCode)
{
    try
    {
        action();
        throw new Exception($"Expected denial {expectedCode}.");
    }
    catch (ObservationAuthorizationException ex) when (ex.Code == expectedCode)
    {
    }
}

static void AssertStartDenied(Action action, string expectedCode)
{
    try
    {
        action();
        throw new Exception($"Expected denial {expectedCode}.");
    }
    catch (ObservationDomainException ex) when (ex.Code == expectedCode)
    {
    }
}

static void AssignmentPrincipalRequired() =>
    AssertDenied(
        () => AssertAuthorized(Context(), Submitted(), Assignment(principal: null)),
        "EIMS_G01_ASSIGNMENT_PRINCIPAL_REQUIRED");

static void InProgressRequired() =>
    AssertDenied(
        () => AssertAuthorized(Context(), Submitted(), Assignment(status: G01WorkAssignmentStatus.Accepted)),
        "EIMS_G01_ASSIGNMENT_NOT_IN_PROGRESS");

static void PrincipalMustMatchAssignment() =>
    AssertDenied(
        () => AssertAuthorized(Context("P-OTHER"), Submitted(), Assignment(principal: "P-REVIEWER")),
        "EIMS_G01_ASSIGNMENT_PRINCIPAL_MISMATCH");

static void ObservationMustMatchAssignment() =>
    AssertDenied(
        () => AssertAuthorized(Context(), Submitted(), Assignment(observationId: "OBS-OTHER")),
        "EIMS_G01_ASSIGNMENT_OBSERVATION_MISMATCH");

static void CapabilityRequired() =>
    AssertDenied(
        () => AssertAuthorized(Context(capabilities: Array.Empty<string>()), Submitted(), Assignment()),
        "EIMS_G01_DECIDE_CAPABILITY_REQUIRED");

static void SubmitterCannotDecide() =>
    AssertDenied(
        () => AssertAuthorized(Context("P-SUBMITTER"), Submitted(), Assignment(principal: "P-SUBMITTER")),
        "EIMS_G01_SELF_APPROVAL_FORBIDDEN");

static void ServerPrincipalOnly() =>
    AssertDenied(
        () => AssertAuthorized(Context("P-REVIEWER"), Submitted(), Assignment(principal: "P-CLIENT-SPOOF")),
        "EIMS_G01_ASSIGNMENT_PRINCIPAL_MISMATCH");

static void ValidAuthorization()
{
    AssertAuthorized(Context(), Submitted(), Assignment());
}

static void AcceptBindsPrincipal()
{
    var pending = Assignment(status: G01WorkAssignmentStatus.Pending, principal: null);
    var accepted = new ObservationSubmissionService().AcceptG01Assignment(pending, "P-REVIEWER");
    if (accepted.AssignedPrincipalId != "P-REVIEWER")
        throw new Exception("Accept must bind the server-supplied principal.");
}

static void StartRequiresOwner()
{
    var pending = Assignment(status: G01WorkAssignmentStatus.Pending, principal: null);
    var accepted = new ObservationSubmissionService().AcceptG01Assignment(pending, "P-REVIEWER");
    AssertStartDenied(
        () => new ObservationSubmissionService().StartG01Assignment(accepted, "P-OTHER"),
        "EIMS_G01_ASSIGNMENT_PRINCIPAL_MISMATCH");
}

static void OwnerCanStart()
{
    var pending = Assignment(status: G01WorkAssignmentStatus.Pending, principal: null);
    var accepted = new ObservationSubmissionService().AcceptG01Assignment(pending, "P-REVIEWER");
    var started = new ObservationSubmissionService().StartG01Assignment(accepted, "P-REVIEWER");
    if (started.Status != G01WorkAssignmentStatus.InProgress)
        throw new Exception("The bound assignment principal must be able to start the assignment.");
}
