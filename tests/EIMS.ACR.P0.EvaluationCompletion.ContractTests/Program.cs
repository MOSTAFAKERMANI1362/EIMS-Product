using System.Text.Json;

if (args.Length != 3 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.EvaluationCompletion.ContractTests <ACR-P0-005-json> <ACR-P0-004-json> <P0-recovery-json>");
    return 2;
}

using var acr5Doc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var acr4Doc = JsonDocument.Parse(File.ReadAllText(args[1]));
using var p0Doc = JsonDocument.Parse(File.ReadAllText(args[2]));
var acr5 = acr5Doc.RootElement;
var acr4 = acr4Doc.RootElement;
var p0 = p0Doc.RootElement;
var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));

Add("E05-CT-01", "ACR identity schema and status are exact",
    S(acr5,"acrId") == "ACR-P0-005"
    && S(acr5,"schema") == "EIMS-ACR-EVALUATION-ASSIGNMENT-COMPLETION-1.0"
    && S(acr5,"status") == "APPROVED_FOR_P1_IMPLEMENTATION");
Add("E05-CT-02", "decision is explicitly post-freeze",
    S(acr5,"decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION");
Add("E05-CT-03", "frozen v6.360 identity and hash are preserved",
    S(acr5.GetProperty("frozenProduct"),"file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html"
    && S(acr5.GetProperty("frozenProduct"),"sha256") == "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a"
    && !B(acr5.GetProperty("frozenProduct"),"modifiedByThisDecision"));
Add("E05-CT-04", "historical completion/event contracts are not falsely claimed recovered",
    S(acr5.GetProperty("provenance"),"historicalOriginalCompletionContractStatus") == "PARTIAL_RECOVERY_ONLY"
    && S(acr5.GetProperty("provenance"),"historicalOriginalEventIdentitiesStatus") == "TBD_OR_UNAVAILABLE"
    && B(acr5.GetProperty("provenance"),"mustNotBeRepresentedAsRecoveredOriginal"));

var p0Command = p0.GetProperty("commandCatalog").GetProperty("commands").EnumerateArray()
    .Single(x => S(x,"id") == "evaluation-assignments.complete");
var command = acr5.GetProperty("command");
Add("E05-CT-05", "P0 command identity and role token are preserved",
    S(command,"commandName") == "evaluation-assignments.complete"
    && S(command,"recoveredRequiredRoleToken") == "MATCH_ASSIGNMENT_ROLE"
    && S(p0Command,"requiredRole") == "MATCH_ASSIGNMENT_ROLE"
    && S(p0Command,"targetAggregate") == "EvaluationAssignment");
Add("E05-CT-06", "P0 remains partial and non-executable before this post-freeze decision is implemented",
    S(p0Command,"recoveryStatus") == "PARTIAL_EVIDENCE" && !B(p0Command,"executable"));

var terminology = acr5.GetProperty("terminology");
Add("E05-CT-07", "authority assignment and evaluation assignment are distinct identities",
    terminology.TryGetProperty("authorityAssignmentId", out var authorityAssignment)
    && terminology.TryGetProperty("evaluationAssignmentId", out var evaluationAssignment)
    && !B(authorityAssignment,"maySubstituteForEvaluationAssignmentId")
    && !B(evaluationAssignment,"maySubstituteForAuthorityAssignmentId")
    && B(terminology,"bothRequiredForMutation"));
var authorityContext = Strings(command,"requiredAuthorityContext");
Add("E05-CT-08", "mutation context requires both assignment identities plus exact Idea Plan Role Scope",
    new[] { "personId","authorityAssignmentId","authorityRole","authorityScope","evaluationAssignmentId","ideaId","ideaVersion","evaluationPlanId","evaluationRole","evaluationScope" }
        .All(authorityContext.Contains));
Add("E05-CT-09", "role-only or general-page completion is forbidden",
    S(command,"requiredTargetIdentity") == "evaluationAssignmentId"
    && !B(command,"directRoleOnlyOrGeneralPageCompletionAllowed"));

var authorityRules = command.GetProperty("authorityRules");
Add("E05-CT-10", "server authority requires effective Person assignment with exact evaluator Role",
    B(authorityRules,"authorityAssignmentMustBeEffective")
    && B(authorityRules,"authorityAssignmentPersonMustEqualAuthenticatedPerson")
    && B(authorityRules,"authorityRoleMustEqualEvaluationRole")
    && !B(authorityRules,"roleVisibilityAloneAuthorizesCompletion"));
Add("E05-CT-11", "authority and evaluation scopes are constrained by authoritative Idea scope",
    B(authorityRules,"authorityScopeMustContainEvaluationScope")
    && B(authorityRules,"evaluationScopeMustEqualAuthoritativeIdeaScope"));

var coherence = acr5.GetProperty("missionCoherence");
Add("E05-CT-12", "only PENDING mission on ACTIVE plan may complete",
    S(coherence,"evaluationAssignmentRequiredState") == "PENDING"
    && S(coherence,"evaluationPlanRequiredState") == "ACTIVE");
Add("E05-CT-13", "mission must match Plan Idea and current exact Idea version",
    B(coherence,"evaluationAssignmentMustBelongToPlan")
    && B(coherence,"evaluationAssignmentMustMatchIdeaId")
    && B(coherence,"evaluationAssignmentMustMatchCurrentIdeaVersion")
    && B(coherence,"planMustMatchCurrentIdeaVersion"));
Add("E05-CT-14", "stale superseded cancelled and version-mismatched work fails closed",
    !B(coherence,"staleAssignmentCompletionAllowed")
    && !B(coherence,"supersededPlanCompletionAllowed")
    && !B(coherence,"cancelledPlanCompletionAllowed")
    && S(coherence,"versionMismatchAction") == "FAIL_CLOSED_BEFORE_MUTATION");

var validation = acr5.GetProperty("assessmentValidation");
Add("E05-CT-15", "role-specific versioned assessment schema and validator are mandatory",
    B(validation,"roleSpecificSchemaRequired")
    && B(validation,"assessmentSchemaVersionRequired")
    && B(validation,"validatorMustBeServerBound")
    && S(validation,"validatorRegistryKey") == "evaluationRole + assessmentSchemaVersion");
Add("E05-CT-16", "missing validator fails closed and generic runtime cannot invent answers",
    S(validation,"missingValidatorAction") == "FAIL_CLOSED"
    && !B(validation,"genericRuntimeMayInventMissingDomainAnswers")
    && B(validation,"requestPayloadValidatedBeforeCompletion"));
Add("E05-CT-17", "role question schemas remain a separate Wave 6 versioned contract",
    !B(validation,"roleSchemasDefinedByThisAcr")
    && S(validation,"roleSchemaRegistryStatus") == "SEPARATE_VERSIONED_CONTRACT_REQUIRED_FOR_WAVE6");

var completion = acr5.GetProperty("completionMutation");
var assignmentMutation = completion.GetProperty("evaluationAssignment");
Add("E05-CT-18", "assignment transition is exactly PENDING to COMPLETED with one version increment",
    S(assignmentMutation,"fromState") == "PENDING"
    && S(assignmentMutation,"toState") == "COMPLETED"
    && I(assignmentMutation,"versionIncrement") == 1);
Add("E05-CT-19", "completion actor and timestamp are server-derived",
    B(assignmentMutation,"completedAtUtcServerStamped")
    && B(assignmentMutation,"completedByPersonIdServerDerived")
    && B(assignmentMutation,"authorityAssignmentIdServerDerived"));
var snapshot = completion.GetProperty("assessmentSnapshot");
var snapshotLinks = Strings(snapshot,"requiredLinkage");
Add("E05-CT-20", "assessment snapshot is immutable append-only with exact evaluator and mission linkage",
    B(snapshot,"createImmutableSnapshot") && B(snapshot,"appendOnly") && !B(snapshot,"historicalSnapshotMayBeOverwritten")
    && new[] { "evaluationAssignmentId","evaluationPlanId","ideaId","ideaVersion","evaluationRole","assessmentSchemaVersion","evaluatorPersonId","authorityAssignmentId","occurredAtUtc","correlationId" }.All(snapshotLinks.Contains));

var idempotency = acr5.GetProperty("idempotency");
Add("E05-CT-21", "exact replay is idempotent with no duplicate evidence",
    B(idempotency,"exactReplayReturnsCommittedResultWithoutMutation")
    && !B(idempotency,"exactReplayCreatesDuplicateEvidence"));
Add("E05-CT-22", "different completion fingerprint conflicts and cannot overwrite completed mission",
    S(idempotency,"sameEvaluationAssignmentDifferentCompletionFingerprintAction") == "CONFLICT"
    && !B(idempotency,"completedAssignmentMayBeOverwritten"));

var readiness = acr5.GetProperty("planReadiness");
Add("E05-CT-23", "server recalculates readiness from Plan required assignments only",
    B(readiness,"serverRecalculatesAfterEveryCompletion")
    && S(readiness,"sourceOfTruth") == "DYNAMIC_EVALUATION_PLAN_REQUIRED_ASSIGNMENTS"
    && B(readiness,"requiredAssignmentsOnlyDetermineBlockingReadiness"));
Add("E05-CT-24", "unactivated optional role candidates never block readiness",
    B(readiness,"activatedOptionalAssignmentsBecomePlanAssignments")
    && !B(readiness,"unactivatedOptionalRoleCandidatesBlockReadiness"));
var incomplete = readiness.GetProperty("whenRequiredAssignmentsRemainIncomplete");
Add("E05-CT-25", "incomplete required work keeps Plan ACTIVE and creates no G04 assessment",
    S(incomplete,"planStateAfter") == "ACTIVE" && !B(incomplete,"createG04DecisionAssessment"));
var final = readiness.GetProperty("whenFinalRequiredAssignmentCompletes");
Add("E05-CT-26", "final required completion makes Plan ready and creates exactly one G04Assessment",
    S(final,"planStateAfter") == "READY_FOR_G04_DECISION"
    && B(final,"createG04DecisionAssessment") && B(final,"createExactlyOne")
    && S(final,"assessmentAggregateType") == "G04Assessment"
    && S(final,"assessmentInitialState") == "PENDING");
Add("E05-CT-27", "G04 assessment identity/linkage/snapshot are server controlled",
    B(final,"serverGeneratedAssessmentId")
    && B(final,"bindToIdeaId") && B(final,"bindToExactIdeaVersion")
    && B(final,"bindToEvaluationPlanId") && B(final,"freezePlanSnapshot"));

var acr4Plan = acr4.GetProperty("evaluationPlanContract");
Add("E05-CT-28", "ACR-P0-005 preserves ACR-P0-004 Plan source-of-truth and final-required readiness semantics",
    B(acr4Plan,"planIsAuthorityForRequiredEvaluations")
    && B(acr4Plan.GetProperty("completionRule"),"allRequiredAssignmentsMustComplete")
    && B(acr4Plan.GetProperty("completionRule"),"g04DecisionAssessmentCreatedOnlyAfterRequiredCompletion"));

var separation = acr5.GetProperty("downstreamSeparation");
Add("E05-CT-29", "completion does not create vote or final Idea decision",
    !B(separation,"completionCreatesCommitteeVote")
    && !B(separation,"completionCreatesFinalIdeaDecision")
    && S(separation,"committeeVotingCommand") == "g04.vote"
    && S(separation,"finalDecisionCommand") == "g04.final-decision"
    && B(separation,"committeeVoteAndFinalDecisionRemainSeparate"));

var events = acr5.GetProperty("eventContracts").EnumerateArray().ToArray();
Add("E05-CT-30", "exactly two post-freeze event identities are defined",
    events.Length == 2
    && events.Select(x => S(x,"eventType")).OrderBy(x => x).SequenceEqual(new[] { "EvaluationAssignmentCompleted.v1", "G04DecisionAssessmentCreated.v1" }));
var completedEvent = events.Single(x => S(x,"eventType") == "EvaluationAssignmentCompleted.v1");
var assessmentEvent = events.Single(x => S(x,"eventType") == "G04DecisionAssessmentCreated.v1");
Add("E05-CT-31", "completion event is always emitted and assessment-created event only on final required completion",
    B(completedEvent,"emittedOnEverySuccessfulCompletion")
    && B(assessmentEvent,"emittedOnlyWhenFinalRequiredCompletionCreatesAssessment"));
Add("E05-CT-32", "both events use Outbox at-least-once delivery and do not claim historical identity",
    events.All(e => S(e,"emitTiming") == "AFTER_ATOMIC_COMMIT_VIA_OUTBOX"
        && S(e,"delivery") == "AT_LEAST_ONCE"
        && S(e,"consumerIdempotency") == "EVENT_ID"
        && !B(e,"historicalOriginalIdentityClaimed")));
var forbidden = new[] { "assessmentAnswers","assessmentNarrative","attachments","financialDetails","technicalDetails","hseDetails","itDetails" };
Add("E05-CT-33", "integration events exclude assessment content and specialist detail",
    events.All(e => forbidden.All(Strings(e,"forbiddenPayloadFields").Contains)));

var atomic = acr5.GetProperty("atomicPersistenceBoundary");
var atomicAlways = Strings(atomic,"alwaysRequiredComponents");
var atomicFinal = Strings(atomic,"finalRequiredCompletionAdditionalComponents");
Add("E05-CT-34", "atomic boundary includes assignment snapshot plan audit outbox idempotency and final G04 assessment",
    new[] { "EvaluationAssignmentStateAndVersion","ImmutableAssessmentSnapshot","EvaluationPlanReadiness","Audit","Outbox","Idempotency" }.All(atomicAlways.Contains)
    && atomicFinal.Contains("G04DecisionAssessment")
    && B(atomic,"singleTransactionRequired") && B(atomic,"outboxPublishedOnlyAfterCommit") && B(atomic,"rollbackAllOnAnyFailure"));

var security = acr5.GetProperty("securityAndAuthority");
Add("E05-CT-35", "client cannot control authoritative authority mission state or event fields",
    new[] { "clientMaySetAuthorityAssignmentId","clientMaySetEvaluatorPersonId","clientMaySetEvaluationRole","clientMaySetEvaluationScope","clientMaySetIdeaVersion","clientMaySetPlanState","clientMaySetAssignmentState","clientMaySetG04AssessmentIdentity","clientMaySetEventType" }
        .All(key => !B(security,key))
    && B(security,"serverMustReauthorizeOnEveryCompletion")
    && B(security,"serverMustValidateExactMissionContext"));

var gate = acr5.GetProperty("p1ConsumptionGate");
var stillRequired = Strings(gate,"stillRequiredBeforeRuntimePromotion");
Add("E05-CT-36", "ACR defines envelope but cannot promote runtime by itself",
    B(gate,"lifecycleAndAuthorityEnvelopeDefined")
    && !B(gate,"commandPromotionAllowedByThisAcrAlone")
    && new[] { "roleSpecificAssessmentSchemaRegistry","roleSpecificAssessmentValidators","evaluationAssignmentRepositoryContract","assessmentSnapshotPersistenceContract","planReadinessRecalculationImplementation","g04AssessmentCreationPersistenceContract","multiEventOutboxContract","atomicRelatedEntityPersistenceTests","authorityAssignmentAndMissionContextTests","stalePlanAndVersionTests","idempotencyConflictTests","P5CompositionDecision" }.All(stillRequired.Contains));
var nonEffects = Strings(acr5,"nonEffects");
Add("E05-CT-37", "ACR leaves evaluator command unpromoted P5 closed and frozen product unchanged",
    nonEffects.Contains("Does not promote evaluation-assignments.complete into executable P1 runtime by itself")
    && nonEffects.Contains("Does not bind P5 to P1")
    && nonEffects.Contains("Does not modify v6.360"));
Add("E05-CT-38", "ACR makes no live Oracle Domain or Network Pilot readiness claim",
    nonEffects.Contains("Does not claim physical Oracle readiness")
    && nonEffects.Contains("Does not claim live Windows Domain readiness")
    && nonEffects.Contains("Does not claim Network Pilot readiness"));

foreach (var r in results)
    Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Id} {r.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;

static string S(JsonElement e, string p) =>
    e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
static int I(JsonElement e, string p) => e.GetProperty(p).GetInt32();
static HashSet<string> Strings(JsonElement e, string p) =>
    e.GetProperty(p).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
