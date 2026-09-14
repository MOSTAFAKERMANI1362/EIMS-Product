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

Console.WriteLine($"RESULT {passed}/{passed+failed} PASS");
Environment.ExitCode=failed==0?0:1;

PilotBindingSnapshot Snapshot(string p4Path,string p4ExpectedHash,bool p1,bool oracle,bool live)=>new(
    p1, live&&p1, p1?"P1-PACKAGE":"",
    oracle?"APPROVED_VERSION":"",oracle?"APPROVED_PROVIDER":"",oracle?"APPROVED_CONNECTION":"",oracle?"EIMS_SVC":"",oracle?"EIMS":"",live&&oracle,
    "IIS_WINDOWS_AUTH",true,false,live,true,true,live,p4Path,p4ExpectedHash,live,live,live,live,live);
