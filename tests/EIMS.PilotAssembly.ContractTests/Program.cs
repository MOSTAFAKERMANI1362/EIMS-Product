using System.Security.Claims;
using System.Security.Cryptography;
using EIMS.PilotAssembly.Core;

var passed = 0;
var failed = 0;
void Test(string name, Action action){try{action();Console.WriteLine($"PASS {name}");passed++;}catch(Exception ex){Console.WriteLine($"FAIL {name}: {ex.Message}");failed++;}}
void Assert(bool condition,string message="assertion failed"){if(!condition)throw new Exception(message);}
PilotGate Gate(IReadOnlyList<PilotGate> gates,string id)=>gates.Single(x=>x.Id==id);

var fixtureDir = Path.Combine(Path.GetTempPath(), "eims-p5-contract-tests");
Directory.CreateDirectory(fixtureDir);
var p4Fixture = Path.Combine(fixtureDir, "p4-fixture.bin");
File.WriteAllBytes(p4Fixture, "EIMS-P4-CONTRACT-FIXTURE-v1"u8.ToArray());
var p4FixtureHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p4Fixture))).ToLowerInvariant();

Test("P5-CT-01 frozen product baseline identity",()=>{Assert(PilotBaseline.ProductBaseline.Contains("v6.360"));Assert(PilotBaseline.ProductSha256.Length==64);Assert(PilotBaseline.P4ExpectedSha256.Length==64);});
Test("P5-CT-02 anonymous principal cannot become network identity",()=>{Assert(IdentitySourcePolicy.GetAuthenticatedNetworkName(new ClaimsPrincipal(new ClaimsIdentity())) is null);});
Test("P5-CT-03 client identity headers are ignored",()=>{var p=new ClaimsPrincipal(new ClaimsIdentity());var h=new Dictionary<string,string>{{"X-EIMS-Pilot-User","SPOOFED\\admin"}};Assert(IdentitySourcePolicy.GetAuthenticatedNetworkName(p,h) is null);});
Test("P5-CT-04 authenticated principal is accepted as network identity source",()=>{var p=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.Name,"DOMAIN\\user1")},"Windows"));Assert(IdentitySourcePolicy.GetAuthenticatedNetworkName(p)=="DOMAIN\\user1");});
Test("P5-CT-05 missing P1 runtime is BLOCKED",()=>{var g=PilotReadinessEvaluator.Evaluate(Snapshot(p4Fixture,p4FixtureHash,p1:false,oracle:false,live:false));Assert(Gate(g,"p1.authority.runtime").Status==PilotGateStatus.Blocked);});
Test("P5-CT-06 P1 runtime without test evidence is TEST_REQUIRED",()=>{var s=Snapshot(p4Fixture,p4FixtureHash,true,true,false) with{P1ContractTestsPassed=false};var g=PilotReadinessEvaluator.Evaluate(s);Assert(Gate(g,"p1.authority.contract-tests").Status==PilotGateStatus.TestRequired);});
Test("P5-CT-07 incomplete Oracle binding is BLOCKED",()=>{var g=PilotReadinessEvaluator.Evaluate(Snapshot(p4Fixture,p4FixtureHash,true,false,false));Assert(Gate(g,"p2.oracle.binding").Status==PilotGateStatus.Blocked);});
Test("P5-CT-08 configured Oracle without live probe is TEST_REQUIRED",()=>{var g=PilotReadinessEvaluator.Evaluate(Snapshot(p4Fixture,p4FixtureHash,true,true,false));Assert(Gate(g,"p2.oracle.live").Status==PilotGateStatus.TestRequired);});
Test("P5-CT-09 unsafe Windows identity settings are BLOCKED",()=>{var s=Snapshot(p4Fixture,p4FixtureHash,true,true,false) with{TrustClientIdentityHeaders=true};var g=PilotReadinessEvaluator.Evaluate(s);Assert(Gate(g,"p3.windows.config").Status==PilotGateStatus.Blocked);});
Test("P5-CT-10 safe Windows config still requires live evidence",()=>{var g=PilotReadinessEvaluator.Evaluate(Snapshot(p4Fixture,p4FixtureHash,true,true,false));Assert(Gate(g,"p3.windows.live").Status==PilotGateStatus.TestRequired);});
Test("P5-CT-11 package hash gate accepts matching fixture",()=>{var g=PilotReadinessEvaluator.Evaluate(Snapshot(p4Fixture,p4FixtureHash,true,true,false));Assert(Gate(g,"p4.hrorg.package").Status==PilotGateStatus.Ready);});
Test("P5-CT-12 incorrect package hash is BLOCKED",()=>{var s=Snapshot(p4Fixture,new string('0',64),true,true,false);var g=PilotReadinessEvaluator.Evaluate(s);Assert(Gate(g,"p4.hrorg.package").Status==PilotGateStatus.Blocked);});
Test("P5-CT-13 config-only assembly cannot claim Network Pilot READY",()=>{var g=PilotReadinessEvaluator.Evaluate(Snapshot(p4Fixture,p4FixtureHash,true,true,false));Assert(!PilotReadinessEvaluator.IsNetworkPilotReady(g));Assert(g.Any(x=>x.Status==PilotGateStatus.TestRequired));});
Test("P5-CT-14 complete live evidence can satisfy readiness contract",()=>{var g=PilotReadinessEvaluator.Evaluate(Snapshot(p4Fixture,p4FixtureHash,true,true,true));Assert(PilotReadinessEvaluator.IsNetworkPilotReady(g));});
Test("P5-CT-15 fail-closed command gateway never mutates state",()=>{var r=new FailClosedCommandGateway().ExecuteAsync(new CommandAttempt("g01/decide","DOMAIN\\user1","c1",1,"i1","{}")).GetAwaiter().GetResult();Assert(r.HttpStatus==503);Assert(!r.StateMutated);});

// VS-01 TDD: state-based contract. GREEN requires real domain behavior, not type existence.
Test("VS01-UNIT-001 SubmitObservation valid transitions DRAFT to SUBMITTED_FOR_G01",()=>{
    var observation = new Observation("OBS-TEST-001", "کاهش توقف خط تولید", ObservationStatus.Draft, 1);
    var service = new ObservationSubmissionService();

    var submitted = service.SubmitObservation(observation);

    Assert(submitted.Status == ObservationStatus.SubmittedForG01,
        "VS01 RED: valid submission must transition DRAFT to SUBMITTED_FOR_G01.");
    Assert(submitted.Version == 2,
        "VS01 RED: successful submission must increment version.");
});

Test("VS01-UNIT-003 SubmitObservation creates exactly one pending G01 assignment",()=>{
    var observation = new Observation("OBS-TEST-003", "نمونه", ObservationStatus.Draft, 1);
    var service = new ObservationSubmissionService();
    var result = service.SubmitObservationWithG01Assignment(observation);
    Assert(result.Assignment.Status == G01WorkAssignmentStatus.Pending, "VS01 RED: successful submission must create a PENDING G01 work assignment.");
    Assert(result.Assignment.ObservationId == observation.Id, "VS01 RED: G01 assignment must reference the submitted observation.");
    Assert(result.Assignment.AssigneeRole == "INTAKE_STEWARD", "VS01 RED: G01 assignment must route to INTAKE_STEWARD.");
});

Test("VS01-UNIT-004 repeated G01 assignment is idempotent",()=>{
    var observation = new Observation("OBS-TEST-004", "نمونه", ObservationStatus.Draft, 1);
    var service = new ObservationSubmissionService();
    var first = service.SubmitObservationWithG01Assignment(observation);
    var second = service.SubmitObservationWithG01Assignment(first.Observation);
    Assert(first.Assignment.Id == second.Assignment.Id, "VS01 RED: repeated submission must not create a second active G01 assignment.");
    Assert(second.Assignment.Status == G01WorkAssignmentStatus.Pending, "VS01 RED: idempotent result must preserve the active assignment.");
});

Test("VS01-UNIT-002 SubmitObservation rejects non-DRAFT without mutation",()=>{
    var observation = new Observation("OBS-TEST-002", "نمونه", ObservationStatus.SubmittedForG01, 2);
    var service = new ObservationSubmissionService();

    try
    {
        service.SubmitObservation(observation);
        throw new Exception("VS01 RED: submitting a non-DRAFT observation must fail.");
    }
    catch (ObservationDomainException ex)
    {
        Assert(ex.Code == "EIMS_INVALID_STATE",
            "VS01 RED: invalid submission state must produce EIMS_INVALID_STATE.");
        Assert(observation.Status == ObservationStatus.SubmittedForG01,
            "VS01 RED: failed submission must not mutate state.");
        Assert(observation.Version == 2,
            "VS01 RED: failed submission must not mutate version.");
    }
});

Test("VS01-UNIT-005 existing active G01 assignment is reused",()=>{
    var observation = new Observation("OBS-TEST-005", "نمونه", ObservationStatus.SubmittedForG01, 2);
    var existing = new G01WorkAssignment("G01-OBS-TEST-005", observation.Id, "INTAKE_STEWARD", G01WorkAssignmentStatus.Pending);
    var service = new ObservationSubmissionService();

    var result = service.SubmitObservationWithG01Assignment(observation, existing);

    Assert(result.Assignment.Id == existing.Id,
        "VS01 RED: an existing active G01 assignment must be reused.");
});

Test("VS01-UNIT-006 G01 assignment lifecycle accepts then starts",()=>{
    var assignment = new G01WorkAssignment("G01-OBS-TEST-006", "OBS-TEST-006", "INTAKE_STEWARD", G01WorkAssignmentStatus.Pending);
    var service = new ObservationSubmissionService();

    var accepted = service.AcceptG01Assignment(assignment);
    var started = service.StartG01Assignment(accepted);

    Assert(accepted.Status == G01WorkAssignmentStatus.Accepted,
        "VS01 RED: PENDING assignment must transition to ACCEPTED.");
    Assert(started.Status == G01WorkAssignmentStatus.InProgress,
        "VS01 RED: ACCEPTED assignment must transition to IN_PROGRESS.");
});

Test("VS01-UNIT-007 cannot start G01 assignment directly from PENDING",()=>{
    var assignment = new G01WorkAssignment("G01-OBS-TEST-007", "OBS-TEST-007", "INTAKE_STEWARD", G01WorkAssignmentStatus.Pending);
    var service = new ObservationSubmissionService();

    try
    {
        service.StartG01Assignment(assignment);
        throw new Exception("VS01 RED: PENDING assignment must not transition directly to IN_PROGRESS.");
    }
    catch (ObservationDomainException ex)
    {
        Assert(ex.Code == "EIMS_INVALID_TRANSITION",
            "VS01 RED: invalid lifecycle transition must produce EIMS_INVALID_TRANSITION.");
    }
});

Test("VS01-UNIT-008 completed G01 assignment cannot be accepted again",()=>{
    var assignment = new G01WorkAssignment("G01-OBS-TEST-008", "OBS-TEST-008", "INTAKE_STEWARD", G01WorkAssignmentStatus.Completed);
    var service = new ObservationSubmissionService();

    try
    {
        service.AcceptG01Assignment(assignment);
        throw new Exception("VS01 RED: COMPLETED assignment must not be accepted again.");
    }
    catch (ObservationDomainException ex)
    {
        Assert(ex.Code == "EIMS_INVALID_TRANSITION",
            "VS01 RED: accepting a terminal assignment must produce EIMS_INVALID_TRANSITION.");
    }
});

Test("VS01-AUTH-001 submit observation requires CREATE capability and valid scope",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user1",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });

    var service = new ObservationAuthorizationService();
    Assert(service.CanSubmit(context, "OWNED_RECORD"),
        "VS01 RED: authenticated SUBMITTER with CREATE and valid scope must be authorized.");
});

Test("VS01-AUTH-002 missing CREATE capability is forbidden",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user1",
        new[] { "SUBMITTER" },
        Array.Empty<string>(),
        new[] { "OWNED_RECORD" });

    var service = new ObservationAuthorizationService();

    try
    {
        service.AuthorizeSubmit(context, "OWNED_RECORD");
        throw new Exception("VS01 RED: missing CREATE capability must be forbidden.");
    }
    catch (ObservationAuthorizationException ex)
    {
        Assert(ex.Code == "EIMS_FORBIDDEN",
            "VS01 RED: authorization failure must produce EIMS_FORBIDDEN.");
    }
});

Test("VS01-AUTH-003 wrong scope is forbidden",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user1",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });

    var service = new ObservationAuthorizationService();

    try
    {
        service.AuthorizeSubmit(context, "ORG_UNIT");
        throw new Exception("VS01 RED: scope outside effective scopes must be forbidden.");
    }
    catch (ObservationAuthorizationException ex)
    {
        Assert(ex.Code == "EIMS_SCOPE_VIOLATION",
            "VS01 RED: invalid scope must produce EIMS_SCOPE_VIOLATION.");
    }
});

Test("VS01-AUTH-004 client role and scope values do not override security context",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user1",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });

    var service = new ObservationAuthorizationService();

    try
    {
        service.AuthorizeSubmit(context, "GLOBAL", clientRole: "ADMIN", clientCapability: "ROLE_ADMIN");
        throw new Exception("VS01 RED: client-supplied role/capability/scope must not elevate authorization.");
    }
    catch (ObservationAuthorizationException ex)
    {
        Assert(ex.Code == "EIMS_SCOPE_VIOLATION",
            "VS01 RED: authorization must use server-derived effective scope.");
    }
});

Test("VS01-APP-001 authorized submit delegates to domain and returns submitted observation",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user1",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-APP-001", "نمونه", ObservationStatus.Draft, 1);
    var service = new ObservationApplicationService();

    var result = service.SubmitObservation(observation, context, "OWNED_RECORD");

    Assert(result.Status == ObservationStatus.SubmittedForG01,
        "VS01 RED: authorized application command must submit the observation.");
    Assert(result.Version == 2,
        "VS01 RED: successful application command must preserve domain version transition.");
});

Test("VS01-APP-002 forbidden submit does not mutate observation",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user2",
        new[] { "SUBMITTER" },
        Array.Empty<string>(),
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-APP-002", "نمونه", ObservationStatus.Draft, 1);
    var service = new ObservationApplicationService();

    try
    {
        service.SubmitObservation(observation, context, "OWNED_RECORD");
        throw new Exception("VS01 RED: forbidden application command must fail before domain mutation.");
    }
    catch (ObservationAuthorizationException ex)
    {
        Assert(ex.Code == "EIMS_FORBIDDEN",
            "VS01 RED: missing capability must remain EIMS_FORBIDDEN at application boundary.");
        Assert(observation.Status == ObservationStatus.Draft,
            "VS01 RED: forbidden command must not mutate observation state.");
        Assert(observation.Version == 1,
            "VS01 RED: forbidden command must not mutate observation version.");
    }
});

Test("VS01-APP-003 application command ignores client authorization claims",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user3",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-APP-003", "نمونه", ObservationStatus.Draft, 1);
    var service = new ObservationApplicationService();

    try
    {
        service.SubmitObservation(
            observation,
            context,
            "GLOBAL",
            clientRole: "ADMIN",
            clientCapability: "ROLE_ADMIN");
        throw new Exception("VS01 RED: client authorization claims must not alter application authorization.");
    }
    catch (ObservationAuthorizationException ex)
    {
        Assert(ex.Code == "EIMS_SCOPE_VIOLATION",
            "VS01 RED: application authorization must use server-derived scope only.");
        Assert(observation.Status == ObservationStatus.Draft,
            "VS01 RED: rejected client scope must not mutate observation state.");
    }
});

Console.WriteLine($"RESULT {passed}/{passed+failed} PASS");
Environment.ExitCode=failed==0?0:1;

PilotBindingSnapshot Snapshot(string p4Path,string p4ExpectedHash,bool p1,bool oracle,bool live)=>new(
    p1, live&&p1, p1?"P1-PACKAGE":"",
    oracle?"APPROVED_VERSION":"",oracle?"APPROVED_PROVIDER":"",oracle?"APPROVED_CONNECTION":"",oracle?"EIMS_SVC":"",oracle?"EIMS":"",live&&oracle,
    "IIS_WINDOWS_AUTH",true,false,live,true,true,live,p4Path,p4ExpectedHash,live,live,live,live,live);