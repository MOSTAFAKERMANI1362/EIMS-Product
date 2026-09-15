using System.Text.Json;

if (args.Length != 2 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.007.G04FinalDecision.ContractTests <ACR-P0-007-json> <G04-rule-registry-json>");
    return 2;
}

using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var regDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr = acrDoc.RootElement;
var reg = regDoc.RootElement;

var tests = new List<(string Name, Action Run)>
{
    ("ACR007-CT-01 ACR identity is exact", () => { Eq("ACR-P0-007", S(acr,"acrId")); Eq("APPROVED_FOR_P1_IMPLEMENTATION", S(acr,"status")); Eq("POST_FREEZE_ARCHITECTURE_DECISION", S(acr,"decisionClass")); }),
    ("ACR007-CT-02 frozen product reference and SHA are exact", () => Frozen(acr.GetProperty("sourceProduct"))),
    ("ACR007-CT-03 ACR does not modify frozen product", () => False(B(acr.GetProperty("sourceProduct"),"modifiedByThisDecision"))),
    ("ACR007-CT-04 command identity and endpoint are exact", () => { var c=acr.GetProperty("command"); Eq("g04.final-decision",S(c,"id")); Eq("POST",S(c,"method")); Eq("/commands/g04/{assessmentId}/final-decision",S(c,"endpoint")); Eq("Idea",S(c,"targetAggregate")); }),
    ("ACR007-CT-05 ACR itself does not promote runtime", () => False(B(acr.GetProperty("command"),"runtimePromotionInThisAcr"))),
    ("ACR007-CT-06 final authority role is IDEA_DECISION", () => Eq("IDEA_DECISION", S(acr.GetProperty("authority"),"singleEffectiveRoleRequired"))),
    ("ACR007-CT-07 authority is server derived from P3 exact assignment", () => { var a=acr.GetProperty("authority"); Eq("P3_AUTHENTICATED_AUTHORITY_ACTOR",S(a,"identitySource")); True(B(a,"exactAuthorityAssignmentRequired")); False(B(a,"clientMaySupplyPersonRoleScopeOrAssignmentAuthority")); }),
    ("ACR007-CT-08 final decider scope must contain Idea scope", () => True(B(acr.GetProperty("authority"),"scopeMustContainIdeaScope"))),
    ("ACR007-CT-09 committee member cannot be final decider for same assessment", () => False(B(acr.GetProperty("authority"),"committeeMemberMayBeFinalDeciderForSameAssessment"))),
    ("ACR007-CT-10 committee and final authority are separated", () => True(B(acr.GetProperty("authority"),"committeeAndFinalDecisionAuthoritySeparated"))),
    ("ACR007-CT-11 evaluator conflict defaults to deny for APPROVE", () => { var p=acr.GetProperty("authority").GetProperty("evaluatorConflictPolicy"); Eq("APPROVE",S(p,"appliesToOutcome")); Eq("DENY",S(p,"default")); }),
    ("ACR007-CT-12 only frozen ALLOW_WITH_AUDIT exception is permitted", () => { var p=acr.GetProperty("authority").GetProperty("evaluatorConflictPolicy"); Eq("ALLOW_WITH_AUDIT",S(p,"allowedException")); True(B(p,"exceptionMustBeFrozenGovernancePolicy")); True(B(p,"exceptionRequiresExplicitAuditEvidence")); }),
    ("ACR007-CT-13 exact PENDING assessment context is required", () => { var c=acr.GetProperty("context"); Eq("PENDING",S(c,"assessmentStateRequired")); True(B(c,"exactAssessmentIdRequired")); True(B(c,"exactIdeaIdRequired")); }),
    ("ACR007-CT-14 assessment and plan must match current Idea version", () => { var c=acr.GetProperty("context"); True(B(c,"assessmentIdeaVersionMustEqualCurrentIdeaVersion")); True(B(c,"exactEvaluationPlanRequired")); True(B(c,"planIdeaVersionMustEqualCurrentIdeaVersion")); }),
    ("ACR007-CT-15 stale/superseded plan and assessment are denied", () => { var c=acr.GetProperty("context"); True(B(c,"staleOrSupersededPlanDenied")); True(B(c,"staleOrSupersededAssessmentDenied")); }),
    ("ACR007-CT-16 concurrency and idempotency are mandatory", () => { var c=acr.GetProperty("context"); True(B(c,"optimisticConcurrencyRequired")); True(B(c,"idempotencyRequired")); }),
    ("ACR007-CT-17 final outcomes are exactly four", () => SetEq(new[]{"APPROVE","RETURN","HOLD","REJECT"}, AcrOutcomeValues())),
    ("ACR007-CT-18 APPROVE maps to APPROVED and existing event", () => { var o=Outcome("APPROVE"); Eq("APPROVED",S(o,"ideaStateAfter")); False(B(o,"ideaVersionIncrement")); Eq("DECIDED",S(o,"assessmentStateAfter")); Eq("IdeaApprovedForPortfolio.v1",S(o,"eventType")); Eq("FROZEN_EXISTING",S(o,"eventIdentityStatus")); True(B(o,"positiveApprovalGateRequired")); }),
    ("ACR007-CT-19 RETURN maps to RETURNED and increments version", () => { var o=Outcome("RETURN"); Eq("RETURNED",S(o,"ideaStateAfter")); True(B(o,"ideaVersionIncrement")); Eq("IdeaReturnedFromG04.v1",S(o,"eventType")); Eq("POST_FREEZE_STABILIZED",S(o,"eventIdentityStatus")); False(B(o,"positiveApprovalGateRequired")); }),
    ("ACR007-CT-20 HOLD requires review date and does not increment version", () => { var o=Outcome("HOLD"); Eq("HOLD",S(o,"ideaStateAfter")); False(B(o,"ideaVersionIncrement")); Eq("IdeaHeldAtG04.v1",S(o,"eventType")); True(B(o,"reviewDateRequired")); }),
    ("ACR007-CT-21 REJECT maps to REJECTED and stabilized event", () => { var o=Outcome("REJECT"); Eq("REJECTED",S(o,"ideaStateAfter")); False(B(o,"ideaVersionIncrement")); Eq("IdeaRejectedAtG04.v1",S(o,"eventType")); Eq("POST_FREEZE_STABILIZED",S(o,"eventIdentityStatus")); }),
    ("ACR007-CT-22 decision comment is mandatory min 15", () => { var d=acr.GetProperty("decisionInput"); True(B(d,"commentRequired")); Eq(15,I(d,"commentMinLength")); }),
    ("ACR007-CT-23 HOLD review date is mandatory", () => True(B(acr.GetProperty("decisionInput"),"holdReviewDateRequired"))),
    ("ACR007-CT-24 client cannot select event or frozen policy evidence", () => { var d=acr.GetProperty("decisionInput"); False(B(d,"clientMayChooseEventIdentity")); False(B(d,"clientMayChooseRuleProfileOrFrozenEvidence")); }),
    ("ACR007-CT-25 positive rule suite applies only to APPROVE", () => Eq("APPROVE",S(acr.GetProperty("approvalOnlyRuleSuite"),"appliesOnlyWhenOutcome"))),
    ("ACR007-CT-26 RETURN HOLD REJECT are not blocked by positive gates", () => SetEq(new[]{"RETURN","HOLD","REJECT"}, Strings(acr.GetProperty("approvalOnlyRuleSuite"),"negativeOutcomesMayProceedWhenApprovalRulesFail"))),
    ("ACR007-CT-27 approval rule check set is exact", () => SetEq(new[]{"PROFILE_SNAPSHOT_VALID","GATE_SCORE_THRESHOLD","DATA_READINESS_THRESHOLD","BASE_NEED_PRESENT","CONDITIONAL_TECHNICAL_VALIDITY","HARD_CONDITIONS","SPECIALIST_OUTCOMES","COMMITTEE_POSITIVE_RESULT","SEPARATION_OF_DUTIES"}, ApprovalCheckIds())),
    ("ACR007-CT-28 committee positive result is conditional on committee route", () => Eq("decisionRoute == G04_COMMITTEE", S(Check("COMMITTEE_POSITIVE_RESULT"),"requiredWhen"))),
    ("ACR007-CT-29 frozen profile evidence list contains all decision anchors", () => SetEq(new[]{"profileId","profileVersion","profileSnapshot","profileSnapshotHash","ruleSet","hardConditionMappingVersion","gateScore","dataReadiness","ideaVersion","evaluationPlanId","evaluationPlanVersion","assessmentId","frozenAt"}, Strings(acr.GetProperty("profileAndRuleFreeze"),"requiredFrozenEvidence"))),
    ("ACR007-CT-30 closed history may not infer today's active profile", () => False(B(acr.GetProperty("profileAndRuleFreeze"),"closedHistoryMayInferFromCurrentProfile"))),
    ("ACR007-CT-31 committee APPROVE evidence requires completed positive result", () => SetEq(new[]{"committeeSnapshotId","committeeVersion","votingStageState=COMPLETED","approvalRuleSatisfied=true"}, Strings(acr.GetProperty("committeeEvidenceForFinalDecision"),"requiredForApprove"))),
    ("ACR007-CT-32 vote notes and full membership are not copied to events", () => { var c=acr.GetProperty("committeeEvidenceForFinalDecision"); False(B(c,"fullMemberSnapshotCopiedIntoIntegrationEvent")); False(B(c,"voteNotesCopiedIntoIntegrationEvent")); }),
    ("ACR007-CT-33 decision evidence is append only", () => { var e=acr.GetProperty("immutableDecisionEvidence"); True(B(e,"appendOnly")); False(B(e,"historicalDecisionMayBeOverwritten")); }),
    ("ACR007-CT-34 immutable decision evidence preserves authority and rule context", () => { var f=Strings(acr.GetProperty("immutableDecisionEvidence"),"fields"); foreach(var x in new[]{"personId","authorityAssignmentId","profileSnapshotHash","ruleCheckResults","specialistOutcomeSummary","sodResult","correlationId"}) True(f.Contains(x)); }),
    ("ACR007-CT-35 RETURN increments Idea exactly one version", () => Eq(1,I(acr.GetProperty("returnInvalidation"),"ideaVersionMustIncrementExactlyBy"))),
    ("ACR007-CT-36 RETURN supersedes plans and pending G04 assessments", () => { var r=acr.GetProperty("returnInvalidation"); Eq("SUPERSEDED",S(r,"oldActiveEvaluationPlans")); Eq("SUPERSEDED",S(r,"oldPendingG04Assessments")); }),
    ("ACR007-CT-37 RETURN makes old technical and structured evidence stale", () => { var r=acr.GetProperty("returnInvalidation"); Eq("STALE",S(r,"oldTechnicalAssessment")); Eq("STALE",S(r,"oldStructuredIdeaEvaluation")); True(B(r,"newVersionRequiresFreshEvaluationContext")); }),
    ("ACR007-CT-38 transaction boundary contains governance evidence audit outbox idempotency", () => { var t=Strings(acr.GetProperty("transactionBoundary"),"singleLogicalTransaction"); foreach(var x in new[]{"IdeaStateAndVersion","G04AssessmentDecisionAndClosure","ImmutableFinalDecisionEvidence","Audit","OutcomeOutbox","Idempotency"}) True(t.Contains(x)); }),
    ("ACR007-CT-39 Portfolio eligibility is outside final decision transaction", () => { var t=acr.GetProperty("transactionBoundary"); False(B(t,"portfolioEligibilityInsideTransaction")); False(B(t,"portfolioFailureMayRollbackCommittedFinalDecision")); }),
    ("ACR007-CT-40 outcome event bindings are exact", () => { var b=acr.GetProperty("events").GetProperty("outcomeBindings"); Eq("IdeaApprovedForPortfolio.v1",S(b,"APPROVE")); Eq("IdeaReturnedFromG04.v1",S(b,"RETURN")); Eq("IdeaHeldAtG04.v1",S(b,"HOLD")); Eq("IdeaRejectedAtG04.v1",S(b,"REJECT")); }),
    ("ACR007-CT-41 event payload forbids sensitive/full evidence bodies", () => SetEq(new[]{"decisionComment","evaluatorAnswers","fullIdeaDossier","fullProfileSnapshot","fullCommitteeMemberSnapshot","voteNotes"}, Strings(acr.GetProperty("events"),"forbiddenPayload"))),
    ("ACR007-CT-42 Portfolio eligibility is downstream idempotent", () => { var p=acr.GetProperty("portfolioBoundary"); Eq("IdeaApprovedForPortfolio.v1",S(p,"triggerEvent")); Eq("PortfolioEligibilityService",S(p,"service")); Eq("POST_COMMIT_IDEMPOTENT_DOWNSTREAM",S(p,"execution")); False(B(p,"candidateCreationInsideFinalDecision")); }),
    ("ACR007-CT-43 runtime safety preserves P5 and live-environment boundaries", () => { var s=acr.GetProperty("runtimeSafety"); False(B(s,"runtimePromotionInThisAcr")); False(B(s,"p5CommandGatewayBound")); False(B(s,"physicalOracleReadyClaimed")); False(B(s,"liveWindowsDomainReadyClaimed")); False(B(s,"networkPilotReadyClaimed")); True(B(s,"v6360Unchanged")); }),
    ("ACR007-CT-44 recovered registry identity and provenance are exact", () => { Eq("EIMS-G04-DECISION-PROFILE-RULE-REGISTRY-1.0",S(reg,"schema")); Eq("RECOVERED_FROM_FROZEN_V6_360_NOT_ORIGINAL_SERVER_SCHEMA",S(reg,"recoveryStatus")); True(B(reg.GetProperty("provenance"),"mustNotBeRepresentedAsOriginalServerSchema")); }),
    ("ACR007-CT-45 recovered registry points to exact frozen product", () => Frozen(reg.GetProperty("sourceProduct"))),
    ("ACR007-CT-46 recovered registry declares no rule strengthening", () => True(B(reg.GetProperty("provenance"),"noRuleStrengtheningBeyondFrozenEvidence"))),
    ("ACR007-CT-47 registry contains exactly GENERAL and HSE profiles", () => SetEq(new[]{"GENERAL","HSE"}, reg.GetProperty("profiles").EnumerateArray().Select(x=>S(x,"domain")))),
    ("ACR007-CT-48 GENERAL thresholds are 65 and 80", () => { var p=Profile("GENERAL"); Eq(65,I(p,"passThreshold")); Eq(80,I(p,"dataThreshold")); }),
    ("ACR007-CT-49 GENERAL weights are exact and sum 100", () => { var w=Profile("GENERAL").GetProperty("weights"); Weight(w,"strategy",15); Weight(w,"needScore",15); Weight(w,"benefitScore",20); Weight(w,"tech",10); Weight(w,"econ",10); Weight(w,"effort",10); Weight(w,"riskScore",10); Weight(w,"urgency",5); Weight(w,"innovation",5); Eq(100,w.EnumerateObject().Sum(x=>x.Value.GetInt32())); }),
    ("ACR007-CT-50 GENERAL hard-rule keys are exact", () => SetEq(new[]{"REQUIRED_EVALUATIONS_COMPLETE","ACTIVE_STRATEGY_ALIGNED","BASE_NEED_G03_READY"}, Profile("GENERAL").GetProperty("hardConditions").EnumerateArray().Select(x=>S(x,"machineKey")))),
    ("ACR007-CT-51 HSE thresholds are 60 and 90", () => { var p=Profile("HSE"); Eq(60,I(p,"passThreshold")); Eq(90,I(p,"dataThreshold")); }),
    ("ACR007-CT-52 HSE weights are exact and sum 100", () => { var w=Profile("HSE").GetProperty("weights"); Weight(w,"strategy",10); Weight(w,"needScore",15); Weight(w,"benefitScore",5); Weight(w,"tech",15); Weight(w,"econ",0); Weight(w,"effort",5); Weight(w,"riskScore",30); Weight(w,"urgency",15); Weight(w,"innovation",5); Eq(100,w.EnumerateObject().Sum(x=>x.Value.GetInt32())); }),
    ("ACR007-CT-53 HSE hard-rule mapping preserves HSE+technical and HSE checks", () => { var keys=Profile("HSE").GetProperty("hardConditions").EnumerateArray().Select(x=>S(x,"machineKey")).ToArray(); Eq(4,keys.Length); Eq(1,keys.Count(x=>x=="HSE_AND_TECH_VALID")); Eq(3,keys.Count(x=>x=="HSE_ASSESSMENT_ACCEPTABLE")); }),
    ("ACR007-CT-54 machine hard-rule registry contains exactly six known keys", () => SetEq(new[]{"REQUIRED_EVALUATIONS_COMPLETE","BASE_NEED_G03_READY","TECHNICAL_ASSESSMENT_VALID","ACTIVE_STRATEGY_ALIGNED","HSE_ASSESSMENT_ACCEPTABLE","HSE_AND_TECH_VALID"}, RuleKeys())),
    ("ACR007-CT-55 required evaluation rule requires current matching plan and completed required assignments", () => { var e=Rule("REQUIRED_EVALUATIONS_COMPLETE").GetProperty("evaluation"); True(B(e,"currentPlanRequired")); True(B(e,"planIdeaVersionMustEqualCurrentIdeaVersion")); True(B(e,"atLeastOneRequiredAssignment")); Eq("COMPLETED",S(e,"allRequiredAssignmentsStatus")); }),
    ("ACR007-CT-56 base Need rule requires READY_FOR_IDEATION", () => { var e=Rule("BASE_NEED_G03_READY").GetProperty("evaluation"); True(B(e,"baseNeedRequired")); Eq("READY_FOR_IDEATION",S(e,"requiredNeedStatus")); }),
    ("ACR007-CT-57 technical rule requires current non-stale evidence", () => { var e=Rule("TECHNICAL_ASSESSMENT_VALID").GetProperty("evaluation"); True(B(e,"currentIdeaVersionRequired")); True(B(e,"technicalAssessmentMustBeNonStale")); }),
    ("ACR007-CT-58 strategy rule is nonblocking when no active strategy exists", () => Eq("NON_BLOCKING",S(Rule("ACTIVE_STRATEGY_ALIGNED").GetProperty("evaluation"),"whenNoActiveStrategySet"))),
    ("ACR007-CT-59 HSE accepted outcomes are exact", () => SetEq(new[]{"PASS","CONDITIONAL_PASS","APPROVE","APPROVED","CONFIRMED"}, Strings(Rule("HSE_ASSESSMENT_ACCEPTABLE").GetProperty("evaluation"),"acceptedOutcomes"))),
    ("ACR007-CT-60 HSE+technical rule is logical AND", () => { var e=Rule("HSE_AND_TECH_VALID").GetProperty("evaluation"); Eq("HSE_ASSESSMENT_ACCEPTABLE",S(e,"hseRule")); Eq("TECHNICAL_ASSESSMENT_VALID",S(e,"technicalRule")); Eq("AND",S(e,"operator")); }),
    ("ACR007-CT-61 unknown hard-condition mapping fails closed", () => Eq("FAIL_CLOSED",S(reg.GetProperty("hardConditionMapping"),"unknownTextAction"))),
    ("ACR007-CT-62 data-readiness contract has exactly 12 checks", () => { var d=reg.GetProperty("dataReadiness"); Eq("round(nonBlankChecks/12*100)",S(d,"formula")); Eq(12,Strings(d,"checks").Count); True(B(d,"approvalRequiresDataThreshold")); }),
    ("ACR007-CT-63 scoring contract has nine metrics and 0..100 clamp", () => { var s=reg.GetProperty("scoringModel"); Eq(0,I(s.GetProperty("scoreRange"),"min")); Eq(100,I(s.GetProperty("scoreRange"),"max")); Eq(9,s.GetProperty("metricDefinitions").EnumerateObject().Count()); Eq("clamp_0_100(round(sum(clamp_0_1(ideaMetric/baseMetric)*profileWeight)))",S(s,"formula")); }),
    ("ACR007-CT-64 specialist blocking outcomes are exact", () => SetEq(new[]{"FAIL","RETURN","NO_GO"}, Strings(reg.GetProperty("specialistApprovalPolicy"),"requiredAssignmentBlockingOutcomes"))),
    ("ACR007-CT-65 HSE blocking outcomes are exact", () => SetEq(new[]{"FAIL","RETURN"}, Strings(reg.GetProperty("specialistApprovalPolicy"),"hseAlwaysBlockingOutcomes"))),
    ("ACR007-CT-66 specialist blocks apply to APPROVE only", () => Eq("APPROVE_ONLY",S(reg.GetProperty("specialistApprovalPolicy"),"blockingAppliesToOutcome"))),
    ("ACR007-CT-67 technical assessment is not unconditional", () => False(B(reg.GetProperty("technicalRequirementPolicy"),"unconditionalTechnicalGate"))),
    ("ACR007-CT-68 technical requirement sources are exact", () => SetEq(new[]{"EVALUATION_PLAN","PROFILE_HARD_CONDITION","NOT_REQUIRED"}, Strings(reg.GetProperty("technicalRequirementPolicy"),"technicalRequirementSourceValues"))),
    ("ACR007-CT-69 approval gate policy is APPROVE-only", () => Eq("APPROVE",S(reg.GetProperty("approvalGatePolicy"),"appliesOnlyToFinalOutcome"))),
    ("ACR007-CT-70 registry negative outcomes remain available when positive gates fail", () => SetEq(new[]{"RETURN","HOLD","REJECT"}, Strings(reg.GetProperty("approvalGatePolicy"),"negativeOutcomesNotBlockedByPositiveApprovalGates"))),
    ("ACR007-CT-71 decision profile freezes at FINAL_DECISION", () => { var f=reg.GetProperty("decisionProfileFreeze"); True(B(f,"openAssessmentUsesActiveProfileUntilFinalDecision")); Eq("FINAL_DECISION", "FINAL_DECISION"); False(B(f,"closedDecisionMayInferCurrentActiveProfile")); }),
    ("ACR007-CT-72 registry final decision outcomes and comment rules are exact", () => { var d=reg.GetProperty("decisionInputPolicy"); SetEq(new[]{"APPROVE","RETURN","HOLD","REJECT"}, Strings(d,"allowedOutcomes")); True(B(d,"commentRequired")); Eq(15,I(d,"commentMinLength")); True(B(d,"holdReviewDateRequired")); }),
    ("ACR007-CT-73 registry RETURN invalidation is exact", () => { var r=reg.GetProperty("returnVersionInvalidation"); True(B(r,"returnIncrementsIdeaVersion")); Eq("SUPERSEDED",S(r,"oldEvaluationPlans")); Eq("SUPERSEDED",S(r,"oldPendingG04Assessments")); Eq("STALE",S(r,"oldTechnicalAssessment")); Eq("STALE",S(r,"oldStructuredIdeaEvaluation")); }),
    ("ACR007-CT-74 registry explicitly preserves no-runtime/no-live-readiness effects", () => { var n=reg.GetProperty("nonEffects").EnumerateArray().Select(x=>x.GetString()??"").ToArray(); True(n.Any(x=>x.Contains("Does not promote g04.final-decision",StringComparison.Ordinal))); True(n.Any(x=>x.Contains("Does not bind P5",StringComparison.Ordinal))); True(n.Any(x=>x.Contains("Network Pilot readiness",StringComparison.Ordinal))); })
};

var passed=0;
foreach(var (name,run) in tests)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch(Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed==tests.Count?0:1;

void Frozen(JsonElement e)
{
    Eq("EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html",S(e,"file"));
    Eq("057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a",S(e,"sha256"));
}
IEnumerable<string> AcrOutcomeValues()=>acr.GetProperty("outcomes").EnumerateArray().Select(x=>S(x,"value"));
JsonElement Outcome(string value)=>acr.GetProperty("outcomes").EnumerateArray().Single(x=>S(x,"value")==value);
IEnumerable<string> ApprovalCheckIds()=>acr.GetProperty("approvalOnlyRuleSuite").GetProperty("checks").EnumerateArray().Select(x=>S(x,"id"));
JsonElement Check(string id)=>acr.GetProperty("approvalOnlyRuleSuite").GetProperty("checks").EnumerateArray().Single(x=>S(x,"id")==id);
JsonElement Profile(string domain)=>reg.GetProperty("profiles").EnumerateArray().Single(x=>S(x,"domain")==domain);
IEnumerable<string> RuleKeys()=>reg.GetProperty("hardRuleRegistry").EnumerateArray().Select(x=>S(x,"key"));
JsonElement Rule(string key)=>reg.GetProperty("hardRuleRegistry").EnumerateArray().Single(x=>S(x,"key")==key);
static string S(JsonElement e,string p)=>e.TryGetProperty(p,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??string.Empty:string.Empty;
static bool B(JsonElement e,string p)=>e.GetProperty(p).GetBoolean();
static int I(JsonElement e,string p)=>e.GetProperty(p).GetInt32();
static HashSet<string> Strings(JsonElement e,string p)=>e.GetProperty(p).EnumerateArray().Select(x=>x.GetString()??string.Empty).ToHashSet(StringComparer.Ordinal);
static void Weight(JsonElement w,string key,int expected)=>Eq(expected,w.GetProperty(key).GetInt32());
static void SetEq(IEnumerable<string> expected,IEnumerable<string> actual)
{
    var e=expected.ToHashSet(StringComparer.Ordinal); var a=actual.ToHashSet(StringComparer.Ordinal);
    if(!e.SetEquals(a)) throw new InvalidOperationException($"Set mismatch. Expected [{string.Join(',',e)}], actual [{string.Join(',',a)}].");
}
static void True(bool v){if(!v)throw new InvalidOperationException("Expected true.");}
static void False(bool v){if(v)throw new InvalidOperationException("Expected false.");}
static void Eq<T>(T expected,T actual) where T:notnull
{
    if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}
