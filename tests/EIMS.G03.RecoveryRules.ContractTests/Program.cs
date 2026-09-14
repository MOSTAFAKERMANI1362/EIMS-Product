using System.Text.Json;
using EIMS.Authority.Recovery;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.G03.RecoveryRules.ContractTests <P1_WAVE2_G03_RULE_REBASELINE_v1.0.json>");
    return 2;
}

using var contractDocument = JsonDocument.Parse(File.ReadAllText(args[0]));
var contract = contractDocument.RootElement;
var contractRules = contract.GetProperty("ruleContracts").EnumerateArray().ToArray();

var evaluator = new RecoveredG03RuleEvaluator();
var actorOwner = new AuthorityActor("P-OWNER", "DOMAIN\\owner", "TEST", "A-1", new[] { "NEED_OWNER" }, new[] { "GLOBAL" });
var actorReviewer = new AuthorityActor("P-REVIEW", "DOMAIN\\review", "TEST", "A-2", new[] { "NEED_REVIEWER" }, new[] { "GLOBAL" });
var submitPolicy = new CommandPolicy("needs.submit-g03", new[] { "NEED_OWNER" }, new[] { "DRAFT" }, "P1-G03-SUBMIT-REBASELINE-1.0", "UNRECOVERED_EVENT_IDENTITY", true, true, false, false);
var decisionPolicy = new CommandPolicy("needs.g03-decision", new[] { "NEED_REVIEWER" }, new[] { "PENDING_G03_REVIEW" }, "P1-G03-DECISION-REBASELINE-1.0", "UNRECOVERED_EVENT_IDENTITY", true, true, false, false);

var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));

Add("G03R-CT-00A", "Wave 2 artifact is explicit recovery rebaseline", contract.GetProperty("status").GetString() == "APPROVED_REBASELINE_FOR_P1_RULE_BINDING" && contract.GetProperty("decisionClass").GetString() == "RECOVERY_REBASELINE_ACCEPTANCE");
Add("G03R-CT-00B", "artifact does not claim missing original rule catalog", contract.GetProperty("provenance").GetProperty("doesNotClaimMissingOriginalRuleCatalogRecovered").GetBoolean());
Add("G03R-CT-00C", "artifact binds exactly two G03 rule contracts", contractRules.Length == 2 && contractRules.Select(x => x.GetProperty("command").GetString()).OrderBy(x => x).SequenceEqual(new[] { "needs.g03-decision", "needs.submit-g03" }));
Add("G03R-CT-00D", "event and mutation contracts remain unrecovered", !contract.GetProperty("runtimeSafety").GetProperty("eventContractsRecovered").GetBoolean() && !contract.GetProperty("runtimeSafety").GetProperty("mutationContractsRecovered").GetBoolean() && !contract.GetProperty("runtimeSafety").GetProperty("productMutationEnabled").GetBoolean());
Add("G03R-CT-00E", "request body cannot replace persisted Need definition", contract.GetProperty("runtimeSafety").GetProperty("ruleEvaluationMustUseAuthoritativeAggregateFacts").GetBoolean() && contract.GetProperty("runtimeSafety").GetProperty("requestBodyMayNotOverridePersistedNeedDefinition").GetBoolean());

Dictionary<string,string> GoodNeed() => new(StringComparer.OrdinalIgnoreCase)
{
    ["title"] = "کاهش توقف اضطراری خط نورد",
    ["owner"] = "سرپرست نورد",
    ["current"] = "میانگین توقف اضطراری ماهانه ۱۸ ساعت است.",
    ["desired"] = "توقف اضطراری ماهانه باید به کمتر از ۶ ساعت برسد.",
    ["gap"] = "کاهش حداقل ۱۲ ساعت توقف در ماه لازم است."
};

AggregateSnapshot Need(Dictionary<string,string>? facts) => new("NEED-1", "Need", "DRAFT", 1, "P-OWNER", "NEED_OWNER", "GLOBAL", facts);
AggregateSnapshot Pending(Dictionary<string,string>? facts = null) => new("NEED-1", "Need", "PENDING_G03_REVIEW", 2, "P-OWNER", "NEED_OWNER", "GLOBAL", facts ?? new Dictionary<string,string>{{"g03ReviewStatus","PENDING"}});
AuthorityCommand Submit(string body = "{}") => new("needs.submit-g03", "NEED-1", 1, "K1", "C1", body);
AuthorityCommand Decide(string body) => new("needs.g03-decision", "NEED-1", 2, "K2", "C2", body);

async Task<RuleEvaluation> EvalSubmit(Dictionary<string,string>? facts, string body = "{}") => await evaluator.EvaluateAsync(Submit(body), actorOwner, Need(facts), submitPolicy);
async Task<RuleEvaluation> EvalDecision(string body, Dictionary<string,string>? facts = null) => await evaluator.EvaluateAsync(Decide(body), actorReviewer, Pending(facts), decisionPolicy);

var good = await EvalSubmit(GoodNeed());
Add("G03R-CT-01", "complete authoritative Need passes submit rule", good.Passed && good.Code == "G03_SUBMIT_RULES_PASS");

async Task ExpectSubmitFail(string id, string name, string expectedCode, Action<Dictionary<string,string>> mutate)
{
    var facts = GoodNeed(); mutate(facts);
    var r = await EvalSubmit(facts);
    Add(id, name, !r.Passed && r.Code == expectedCode);
}

await ExpectSubmitFail("G03R-CT-02", "title below 10 chars fails", "G03_S01_TITLE_TOO_SHORT", f => f["title"] = "کوتاه");
await ExpectSubmitFail("G03R-CT-03", "temporary related-to title fails", "G03_S02_TEMP_TITLE", f => f["title"] = "نیاز مرتبط با توقف خط نورد");
await ExpectSubmitFail("G03R-CT-04", "owner below 3 chars fails", "G03_S03_OWNER_TOO_SHORT", f => f["owner"] = "مد");
await ExpectSubmitFail("G03R-CT-05", "process-owner placeholder fails", "G03_S04_OWNER_PLACEHOLDER", f => f["owner"] = "مالک فرآیند مرتبط");
await ExpectSubmitFail("G03R-CT-06", "unit-owner placeholder fails", "G03_S04_OWNER_PLACEHOLDER", f => f["owner"] = "مالک واحد موضوع");
await ExpectSubmitFail("G03R-CT-07", "current below 15 chars fails", "G03_S05_CURRENT_TOO_SHORT", f => f["current"] = "توقف زیاد است");
await ExpectSubmitFail("G03R-CT-08", "current placeholder fails", "G03_S06_CURRENT_PLACEHOLDER", f => f["current"] = "وضعیت موجود نیازمند تکمیل و بررسی است");
await ExpectSubmitFail("G03R-CT-09", "desired below 15 chars fails", "G03_S07_DESIRED_TOO_SHORT", f => f["desired"] = "توقف کم شود");
await ExpectSubmitFail("G03R-CT-10", "desired placeholder fails", "G03_S08_DESIRED_PLACEHOLDER", f => f["desired"] = "وضعیت مطلوب را تکمیل کنید و عدد بدهید");
await ExpectSubmitFail("G03R-CT-11", "gap below 10 chars fails", "G03_S09_GAP_TOO_SHORT", f => f["gap"] = "کمبود عدد");
await ExpectSubmitFail("G03R-CT-12", "gap placeholder fails", "G03_S10_GAP_PLACEHOLDER", f => f["gap"] = "نیازمند تکمیل دقیق شکاف است");
await ExpectSubmitFail("G03R-CT-13", "current equal desired fails", "G03_S11_CURRENT_EQUALS_DESIRED", f => f["desired"] = f["current"]);

var missingFacts = await EvalSubmit(null);
Add("G03R-CT-14", "missing authoritative facts fails closed", !missingFacts.Passed && missingFacts.Code == "G03_RULE_FACTS_REQUIRED");
var partial = GoodNeed(); partial.Remove("gap");
var partialResult = await EvalSubmit(partial);
Add("G03R-CT-15", "missing one authoritative fact fails closed", !partialResult.Passed && partialResult.Code == "G03_RULE_FACTS_REQUIRED");
var forgedFacts = GoodNeed(); forgedFacts["title"] = "بد";
var forged = await EvalSubmit(forgedFacts, "{\"title\":\"این عنوان از درخواست بسیار کامل و معتبر است\",\"owner\":\"مالک جعلی\"}");
Add("G03R-CT-16", "request body cannot override bad persisted Need definition", !forged.Passed && forged.Code == "G03_S01_TITLE_TOO_SHORT");

var approve = await EvalDecision("{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\",\"note\":\"\"}");
Add("G03R-CT-17", "APPROVE with all three YES passes", approve.Passed && approve.Code == "G03_DECISION_APPROVE_RULES_PASS");
var invalidDecision = await EvalDecision("{\"decision\":\"HOLD\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\"}");
Add("G03R-CT-18", "decision outside APPROVE/RETURN fails", !invalidDecision.Passed && invalidDecision.Code == "G03_D02_DECISION_INVALID");
var noPending = await EvalDecision("{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\"}", new Dictionary<string,string>{{"g03ReviewStatus","APPROVED"}});
Add("G03R-CT-19", "non-pending authoritative review status fails", !noPending.Passed && noPending.Code == "G03_D01_REVIEW_NOT_PENDING");
var noStatus = await EvalDecision("{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\"}", new Dictionary<string,string>());
Add("G03R-CT-20", "missing authoritative review status fails", !noStatus.Passed && noStatus.Code == "G03_D01_REVIEW_STATUS_REQUIRED");
var noDefinition = await EvalDecision("{\"decision\":\"APPROVE\",\"definitionComplete\":\"NO\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\"}");
Add("G03R-CT-21", "APPROVE definition NO fails", !noDefinition.Passed && noDefinition.Code == "G03_D03_DEFINITION_NOT_CONFIRMED");
var noMeasurable = await EvalDecision("{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"NO\",\"solutionBiasFree\":\"YES\"}");
Add("G03R-CT-22", "APPROVE measurable NO fails", !noMeasurable.Passed && noMeasurable.Code == "G03_D03_MEASURABLE_NOT_CONFIRMED");
var noBias = await EvalDecision("{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"NO\"}");
Add("G03R-CT-23", "APPROVE solutionBiasFree NO fails", !noBias.Passed && noBias.Code == "G03_D03_SOLUTION_BIAS_NOT_CONFIRMED");
var missingControl = await EvalDecision("{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\"}");
Add("G03R-CT-24", "APPROVE missing one control fails", !missingControl.Passed && missingControl.Code == "G03_D03_SOLUTION_BIAS_NOT_CONFIRMED");
var shortReturn = await EvalDecision("{\"decision\":\"RETURN\",\"note\":\"اصلاح شود\"}");
Add("G03R-CT-25", "RETURN note below 10 trimmed chars fails", !shortReturn.Passed && shortReturn.Code == "G03_D04_RETURN_NOTE_TOO_SHORT");
var validReturn = await EvalDecision("{\"decision\":\"RETURN\",\"note\":\"لطفاً خط مبنای عددی توقف را دقیق‌تر ثبت کنید.\"}");
Add("G03R-CT-26", "RETURN actionable note passes", validReturn.Passed && validReturn.Code == "G03_DECISION_RETURN_RULES_PASS");
var malformed = await EvalDecision("{not-json}");
Add("G03R-CT-27", "malformed decision JSON fails deterministically", !malformed.Passed && malformed.Code == "G03_DECISION_JSON_INVALID");
var bodyCannotSetPending = await EvalDecision("{\"decision\":\"APPROVE\",\"g03ReviewStatus\":\"PENDING\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\"}", new Dictionary<string,string>{{"g03ReviewStatus","RETURNED"}});
Add("G03R-CT-28", "request cannot override persisted G03 review status", !bodyCannotSetPending.Passed && bodyCannotSetPending.Code == "G03_D01_REVIEW_NOT_PENDING");
var wrongSubmitPolicy = submitPolicy with { RuleSet = "OTHER" };
var wrongSet = await evaluator.EvaluateAsync(Submit(), actorOwner, Need(GoodNeed()), wrongSubmitPolicy);
Add("G03R-CT-29", "wrong rule set identity fails", !wrongSet.Passed && wrongSet.Code == "G03_RULESET_MISMATCH");
var unknown = await evaluator.EvaluateAsync(new AuthorityCommand("g02.decide","CASE-1",1,"K","C","{}"), actorReviewer, Pending(), decisionPolicy);
Add("G03R-CT-30", "unbound Product command never receives generic pass", !unknown.Passed && unknown.Code == "P1_RULESET_NOT_BOUND");

foreach (var r in results) Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Id} {r.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;
