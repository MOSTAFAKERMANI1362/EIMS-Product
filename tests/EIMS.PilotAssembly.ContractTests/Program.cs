using EIMS.PilotAssembly.Host;
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



Test("VS01-APP-004 atomic submit returns observation and exactly one G01 assignment as one business operation",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user4",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });

    var observation = new Observation("OBS-APP-004", "نمونه", ObservationStatus.Draft, 1);
    var service = new ObservationApplicationService();

    var result = service.SubmitObservationWithG01Assignment(
        observation,
        context,
        "OWNED_RECORD");

    Assert(result.Observation.Status == ObservationStatus.SubmittedForG01,
        "VS01 RED: atomic application command must submit the observation.");
    Assert(result.Observation.Version == 2,
        "VS01 RED: atomic application command must increment the observation version.");
    Assert(result.Assignment.ObservationId == observation.Id,
        "VS01 RED: atomic application command must create a G01 assignment for the submitted observation.");
    Assert(result.Assignment.Status == G01WorkAssignmentStatus.Pending,
        "VS01 RED: the created G01 assignment must be PENDING.");
});

Test("VS01-AUDIT-ENF-001 successful submit appends a server-derived audit record",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\audit-user",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-AUD-ENF-001", "نمونه", ObservationStatus.Draft, 1);
    var audit = new InMemoryObservationAuditSink();
    var service = new ObservationApplicationService(auditSink: audit);

    var result = service.SubmitObservationWithG01Assignment(
        observation,
        context,
        "OWNED_RECORD");

    Assert(audit.Records.Count == 1,
        "VS01 RED: successful sensitive submission must append exactly one audit record.");
    Assert(audit.Records[0].ActorPrincipalId == context.PrincipalId,
        "VS01 RED: audit actor must come from authenticated security context.");
    Assert(audit.Records[0].CommandName == "OBSERVATION_SUBMIT",
        "VS01 RED: audit must identify the business command.");
    Assert(audit.Records[0].EntityId == result.Observation.Id,
        "VS01 RED: audit must identify the submitted observation.");
    Assert(audit.Records[0].Outcome == "SUCCESS",
        "VS01 RED: successful submission must produce a SUCCESS audit outcome.");
    Assert(audit.Records[0].ResultingVersion == result.Observation.Version,
        "VS01 RED: audit must capture the resulting observation version.");
});

Test("VS01-AUDIT-ENF-002 forbidden submit does not append audit",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\audit-user2",
        new[] { "SUBMITTER" },
        Array.Empty<string>(),
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-AUD-ENF-002", "نمونه", ObservationStatus.Draft, 1);
    var audit = new InMemoryObservationAuditSink();
    var service = new ObservationApplicationService(auditSink: audit);

    try
    {
        service.SubmitObservationWithG01Assignment(
            observation,
            context,
            "OWNED_RECORD");
        throw new Exception("VS01 RED: forbidden sensitive submission must fail.");
    }
    catch (ObservationAuthorizationException ex)
    {
        Assert(ex.Code == "EIMS_FORBIDDEN",
            "VS01 RED: authorization must fail before audit is appended.");
        Assert(audit.Records.Count == 0,
            "VS01 RED: rejected command must not append a SUCCESS audit record.");
    }
});

Test("VS01-AUDIT-ENF-003 failed concurrency does not append success audit",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\audit-user3",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-AUD-ENF-003", "نمونه", ObservationStatus.Draft, 5);
    var audit = new InMemoryObservationAuditSink();
    var service = new ObservationApplicationService(auditSink: audit);

    try
    {
        service.SubmitObservationWithG01Assignment(
            observation,
            context,
            "OWNED_RECORD",
            expectedVersion: 4);
        throw new Exception("VS01 RED: stale expectedVersion must fail.");
    }
    catch (ObservationDomainException ex)
    {
        Assert(ex.Code == "EIMS_CONCURRENCY_CONFLICT",
            "VS01 RED: stale expectedVersion must remain a concurrency conflict.");
        Assert(audit.Records.Count == 0,
            "VS01 RED: failed sensitive command must not append a SUCCESS audit record.");
    }
});

Test("VS01-CON-001 matching expected version succeeds and increments version",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user6",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-CON-001", "نمونه", ObservationStatus.Draft, 7);
    IObservationRepository repository = new InMemoryObservationRepository();
    repository.Save(observation);
    var service = new ObservationApplicationService(repository: repository);

    var result = service.SubmitObservationWithG01Assignment(
        observation,
        context,
        "OWNED_RECORD",
        expectedVersion: 7);

    Assert(result.Observation.Status == ObservationStatus.SubmittedForG01,
        "VS01 RED: matching expectedVersion must allow submission.");
    Assert(result.Observation.Version == 8,
        "VS01 RED: successful optimistic-concurrency transition must increment version.");
});

Test("VS01-CON-002 stale expected version rejects without mutation",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user7",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-CON-002", "نمونه", ObservationStatus.Draft, 7);
    var service = new ObservationApplicationService();

    var failed = false;
    try
    {
        service.SubmitObservationWithG01Assignment(
            observation,
            context,
            "OWNED_RECORD",
            expectedVersion: 6);
    }
    catch (ObservationDomainException ex)
    {
        failed = ex.Code == "EIMS_CONCURRENCY_CONFLICT";
    }

    Assert(failed,
        "VS01 RED: stale expectedVersion must raise EIMS_CONCURRENCY_CONFLICT.");
    Assert(observation.Status == ObservationStatus.Draft && observation.Version == 7,
        "VS01 RED: stale concurrency rejection must not mutate the observation.");
});

Test("VS01-PERSIST-001 successful submit persists the new observation version",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\persist-user1",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-PERSIST-001", "نمونه", ObservationStatus.Draft, 1);
    IObservationRepository repository = new InMemoryObservationRepository();
    repository.Save(observation);
    var service = new ObservationApplicationService(repository: repository);

    var result = service.SubmitObservationWithG01Assignment(
        observation,
        context,
        "OWNED_RECORD",
        expectedVersion: 1);

    Assert(repository.GetById(observation.Id) == result.Observation,
        "VS01 RED: successful submit must persist the resulting observation through the repository.");
});

Test("VS01-PERSIST-002 repository version conflict rejects before success audit",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\persist-user2",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var commandObservation = new Observation("OBS-PERSIST-002", "نمونه", ObservationStatus.Draft, 1);
    IObservationRepository repository = new InMemoryObservationRepository();
    repository.Save(new Observation(commandObservation.Id, commandObservation.Title, ObservationStatus.Draft, 2));
    var audit = new InMemoryObservationAuditSink();
    var service = new ObservationApplicationService(
        repository: repository,
        auditSink: audit);

    var failed = false;
    try
    {
        service.SubmitObservationWithG01Assignment(
            commandObservation,
            context,
            "OWNED_RECORD",
            expectedVersion: 1);
    }
    catch (ObservationDomainException ex)
    {
        failed = ex.Code == "EIMS_CONCURRENCY_CONFLICT";
    }

    Assert(failed,
        "VS01 RED: repository version conflict must raise EIMS_CONCURRENCY_CONFLICT.");
    Assert(repository.GetById(commandObservation.Id)?.Version == 2,
        "VS01 RED: repository conflict must preserve the current stored version.");
    Assert(audit.Records.Count == 0,
        "VS01 RED: repository conflict must not append a SUCCESS audit.");
});

Test("VS01-IDEMP-001 same idempotency key and semantic request returns same result",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\\\idemp1",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-IDEMP-001", "نمونه", ObservationStatus.Draft, 1);
    var service = new ObservationApplicationService();

    var first = service.SubmitObservationWithG01Assignment(
        observation, context, "OWNED_RECORD", idempotencyKey: "IDEMP-001");
    var second = service.SubmitObservationWithG01Assignment(
        observation, context, "OWNED_RECORD", idempotencyKey: "IDEMP-001");

    Assert(first.Observation == second.Observation,
        "VS01 RED: same idempotency key and semantic request must return the same result.");
    Assert(first.Assignment.Id == second.Assignment.Id,
        "VS01 RED: same idempotency key must not create a duplicate G01 assignment.");
});

Test("VS01-IDEMP-002 same idempotency key with different semantic request is rejected",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\\\idemp2",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var firstObservation = new Observation("OBS-IDEMP-002-A", "نمونه A", ObservationStatus.Draft, 1);
    var secondObservation = new Observation("OBS-IDEMP-002-B", "نمونه B", ObservationStatus.Draft, 1);
    var service = new ObservationApplicationService();

    service.SubmitObservationWithG01Assignment(
        firstObservation, context, "OWNED_RECORD", idempotencyKey: "IDEMP-002");

    var failed = false;
    try
    {
        service.SubmitObservationWithG01Assignment(
            secondObservation, context, "OWNED_RECORD", idempotencyKey: "IDEMP-002");
    }
    catch (ObservationDomainException ex)
    {
        failed = ex.Code == "EIMS_IDEMPOTENCY_CONFLICT";
    }

    Assert(failed,
        "VS01 RED: reusing an idempotency key for a different semantic request must raise EIMS_IDEMPOTENCY_CONFLICT.");
});

Test("VS01-REPO-001 repository loads observation by stable identifier",()=>{
    IObservationRepository repository = new InMemoryObservationRepository();
    var observation = new Observation("OBS-REPO-001", "نمونه", ObservationStatus.Draft, 1);
    repository.Save(observation);
    var loaded = repository.GetById(observation.Id);
    Assert(loaded == observation, "VS01 RED: repository must return the observation stored under its stable identifier.");
});

Test("VS01-REPO-002 repository saves submitted observation with expected version guard",()=>{
    IObservationRepository repository = new InMemoryObservationRepository();
    var observation = new Observation("OBS-REPO-002", "نمونه", ObservationStatus.Draft, 3);
    repository.Save(observation);
    var submitted = new Observation(observation.Id, observation.Title, ObservationStatus.SubmittedForG01, 4);
    var updated = repository.SaveIfVersion(submitted, expectedVersion: 3);
    Assert(updated, "VS01 RED: matching expected version must permit repository update.");
    Assert(repository.GetById(observation.Id) == submitted, "VS01 RED: successful repository update must persist the new version/state.");
});

Test("VS01-REPO-003 stale repository version rejects without overwrite",()=>{
    IObservationRepository repository = new InMemoryObservationRepository();
    var current = new Observation("OBS-REPO-003", "نمونه", ObservationStatus.SubmittedForG01, 5);
    repository.Save(current);
    var stale = new Observation(current.Id, current.Title, ObservationStatus.SubmittedForG01, 6);
    var updated = repository.SaveIfVersion(stale, expectedVersion: 4);
    Assert(!updated, "VS01 RED: stale expected version must reject the repository update.");
    Assert(repository.GetById(current.Id) == current, "VS01 RED: rejected stale update must not overwrite current state.");
});

Test("VS01-APP-005 submit-to-G01 executes inside an application transaction boundary",()=>{
    var context = new ObservationSecurityContext(
        "DOMAIN\\user5",
        new[] { "SUBMITTER" },
        new[] { "CREATE" },
        new[] { "OWNED_RECORD" });
    var observation = new Observation("OBS-APP-005", "نمونه", ObservationStatus.Draft, 1);
    var transaction = new RecordingObservationTransaction();
    var service = new ObservationApplicationService(transaction: transaction);

    var result = service.SubmitObservationWithG01Assignment(
        observation,
        context,
        "OWNED_RECORD");

    Assert(result.Observation.Status == ObservationStatus.SubmittedForG01,
        "VS01 RED: transaction-wrapped command must still submit the observation.");
    Assert(transaction.State == ObservationTransactionState.Committed,
        "VS01 RED: successful submit-to-G01 must commit the application transaction.");
});


Test("VS01-API-001 submission request DTO carries only command data",()=>{
    var request = new SubmitObservationRequestDto();
    var properties = typeof(SubmitObservationRequestDto).GetProperties();

    Assert(properties.Length == 0,
        "VS01 RED: submission authorization data must not be accepted through the request body.");
});

Test("VS01-API-002 submission response DTO exposes observation and G01 assignment state",()=>{
    var response = new SubmitObservationResponseDto(
        "OBS-API-002",
        "SUBMITTED_FOR_G01",
        2,
        "G01-OBS-API-002",
        "PENDING");

    Assert(response.ObservationId == "OBS-API-002",
        "VS01 RED: response must expose the stable observation identifier.");
    Assert(response.Status == "SUBMITTED_FOR_G01",
        "VS01 RED: response must expose the resulting observation state.");
    Assert(response.Version == 2,
        "VS01 RED: response must expose the resulting optimistic-concurrency version.");
    Assert(response.G01AssignmentId == "G01-OBS-API-002",
        "VS01 RED: response must expose the created G01 assignment identifier.");
    Assert(response.G01AssignmentStatus == "PENDING",
        "VS01 RED: response must expose the G01 assignment state.");
});

Test("VS01-API-003 submission contract defines command route and concurrency/idempotency headers",()=>{
    Assert(ObservationApiContract.SubmissionRoute == "/api/v1/observations/{observationId}/submission",
        "VS01 RED: submission must use the approved command route.");
    Assert(ObservationApiContract.ExpectedVersionHeader == "If-Match",
        "VS01 RED: optimistic concurrency must bind expectedVersion to If-Match.");
    Assert(ObservationApiContract.IdempotencyHeader == "Idempotency-Key",
        "VS01 RED: sensitive submission must support Idempotency-Key.");
});


Test("VS01-AUDIT-001 audit record captures server-derived actor and command identity",()=>{
    var audit = new ObservationAuditRecord(
        "AUD-001",
        "DOMAIN\\\\reviewer1",
        "OBSERVATION_SUBMIT",
        "OBS-AUD-001",
        "SUCCESS",
        2);
    Assert(audit.ActorPrincipalId == "DOMAIN\\\\reviewer1",
        "VS01 RED: audit actor must come from the authenticated server context.");
    Assert(audit.CommandName == "OBSERVATION_SUBMIT",
        "VS01 RED: audit must identify the business command.");
    Assert(audit.EntityId == "OBS-AUD-001",
        "VS01 RED: audit must identify the affected business entity.");
});

Test("VS01-AUDIT-002 audit record is append-oriented and does not expose mutation operations",()=>{
    var methods = typeof(IObservationAuditSink).GetMethods();
    Assert(methods.Length == 1,
        "VS01 RED: audit sink must expose only an append operation at the application contract boundary.");
    Assert(methods[0].Name == "Append",
        "VS01 RED: audit sink mutation contract must be append-only.");
});

Test("VS01-AUDIT-003 audit append requires a non-empty server actor",()=>{
    try
    {
        _ = new ObservationAuditRecord(
            "AUD-003",
            "",
            "OBSERVATION_SUBMIT",
            "OBS-AUD-003",
            "SUCCESS",
            2);
        throw new Exception("VS01 RED: audit records must reject an empty actor principal.");
    }
    catch (ArgumentException)
    {
    }
});

Test("VS01-AUDIT-004 audit record carries operation outcome and resulting version",()=>{
    var audit = new ObservationAuditRecord(
        "AUD-004",
        "DOMAIN\\\\reviewer4",
        "OBSERVATION_SUBMIT",
        "OBS-AUD-004",
        "SUCCESS",
        7);
    Assert(audit.Outcome == "SUCCESS",
        "VS01 RED: audit must record the operation outcome.");
    Assert(audit.ResultingVersion == 7,
        "VS01 RED: audit must record the resulting business version.");
});

Console.WriteLine($"RESULT {passed}/{passed+failed} PASS");
Environment.ExitCode=failed==0?0:1;

PilotBindingSnapshot Snapshot(string p4Path,string p4ExpectedHash,bool p1,bool oracle,bool live)=>new(
    p1, live&&p1, p1?"P1-PACKAGE":"",
    oracle?"APPROVED_VERSION":"",oracle?"APPROVED_PROVIDER":"",oracle?"APPROVED_CONNECTION":"",oracle?"EIMS_SVC":"",oracle?"EIMS":"",live&&oracle,
    "IIS_WINDOWS_AUTH",true,false,live,true,true,live,p4Path,p4ExpectedHash,live,live,live,live,live);

// VS01 RED helper: implementation must provide the transaction boundary contract.
sealed class RecordingObservationTransaction : IObservationTransaction
{
    public ObservationTransactionState State { get; private set; }

    public T Execute<T>(Func<T> operation)
    {
        State = ObservationTransactionState.Active;
        try
        {
            var result = operation();
            State = ObservationTransactionState.Committed;
            return result;
        }
        catch
        {
            State = ObservationTransactionState.RolledBack;
            throw;
        }
    }
}
