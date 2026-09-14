using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 3 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.IdeaSubmission.ContractTests <ACR-P0-004-json> <ACR-P0-001-json> <P0-wave1-json>");
    return 2;
}

using var acr4Doc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var acr1Doc = JsonDocument.Parse(File.ReadAllText(args[1]));
using var p0Doc = JsonDocument.Parse(File.ReadAllText(args[2]));
var acr4 = acr4Doc.RootElement;
var acr1 = acr1Doc.RootElement;
var p0 = p0Doc.RootElement;

var command = acr4.GetProperty("command");
var prerequisites = command.GetProperty("serverPrerequisites");
var mutation = acr4.GetProperty("atomicMutation");
var ideaMutation = mutation.GetProperty("idea");
var planMutation = mutation.GetProperty("dynamicEvaluationPlan");
var assignmentMutation = mutation.GetProperty("evaluationAssignments");
var assessmentMutation = mutation.GetProperty("g04DecisionAssessment");
var plan = acr4.GetProperty("evaluationPlanContract");
var context = plan.GetProperty("assignmentContext");
var coherence = plan.GetProperty("versionCoherence");
var completion = plan.GetProperty("completionRule");
var evt = acr4.GetProperty("eventContract");
var atomic = acr4.GetProperty("atomicPersistenceBoundary");
var security = acr4.GetProperty("securityAndAuthority");
var downstream = acr4.GetProperty("downstreamSeparation");
var gate = acr4.GetProperty("p1ConsumptionGate");

var p0Commands = p0.GetProperty("commands").EnumerateArray().ToArray();
var p0IdeaCommand = p0Commands.Single(x => S(x, "code") == "ideas.submit-g04");
var p0Transitions = p0.GetProperty("transitions").EnumerateArray().ToArray();
var p0Submit = p0Transitions.Single(x => S(x, "id") == "G04-SUBMIT");
var p0SideEffects = p0Submit.GetProperty("sideEffects").EnumerateArray().ToArray();

var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));

Add("I04-CT-01", "ACR identity schema and status are exact",
    S(acr4,"acrId") == "ACR-P0-004"
    && S(acr4,"schema") == "EIMS-ACR-IDEA-SUBMISSION-EVALUATION-PLAN-1.0"
    && S(acr4,"status") == "APPROVED_FOR_P1_IMPLEMENTATION");
Add("I04-CT-02", "decision is explicitly post-freeze",
    S(acr4,"decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION");
Add("I04-CT-03", "frozen v6.360 identity and hash are preserved",
    S(acr4.GetProperty("frozenProduct"),"file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html"
    && S(acr4.GetProperty("frozenProduct"),"sha256") == "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a"
    && !B(acr4.GetProperty("frozenProduct"),"modifiedByThisDecision"));
Add("I04-CT-04", "historical original transition/event identities are not falsely claimed recovered",
    S(acr4.GetProperty("provenance"),"historicalOriginalTransitionCatalogStatus") == "PARTIAL_RECOVERY_ONLY"
    && S(acr4.GetProperty("provenance"),"historicalOriginalEventIdentityStatus") == "TBD_OR_UNAVAILABLE"
    && B(acr4.GetProperty("provenance"),"mustNotBeRepresentedAsRecoveredOriginal"));
Add("I04-CT-05", "frozen audit labels are not promoted to domain events",
    new[] { "DynamicIdeaEvaluationPlanCreated","IdeaOwnerSubmissionFrozenAndRoutedToEvaluators","IdeaSubmittedForAssessment","AssessmentCreated" }
        .All(Strings(acr4.GetProperty("provenance"),"auditLabelsNotPromotedToDomainEvents").Contains));

Add("I04-CT-06", "P0 recovered command identity and IDEA_OWNER authority remain aligned",
    S(p0IdeaCommand,"requiredRole") == "IDEA_OWNER"
    && S(command,"commandName") == "ideas.submit-g04"
    && S(command,"requiredRole") == "IDEA_OWNER");
Add("I04-CT-07", "allowed source states are exactly DRAFT and RETURNED",
    SetEquals(Strings(command,"allowedSourceStates"), new[] { "DRAFT","RETURNED" }));
Add("I04-CT-08", "target state and version increment are exact",
    S(command,"targetState") == "UNDER_REVIEW" && I(command,"versionIncrement") == 1 && B(command,"expectedVersionRequired"));
Add("I04-CT-09", "P0 frozen transition still agrees on DRAFT RETURNED to UNDER_REVIEW",
    SetEquals(Strings(p0Submit,"fromStates"), new[] { "DRAFT","RETURNED" }) && S(p0Submit,"toState") == "UNDER_REVIEW");
Add("I04-CT-10", "submission prerequisites enforce passport strategy objective and optimistic version",
    I(prerequisites,"passportCompletionPercentGte") == 70
    && B(prerequisites,"activeStrategyLinkRequired")
    && B(prerequisites,"primaryObjectiveReadyRequired")
    && B(prerequisites,"currentExpectedVersionMustMatch"));
Add("I04-CT-11", "authority context requires person assignment role and scope",
    new[] { "personId","assignmentId","role","scope" }.All(Strings(command,"requiredAuthorityContext").Contains));

Add("I04-CT-12", "Idea mutation is server-stamped UNDER_REVIEW and increments once",
    S(ideaMutation,"stateAfter") == "UNDER_REVIEW"
    && I(ideaMutation,"versionIncrement") == 1
    && S(ideaMutation,"submissionMarker") == "SUBMITTED_FOR_EVALUATION"
    && B(ideaMutation,"submittedAtUtcServerStamped"));
Add("I04-CT-13", "one active Dynamic Evaluation Plan is created and bound to resulting Idea version",
    B(planMutation,"create")
    && S(planMutation,"initialState") == "ACTIVE"
    && I(planMutation,"initialVersion") == 1
    && B(planMutation,"bindToResultingIdeaVersion")
    && B(planMutation,"sourceOfTruthForRequiredEvaluationCompletion")
    && B(planMutation,"serverGeneratedIdentity"));
Add("I04-CT-14", "evaluation assignments use distinct server identities and exact version/plan/role/scope binding",
    B(assignmentMutation,"createDistinctAssignmentIds")
    && B(assignmentMutation,"serverGeneratedIdentities")
    && B(assignmentMutation,"bindToIdeaId")
    && B(assignmentMutation,"bindToResultingIdeaVersion")
    && B(assignmentMutation,"bindToEvaluationPlanId")
    && B(assignmentMutation,"bindToRoleAndScope")
    && S(assignmentMutation,"initialState") == "PENDING");
Add("I04-CT-15", "required/optional classification is server controlled",
    B(assignmentMutation,"requiredOptionalClassificationServerControlled"));
Add("I04-CT-16", "initial submission does not create G04 decision assessment",
    !B(assessmentMutation,"createOnInitialSubmit")
    && B(assessmentMutation,"createOnlyAfterAllRequiredEvaluationAssignmentsComplete"));
Add("I04-CT-17", "historical P0 assessment side effect is explicitly distinguished rather than silently rewritten",
    p0SideEffects.Any(x => S(x,"aggregate") == "G04Assessment" && B(x,"create"))
    && S(acr4.GetProperty("provenance"),"historicalRecoveryNote").Contains("does not rewrite", StringComparison.OrdinalIgnoreCase));

Add("I04-CT-18", "Dynamic Evaluation Plan is authoritative for required evaluations",
    B(plan,"planIsAuthorityForRequiredEvaluations"));
Add("I04-CT-19", "mandatory evaluator roles are exactly IDEA_EVALUATOR and UNIT_OWNER_REVIEWER",
    SetEquals(Strings(plan,"mandatoryRoles"), new[] { "IDEA_EVALUATOR","UNIT_OWNER_REVIEWER" }));
Add("I04-CT-20", "specialist assessor roles are candidates only and profile/config driven",
    SetEquals(Strings(plan,"optionalRoleCandidates"), new[] { "TECHNICAL_ASSESSOR","HSE_ASSESSOR","FINANCIAL_ASSESSOR","IT_ASSESSOR" })
    && S(plan,"optionalRoleActivation") == "PROFILE_OR_CONFIGURATION_DRIVEN_ONLY"
    && !B(plan,"optionalRolesGloballyHardCodedAsRequired"));
Add("I04-CT-21", "evaluator mission requires exact assignment idea version role scope context",
    new[] { "assignmentId","ideaId","ideaVersion","role","scope" }.All(Strings(context,"requiredFields").Contains)
    && B(context,"serverMustValidateExactAssignmentContext"));
Add("I04-CT-22", "role visibility alone never authorizes evaluator work and direct/stale links fail closed",
    !B(context,"roleVisibilityAloneAuthorizesWork") && !B(context,"directGeneralOrStaleLinkAllowed"));
Add("I04-CT-23", "plan and assignments are exact-version bound and stale completion is denied",
    B(coherence,"planBoundToExactIdeaVersion")
    && B(coherence,"assignmentBoundToExactIdeaVersion")
    && S(coherence,"currentIdeaVersionMismatchAction") == "SUPERSEDE_OR_INVALIDATE_ACTIVE_PLAN_AND_ASSIGNMENTS"
    && !B(coherence,"staleAssignmentCompletionAllowed")
    && !B(coherence,"silentReuseAcrossIdeaVersionsAllowed"));
Add("I04-CT-24", "all required assignments must complete before G04 decision assessment",
    B(completion,"allRequiredAssignmentsMustComplete")
    && B(completion,"optionalAssignmentsDoNotBecomeMandatoryUnlessActivatedByPlan")
    && B(completion,"g04DecisionAssessmentCreatedOnlyAfterRequiredCompletion"));

var naming = acr1.GetProperty("namingStandard");
Add("I04-CT-25", "new event identity follows approved versioned domain-event style",
    S(evt,"eventType") == "IdeaSubmittedForEvaluation.v1"
    && Regex.IsMatch(S(evt,"eventType"), "^[A-Z][A-Za-z0-9]+\\.v[1-9][0-9]*$")
    && S(naming,"pattern") == "<Entity><PastTenseAction>.v<Major>"
    && S(evt,"eventCategory") == "DOMAIN");
Add("I04-CT-26", "event is explicitly a new post-freeze identity not a recovered original",
    S(evt,"identityDecisionClass") == "POST_FREEZE_NEW_IDENTITY" && !B(evt,"historicalOriginalIdentityClaimed"));
Add("I04-CT-27", "event emits only after atomic commit through Outbox and preserves delivery semantics",
    S(evt,"emitTiming") == "AFTER_ATOMIC_COMMIT_VIA_OUTBOX"
    && S(evt,"delivery") == S(naming,"delivery")
    && S(evt,"consumerIdempotency") == "EVENT_ID");
Add("I04-CT-28", "event payload carries only Idea Plan and required-assignment identifiers/roles",
    new[] { "ideaId","ideaVersion","evaluationPlanId","evaluationPlanVersion","requiredAssignmentIds","requiredAssignmentRoles" }
        .All(Strings(evt,"requiredPayloadFields").Contains));
Add("I04-CT-29", "event payload forbids dossier passport attachments answers financial detail and strategy body",
    new[] { "ideaPassport","ideaDossier","attachments","evaluationAnswers","financialDetails","strategyDocumentBody" }
        .All(Strings(evt,"forbiddenPayloadFields").Contains));

Add("I04-CT-30", "atomic transaction includes Idea Plan Assignments Audit Outbox and Idempotency",
    new[] { "IdeaStateAndVersion","DynamicEvaluationPlan","EvaluationAssignments","Audit","Outbox","Idempotency" }
        .All(Strings(atomic,"requiredComponents").Contains)
    && B(atomic,"singleTransactionRequired")
    && B(atomic,"outboxPublishedOnlyAfterCommit")
    && B(atomic,"rollbackAllOnAnyFailure"));
Add("I04-CT-31", "client cannot choose authoritative submission/plan/assignment/routing/event fields",
    !B(security,"clientMaySetState")
    && !B(security,"clientMaySetVersion")
    && !B(security,"clientMaySetEvaluationPlanIdentity")
    && !B(security,"clientMaySetAssignmentIdentities")
    && !B(security,"clientMaySetAssignmentRoles")
    && !B(security,"clientMaySetRequiredOptionalClassification")
    && !B(security,"clientMaySetRouting")
    && !B(security,"clientMaySetEventType")
    && !B(security,"clientMayBypassAssignmentContext"));
Add("I04-CT-32", "server Person Assignment Role Scope remains authorization boundary",
    B(security,"authorizationStillRequiresServerPersonRoleScopeAssignment")
    && B(security,"assignmentScopeMustMatchAuthoritativeIdeaScope"));
Add("I04-CT-33", "committee vote and final IDEA_DECISION remain separate downstream authorities",
    !B(downstream,"initialSubmissionCreatesCommitteeVote")
    && !B(downstream,"initialSubmissionCreatesFinalDecision")
    && S(downstream,"committeeVotingCommand") == "g04.vote"
    && S(downstream,"committeeVotingRole") == "G04_COMMITTEE_MEMBER"
    && S(downstream,"finalDecisionCommand") == "g04.final-decision"
    && S(downstream,"finalDecisionRole") == "IDEA_DECISION"
    && B(downstream,"committeeVoteAndFinalDecisionAreSeparate"));
Add("I04-CT-34", "ACR cannot promote Product command by itself and preserves P5/environment boundaries",
    B(gate,"submissionAndPlanContractDefined")
    && !B(gate,"commandPromotionAllowedByThisAcrAlone")
    && new[] { "ideaSubmitRuleEvaluator","relatedEntityMutationPlanner","evaluationPlanAndAssignmentPersistenceContract","atomicRelatedEntityPersistenceTests","assignmentContextAuthorizationTests","staleVersionInvalidationTests","eventBindingTests","serverAuthorizationTests","P5CompositionDecision" }
        .All(Strings(gate,"stillRequiredBeforeRuntimePromotion").Contains)
    && Strings(acr4,"nonEffects").Contains("Does not bind P5 to P1")
    && Strings(acr4,"nonEffects").Contains("Does not modify v6.360"));

foreach (var result in results)
    Console.WriteLine($"{(result.Pass ? "PASS" : "FAIL")} {result.Id} {result.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;

static string S(JsonElement e, string property) =>
    e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;
static bool B(JsonElement e, string property) => e.GetProperty(property).GetBoolean();
static int I(JsonElement e, string property) => e.GetProperty(property).GetInt32();
static HashSet<string> Strings(JsonElement e, string property) =>
    e.GetProperty(property).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
static bool SetEquals(HashSet<string> actual, IEnumerable<string> expected) => actual.SetEquals(expected);
