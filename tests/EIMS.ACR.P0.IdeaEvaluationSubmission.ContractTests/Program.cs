using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 2 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.IdeaEvaluationSubmission.ContractTests <ACR-P0-004-json> <ACR-P0-001-event-naming-json>");
    return 2;
}

using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var namingDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr = acrDoc.RootElement;
var naming = namingDoc.RootElement;
var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));
string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? string.Empty;
bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
string[] SA(JsonElement e, string p) => e.GetProperty(p).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();

var frozen = acr.GetProperty("frozenProduct");
var provenance = acr.GetProperty("provenance");
var command = acr.GetProperty("commandContract");
var pre = command.GetProperty("preconditions");
var mutation = command.GetProperty("serverMutation");
var plan = acr.GetProperty("evaluationPlanContract");
var assignment = plan.GetProperty("assignmentContext");
var stale = plan.GetProperty("stalePlanPolicy");
var handoff = acr.GetProperty("g04HandoffContract");
var evt = acr.GetProperty("domainEvent");
var atomic = acr.GetProperty("atomicityContract");
var client = acr.GetProperty("clientTrustBoundary");
var p1 = acr.GetProperty("p1ConsumptionGate");

Add("IDEASUB-CT-01", "ACR identity/schema/status are exact",
    S(acr,"acrId") == "ACR-P0-004"
    && S(acr,"schema") == "EIMS-ACR-IDEA-EVALUATION-SUBMISSION-1.0"
    && S(acr,"status") == "APPROVED_FOR_P1_IMPLEMENTATION");
Add("IDEASUB-CT-02", "decision is explicitly post-freeze", S(acr,"decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION");
Add("IDEASUB-CT-03", "frozen v6.360 identity and hash are preserved",
    S(frozen,"file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html"
    && S(frozen,"sha256") == "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a"
    && !B(frozen,"modifiedByThisDecision"));
Add("IDEASUB-CT-04", "historical transition/event recovery is not falsely claimed",
    !B(provenance,"historicalOriginalFullTransitionGraphRecovered")
    && !B(provenance,"historicalOriginalDomainEventIdentityRecovered")
    && B(provenance,"mustNotBeRepresentedAsRecoveredOriginal"));
Add("IDEASUB-CT-05", "audit action names remain distinct from integration events",
    SA(provenance,"frozenAuditActionsAreNotIntegrationEvents").OrderBy(x=>x).SequenceEqual(new[]{"DynamicIdeaEvaluationPlanCreated","EvaluationRoutingRecovered","IdeaOwnerSubmissionFrozenAndRoutedToEvaluators"}.OrderBy(x=>x)));

Add("IDEASUB-CT-06", "command and authority are exact",
    S(command,"command") == "ideas.submit-g04"
    && S(command,"aggregateType") == "Idea"
    && S(command,"requiredRole") == "IDEA_OWNER"
    && B(command,"assignmentContextRequired")
    && B(command,"scopeRequired"));
Add("IDEASUB-CT-07", "allowed source states are exactly DRAFT and RETURNED",
    SA(command,"allowedSourceStates").OrderBy(x=>x).SequenceEqual(new[]{"DRAFT","RETURNED"}.OrderBy(x=>x)));
Add("IDEASUB-CT-08", "target state and version delta are exact",
    S(command,"targetState") == "UNDER_REVIEW" && command.GetProperty("versionDelta").GetInt32() == 1);
Add("IDEASUB-CT-09", "expectedVersion and idempotency are mandatory", B(command,"expectedVersionRequired") && B(command,"idempotencyKeyRequired"));
Add("IDEASUB-CT-10", "passport threshold is exactly 70 percent", pre.GetProperty("passportCompletionMinimumPercent").GetInt32() == 70);
Add("IDEASUB-CT-11", "strategy and primary objective are required", B(pre,"activeStrategyLinkRequired") && B(pre,"primaryObjectiveCodeRequired") && B(pre,"currentIdeaVersionRequired"));

Add("IDEASUB-CT-12", "server mutation is exact",
    B(mutation,"specialistReviewRequested")
    && S(mutation,"specialistReviewRequestedAt") == "SERVER_TIMESTAMP"
    && S(mutation,"state") == "UNDER_REVIEW"
    && S(mutation,"ideaVersion") == "BEFORE_PLUS_ONE");
Add("IDEASUB-CT-13", "evaluation plan is created and bound to resulting version",
    B(mutation,"evaluationPlanCreated")
    && B(mutation,"evaluationPlanBoundToResultingIdeaVersion")
    && S(mutation,"evaluationPlanInitialStatus") == "ACTIVE");
Add("IDEASUB-CT-14", "initial submission does not create G04 decision assessment", !B(mutation,"g04DecisionAssessmentCreatedByInitialSubmission"));

var mandatory = plan.GetProperty("mandatoryAssignments").EnumerateArray().ToArray();
Add("IDEASUB-CT-15", "Dynamic Evaluation Plan is G04 evaluation source of truth", B(plan,"sourceOfTruthForRequiredG04Evaluations"));
Add("IDEASUB-CT-16", "plan identities/version are server-authoritative",
    B(plan,"planIdGeneratedByServer") && B(plan,"planVersionGeneratedByServer") && B(plan,"ideaVersionMustEqualResultingIdeaVersion") && B(plan,"singleActivePlanPerIdeaVersion"));
Add("IDEASUB-CT-17", "mandatory evaluator roles are exact",
    mandatory.Length == 2
    && mandatory.All(x => B(x,"required") && S(x,"initialStatus") == "PENDING")
    && mandatory.Select(x=>S(x,"role")).OrderBy(x=>x).SequenceEqual(new[]{"IDEA_EVALUATOR","UNIT_OWNER_REVIEWER"}.OrderBy(x=>x)));
Add("IDEASUB-CT-18", "conditional roles are exact supported specialist set",
    SA(plan,"conditionalAssignmentRoles").OrderBy(x=>x).SequenceEqual(new[]{"FINANCIAL_ASSESSOR","HSE_ASSESSOR","IT_ASSESSOR","TECHNICAL_ASSESSOR"}.OrderBy(x=>x)));
Add("IDEASUB-CT-19", "conditional assignments remain configuration/profile-driven", B(plan,"conditionalAssignmentsAreProfileOrConfigurationDriven"));
Add("IDEASUB-CT-20", "zero estimated cost alone cannot suppress finance assessment", B(plan,"estimatedCostZeroAloneMayNotSuppressFinancialAssessment"));
Add("IDEASUB-CT-21", "assignment IDs are server-generated and distinct", B(plan,"assignmentIdsGeneratedByServer") && B(plan,"assignmentIdsDistinctWithinPlan"));

Add("IDEASUB-CT-22", "assignment context fields are exact",
    SA(assignment,"requiredFields").OrderBy(x=>x).SequenceEqual(new[]{"assignmentId","ideaId","ideaVersion","role","scope"}.OrderBy(x=>x)));
Add("IDEASUB-CT-23", "direct and stale evaluator entry fail closed",
    !B(assignment,"directGeneralFormEntryAllowed") && !B(assignment,"staleAssignmentAllowed"));
Add("IDEASUB-CT-24", "UI role visibility is not authorization", !B(assignment,"roleVisibilityIsAuthorization") && B(assignment,"serverMustValidateAssignmentPersonRoleScopeAndVersion"));
Add("IDEASUB-CT-25", "stale plan policy protects version consistency",
    B(stale,"ideaVersionMismatchInvalidatesActivePlan") && !B(stale,"stalePlanMayNotAcceptCompletion") && B(stale,"supersedeOrControlledRepairRequired") && B(stale,"historicalPlanAndAssignmentEvidencePreserved"));

Add("IDEASUB-CT-26", "G04 handoff waits for all required assignments",
    !B(handoff,"initialSubmissionCreatesG04DecisionAssessment") && B(handoff,"allRequiredAssignmentsMustBeCompleted") && B(handoff,"currentIdeaVersionMustMatchPlanVersion"));
Add("IDEASUB-CT-27", "G04 assessment authority and snapshot are exact",
    S(handoff,"decisionAssessmentStatus") == "PENDING"
    && S(handoff,"decisionAssessmentAssignedRole") == "IDEA_DECISION"
    && B(handoff,"evaluationPlanSnapshotRequired")
    && B(handoff,"currentIdeaVersionSnapshotRequired"));
Add("IDEASUB-CT-28", "committee vote and final decision authorities remain separate", B(handoff,"committeeVoteAuthoritySeparate") && B(handoff,"finalDecisionAuthoritySeparate"));

var pattern = S(naming.GetProperty("namingStandard"),"pattern");
Add("IDEASUB-CT-29", "new event is explicit post-freeze identity",
    S(evt,"eventType") == "IdeaSubmittedForEvaluation.v1"
    && S(evt,"identityClass") == "POST_FREEZE_ARCHITECTURE_DECISION"
    && !B(evt,"historicalOriginalIdentityRecovered"));
Add("IDEASUB-CT-30", "event follows approved entity-past-tense naming style",
    pattern == "<Entity><PastTenseAction>.v<Major>"
    && Regex.IsMatch(S(evt,"eventType"), "^[A-Z][A-Za-z0-9]+[A-Z][A-Za-z0-9]+\\.v[1-9][0-9]*$"));
Add("IDEASUB-CT-31", "event producer/delivery are exact",
    S(evt,"producerCommand") == "ideas.submit-g04"
    && S(evt,"emitTiming") == "AFTER_ATOMIC_COMMIT_VIA_OUTBOX"
    && S(evt,"delivery") == "AT_LEAST_ONCE"
    && S(evt,"consumerRequirement") == "IDEMPOTENT_BY_EVENT_ID");
Add("IDEASUB-CT-32", "event payload is minimized to plan/routing references",
    SA(evt,"requiredPayloadFields").OrderBy(x=>x).SequenceEqual(new[]{"evaluationPlanId","evaluationPlanVersion","ideaId","ideaVersion","requiredAssignments"}.OrderBy(x=>x))
    && new[]{"fullIdeaPassport","fullNeedDossier","attachmentContent","financialNarrative","personalNotes"}.All(SA(evt,"forbiddenPayloadFields").Contains));

Add("IDEASUB-CT-33", "atomic boundary includes Idea Plan Assignments Audit Outbox Idempotency",
    SA(atomic,"sameTransaction").OrderBy(x=>x).SequenceEqual(new[]{"Audit","DynamicEvaluationPlan","EvaluationAssignments","IdeaStateAndVersion","Idempotency","Outbox"}.OrderBy(x=>x))
    && !B(atomic,"partialCommitAllowed") && !B(atomic,"eventBeforeCommitAllowed") && !B(atomic,"planWithoutMatchingIdeaVersionAllowed"));
Add("IDEASUB-CT-34", "client cannot control authoritative state plan assignment or event fields",
    !B(client,"clientMaySetIdeaTargetState")
    && !B(client,"clientMaySetResultingIdeaVersion")
    && !B(client,"clientMaySetPlanId")
    && !B(client,"clientMaySetAssignmentIds")
    && !B(client,"clientMaySetAssignmentRequiredFlag")
    && !B(client,"clientMaySetAssignmentRoleWithoutServerPlanRules")
    && !B(client,"clientMaySetIntegrationEventType")
    && !B(client,"clientMayBypassAssignmentContext"));
Add("IDEASUB-CT-35", "ACR alone cannot promote command and Wave5 related-entity model is required",
    B(p1,"eventIdentityResolvedByThisAcr") && !B(p1,"commandPromotionAllowedByThisAcrAlone") && B(p1,"relatedEntityTransactionModelRequired"));
Add("IDEASUB-CT-36", "non-effects preserve P5 environment and frozen product boundaries",
    SA(acr,"nonEffects").Contains("Does not activate P5 command gateway")
    && SA(acr,"nonEffects").Contains("Does not claim physical Oracle readiness")
    && SA(acr,"nonEffects").Contains("Does not claim live Windows Domain readiness")
    && SA(acr,"nonEffects").Contains("Does not claim Network Pilot readiness")
    && SA(acr,"nonEffects").Contains("Does not modify v6.360"));

foreach (var r in results)
    Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Id} {r.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;
