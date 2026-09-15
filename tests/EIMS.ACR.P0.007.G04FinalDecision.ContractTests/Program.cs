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
var tests = new List<(string Id, string Name, Action Run)>();
void T(string id, string name, Action run) => tests.Add((id, name, run));

T("ACR007-CT-01", "ACR identity/status are exact", () => { Eq("ACR-P0-007", S(acr,"acrId")); Eq("APPROVED_FOR_P1_IMPLEMENTATION", S(acr,"status")); Eq("POST_FREEZE_ARCHITECTURE_DECISION", S(acr,"decisionClass")); });
T("ACR007-CT-02", "frozen v6.360 SHA is exact and unchanged", () => Frozen(acr.GetProperty("sourceProduct")));
T("ACR007-CT-03", "ACR does not promote runtime", () => False(B(acr.GetProperty("command"),"runtimePromotionInThisAcr")));
T("ACR007-CT-04", "command identity/endpoint are exact", () => { var c=acr.GetProperty("command"); Eq("g04.final-decision",S(c,"id")); Eq("POST",S(c,"method")); Eq("/commands/g04/{assessmentId}/final-decision",S(c,"endpoint")); Eq("Idea",S(c,"targetAggregate")); Eq("IDEA_DECISION",S(c,"requiredRole")); });
T("ACR007-CT-05", "Wave7 committee runtime is a prerequisite", () => True(acr.GetProperty("prerequisites").EnumerateArray().Any(x=>S(x,"artifact")=="P1 Recovery Wave 7")));

var authority=acr.GetProperty("authority");
T("ACR007-CT-06", "authority is P3 exact IDEA_DECISION assignment", () => { Eq("P3_AUTHENTICATED_AUTHORITY_ACTOR",S(authority,"identitySource")); True(B(authority,"exactAuthorityAssignmentRequired")); Eq("IDEA_DECISION",S(authority,"singleEffectiveRoleRequired")); });
T("ACR007-CT-07", "client cannot supply security authority", () => False(B(authority,"clientMaySupplyPersonRoleScopeOrAssignmentAuthority")));
T("ACR007-CT-08", "scope must contain Idea scope", () => True(B(authority,"scopeMustContainIdeaScope")));
T("ACR007-CT-09", "committee member cannot be final decider same assessment", () => { False(B(authority,"committeeMemberMayBeFinalDeciderForSameAssessment")); True(B(authority,"committeeAndFinalDecisionAuthoritySeparated")); });
T("ACR007-CT-10", "evaluator conflict defaults DENY for APPROVE", () => { var p=authority.GetProperty("evaluatorConflictPolicy"); Eq("APPROVE",S(p,"appliesToOutcome")); Eq("DENY",S(p,"default")); });
T("ACR007-CT-11", "only frozen ALLOW_WITH_AUDIT evaluator exception is allowed", () => { var p=authority.GetProperty("evaluatorConflictPolicy"); Eq("ALLOW_WITH_AUDIT",S(p,"allowedException")); True(B(p,"exceptionMustBeFrozenGovernancePolicy")); True(B(p,"exceptionRequiresExplicitAuditEvidence")); });

var context=acr.GetProperty("context");
T("ACR007-CT-12", "exact PENDING assessment context is required", () => { Eq("PENDING",S(context,"assessmentStateRequired")); True(B(context,"exactAssessmentIdRequired")); True(B(context,"exactIdeaIdRequired")); });
T("ACR007-CT-13", "assessment/plan business revision must equal current Idea revision", () => { True(B(context,"assessmentIdeaRevisionMustEqualCurrentIdeaRevision")); True(B(context,"exactEvaluationPlanRequired")); True(B(context,"planIdeaRevisionMustEqualCurrentIdeaRevision")); });
T("ACR007-CT-14", "stale/superseded context is denied", () => { True(B(context,"staleOrSupersededPlanDenied")); True(B(context,"staleOrSupersededAssessmentDenied")); });
T("ACR007-CT-15", "concurrency/version/idempotency are mandatory", () => { True(B(context,"optimisticConcurrencyRequired")); True(B(context,"expectedStateMutationVersionRequired")); True(B(context,"idempotencyRequired")); });

var versions=acr.GetProperty("versionSemantics");
T("ACR007-CT-16", "business revision is separate from technical concurrency version", () => True(B(versions,"businessIdeaRevisionSeparatedFromTechnicalConcurrencyVersion")));
T("ACR007-CT-17", "every final decision increments technical state mutation version once", () => { Eq(1,I(versions,"technicalStateMutationVersionIncrementExactlyBy")); Eq(1,I(versions,"technicalAssessmentDecisionVersionIncrementExactlyBy")); });
T("ACR007-CT-18", "business revision increments only on RETURN", () => { var p=versions.GetProperty("businessIdeaRevisionIncrementPolicy"); Eq(0,I(p,"APPROVE")); Eq(1,I(p,"RETURN")); Eq(0,I(p,"HOLD")); Eq(0,I(p,"REJECT")); });

T("ACR007-CT-19", "final outcomes are exactly four", () => SetEq(new[]{"APPROVE","RETURN","HOLD","REJECT"}, AcrOutcomes()));
T("ACR007-CT-20", "APPROVE mapping is exact", () => { var o=Outcome("APPROVE"); Eq("APPROVED",S(o,"ideaStateAfter")); Eq(0,I(o,"businessIdeaRevisionIncrement")); Eq(1,I(o,"technicalStateMutationVersionIncrement")); Eq("DECIDED",S(o,"assessmentStateAfter")); Eq("IdeaApprovedForPortfolio.v1",S(o,"eventType")); True(B(o,"positiveApprovalGateRequired")); });
T("ACR007-CT-21", "RETURN mapping is exact", () => { var o=Outcome("RETURN"); Eq("RETURNED",S(o,"ideaStateAfter")); Eq(1,I(o,"businessIdeaRevisionIncrement")); Eq(1,I(o,"technicalStateMutationVersionIncrement")); Eq("IdeaReturnedFromG04.v1",S(o,"eventType")); False(B(o,"positiveApprovalGateRequired")); });
T("ACR007-CT-22", "HOLD mapping and review-date requirement are exact", () => { var o=Outcome("HOLD"); Eq("HOLD",S(o,"ideaStateAfter")); Eq(0,I(o,"businessIdeaRevisionIncrement")); Eq(1,I(o,"technicalStateMutationVersionIncrement")); Eq("IdeaHeldAtG04.v1",S(o,"eventType")); True(B(o,"reviewDateRequired")); });
T("ACR007-CT-23", "REJECT mapping is exact", () => { var o=Outcome("REJECT"); Eq("REJECTED",S(o,"ideaStateAfter")); Eq(0,I(o,"businessIdeaRevisionIncrement")); Eq(1,I(o,"technicalStateMutationVersionIncrement")); Eq("IdeaRejectedAtG04.v1",S(o,"eventType")); });

var input=acr.GetProperty("decisionInput");
T("ACR007-CT-24", "decision comment minimum is 15", () => { True(B(input,"commentRequired")); Eq(15,I(input,"commentMinLength")); });
T("ACR007-CT-25", "reason code is mandatory and vocabulary is frozen", () => { True(B(input,"reasonCodeRequired")); SetEq(new[]{"G04_READY","G04_SOLUTION_INCOMPLETE","G04_EVIDENCE_INCOMPLETE","G04_COST_INCOMPLETE","G04_RISK_UNRESOLVED","G04_TECH_FAIL","G04_ALIGNMENT","OTHER"}, Strings(input,"allowedReasonCodes")); });
T("ACR007-CT-26", "APPROVE requires G04_READY reason", () => Eq("REQUIRES_G04_READY",S(input.GetProperty("reasonCompatibility"),"APPROVE")));
T("ACR007-CT-27", "negative outcomes cannot use G04_READY", () => { var p=input.GetProperty("reasonCompatibility"); Eq("MUST_NOT_USE_G04_READY",S(p,"RETURN")); Eq("MUST_NOT_USE_G04_READY",S(p,"HOLD")); Eq("MUST_NOT_USE_G04_READY",S(p,"REJECT")); });
T("ACR007-CT-28", "HOLD review date is mandatory", () => True(B(input,"holdReviewDateRequired")));
T("ACR007-CT-29", "client cannot choose event or frozen policy evidence", () => { False(B(input,"clientMayChooseEventIdentity")); False(B(input,"clientMayChooseRuleProfileOrFrozenEvidence")); });

var rules=acr.GetProperty("approvalOnlyRuleSuite");
T("ACR007-CT-30", "positive rule suite applies only to APPROVE", () => Eq("APPROVE",S(rules,"appliesOnlyWhenOutcome")));
T("ACR007-CT-31", "negative outcomes remain available when positive gates fail", () => SetEq(new[]{"RETURN","HOLD","REJECT"}, Strings(rules,"negativeOutcomesMayProceedWhenApprovalRulesFail")));
T("ACR007-CT-32", "approval check set is exact", () => SetEq(new[]{"PROFILE_SNAPSHOT_VALID","GATE_SCORE_THRESHOLD","DATA_READINESS_THRESHOLD","BASE_NEED_PRESENT","CONDITIONAL_TECHNICAL_VALIDITY","HARD_CONDITIONS","SPECIALIST_OUTCOMES","COMMITTEE_POSITIVE_RESULT","SEPARATION_OF_DUTIES"}, ApprovalCheckIds()));
T("ACR007-CT-33", "committee positive result is only an APPROVE gate", () => Eq("decisionRoute == G04_COMMITTEE AND outcome == APPROVE",S(Check("COMMITTEE_POSITIVE_RESULT"),"requiredWhen")));
T("ACR007-CT-34", "specialist gate uses current four-value vocabulary", () => Contains(S(Check("SPECIALIST_OUTCOMES"),"rule"),"CONFIRMED/CONDITIONAL/NOT_CONFIRMED/MORE_EVIDENCE"));

var freeze=acr.GetProperty("profileAndRuleFreeze");
T("ACR007-CT-35", "profile freezes at final decision", () => { Eq("CURRENT_ACTIVE_PROFILE",S(freeze,"openAssessmentProfileSource")); Eq("FINAL_DECISION",S(freeze,"freezeOccursAt")); });
T("ACR007-CT-36", "closed history may not infer current profile", () => False(B(freeze,"closedHistoryMayInferFromCurrentProfile")));
T("ACR007-CT-37", "profile freeze anchors are complete", () => SetEq(new[]{"profileId","profileVersion","profileSnapshot","profileSnapshotHash","ruleSet","hardConditionMappingVersion","gateScore","dataReadiness","ideaRevision","evaluationPlanId","evaluationPlanVersion","assessmentId","frozenAt"},Strings(freeze,"requiredFrozenEvidence")));

var committee=acr.GetProperty("committeeEvidenceForFinalDecision");
T("ACR007-CT-38", "committee route requires stage completion for every final outcome", () => { Eq("G04_COMMITTEE",S(committee,"requiredWhenRoute")); True(B(committee,"committeeStageMustCompleteBeforeAnyFinalOutcome")); SetEq(new[]{"committeeSnapshotId","committeeVersion","votingStageState=COMPLETED"},Strings(committee,"requiredForEveryOutcome")); });
T("ACR007-CT-39", "committee APPROVE additionally requires positive vote result", () => SetEq(new[]{"approvalRuleSatisfied=true"},Strings(committee,"additionalRequiredForApprove")));
T("ACR007-CT-40", "negative outcome may follow completed non-approving committee result", () => True(B(committee,"negativeOutcomeAfterCompletedNonApprovingVoteAllowed")));
T("ACR007-CT-41", "committee full membership and vote notes stay out of integration events", () => { False(B(committee,"fullMemberSnapshotCopiedIntoIntegrationEvent")); False(B(committee,"voteNotesCopiedIntoIntegrationEvent")); });

var evidence=acr.GetProperty("immutableDecisionEvidence");
T("ACR007-CT-42", "final decision evidence is append-only", () => { True(B(evidence,"appendOnly")); False(B(evidence,"historicalDecisionMayBeOverwritten")); });
T("ACR007-CT-43", "final evidence stores reason and version separation", () => { var f=Strings(evidence,"fields"); foreach(var x in new[]{"reasonCode","ideaRevisionBefore","ideaRevisionAfter","technicalStateMutationVersionBefore","technicalStateMutationVersionAfter"}) True(f.Contains(x)); });
T("ACR007-CT-44", "final evidence stores authority/rules/committee context", () => { var f=Strings(evidence,"fields"); foreach(var x in new[]{"personId","authorityAssignmentId","profileSnapshotHash","ruleCheckResults","specialistOutcomeSummary","committeeStageStateWhenApplicable","sodResult","correlationId"}) True(f.Contains(x)); });

var ret=acr.GetProperty("returnInvalidation");
T("ACR007-CT-45", "RETURN increments business Idea revision once", () => Eq(1,I(ret,"businessIdeaRevisionMustIncrementExactlyBy")));
T("ACR007-CT-46", "RETURN supersedes old plan and pending assessment", () => { Eq("SUPERSEDED",S(ret,"oldActiveEvaluationPlans")); Eq("SUPERSEDED",S(ret,"oldPendingG04Assessments")); });
T("ACR007-CT-47", "RETURN makes old technical/structured evidence stale", () => { Eq("STALE",S(ret,"oldTechnicalAssessment")); Eq("STALE",S(ret,"oldStructuredIdeaEvaluation")); True(B(ret,"newRevisionRequiresFreshEvaluationContext")); });

var tx=acr.GetProperty("transactionBoundary");
T("ACR007-CT-48", "transaction includes business and technical version state", () => { var a=Strings(tx,"singleLogicalTransaction"); True(a.Contains("IdeaStateAndBusinessRevision")); True(a.Contains("IdeaTechnicalStateMutationVersion")); });
T("ACR007-CT-49", "decision evidence/audit/outbox/idempotency share transaction", () => { var a=Strings(tx,"singleLogicalTransaction"); foreach(var x in new[]{"G04AssessmentDecisionAndClosure","ImmutableFinalDecisionEvidence","Audit","OutcomeOutbox","Idempotency"}) True(a.Contains(x)); });
T("ACR007-CT-50", "Portfolio eligibility is outside transaction", () => { False(B(tx,"portfolioEligibilityInsideTransaction")); False(B(tx,"portfolioFailureMayRollbackCommittedFinalDecision")); });

var events=acr.GetProperty("events");
T("ACR007-CT-51", "outcome event bindings are exact", () => { var b=events.GetProperty("outcomeBindings"); Eq("IdeaApprovedForPortfolio.v1",S(b,"APPROVE")); Eq("IdeaReturnedFromG04.v1",S(b,"RETURN")); Eq("IdeaHeldAtG04.v1",S(b,"HOLD")); Eq("IdeaRejectedAtG04.v1",S(b,"REJECT")); });
T("ACR007-CT-52", "event payload excludes sensitive/full evidence", () => SetEq(new[]{"decisionComment","evaluatorAnswers","fullIdeaDossier","fullProfileSnapshot","fullCommitteeMemberSnapshot","voteNotes"},Strings(events,"forbiddenPayload")));
var portfolio=acr.GetProperty("portfolioBoundary");
T("ACR007-CT-53", "Portfolio eligibility is post-commit and idempotent", () => { Eq("IdeaApprovedForPortfolio.v1",S(portfolio,"triggerEvent")); Eq("PortfolioEligibilityService",S(portfolio,"service")); Eq("POST_COMMIT_IDEMPOTENT_DOWNSTREAM",S(portfolio,"execution")); False(B(portfolio,"candidateCreationInsideFinalDecision")); });
T("ACR007-CT-54", "Portfolio failure does not roll back G04 decision", () => Contains(S(portfolio,"failureHandling"),"WITHOUT_ROLLING_BACK_G04_DECISION"));
var runtime=acr.GetProperty("runtimeSafety");
T("ACR007-CT-55", "P5/live environment readiness remains outside ACR", () => { False(B(runtime,"runtimePromotionInThisAcr")); False(B(runtime,"p5CommandGatewayBound")); False(B(runtime,"physicalOracleReadyClaimed")); False(B(runtime,"liveWindowsDomainReadyClaimed")); False(B(runtime,"networkPilotReadyClaimed")); True(B(runtime,"v6360Unchanged")); });

T("ACR007-CT-56", "recovered registry identity/provenance are exact", () => { Eq("EIMS-G04-DECISION-PROFILE-RULE-REGISTRY-1.0",S(reg,"schema")); Eq("RECOVERED_FROM_FROZEN_V6_360_NOT_ORIGINAL_SERVER_SCHEMA",S(reg,"recoveryStatus")); True(B(reg.GetProperty("provenance"),"mustNotBeRepresentedAsOriginalServerSchema")); True(B(reg.GetProperty("provenance"),"noRuleStrengtheningBeyondFrozenEvidence")); });
T("ACR007-CT-57", "recovered registry points to frozen v6.360", () => Frozen(reg.GetProperty("sourceProduct")));
T("ACR007-CT-58", "registry contains exactly GENERAL and HSE profiles", () => SetEq(new[]{"GENERAL","HSE"},reg.GetProperty("profiles").EnumerateArray().Select(x=>S(x,"domain"))));
T("ACR007-CT-59", "GENERAL thresholds are 65/80", () => { var p=Profile("GENERAL"); Eq(65,I(p,"passThreshold")); Eq(80,I(p,"dataThreshold")); });
T("ACR007-CT-60", "GENERAL weights are exact and total 100", () => Weights(Profile("GENERAL").GetProperty("weights"),new Dictionary<string,int>{{"strategy",15},{"needScore",15},{"benefitScore",20},{"tech",10},{"econ",10},{"effort",10},{"riskScore",10},{"urgency",5},{"innovation",5}}));
T("ACR007-CT-61", "GENERAL hard rules are exact", () => SetEq(new[]{"REQUIRED_EVALUATIONS_COMPLETE","ACTIVE_STRATEGY_ALIGNED","BASE_NEED_G03_READY"},Profile("GENERAL").GetProperty("hardConditions").EnumerateArray().Select(x=>S(x,"machineKey"))));
T("ACR007-CT-62", "HSE thresholds are 60/90", () => { var p=Profile("HSE"); Eq(60,I(p,"passThreshold")); Eq(90,I(p,"dataThreshold")); });
T("ACR007-CT-63", "HSE weights are exact and total 100", () => Weights(Profile("HSE").GetProperty("weights"),new Dictionary<string,int>{{"strategy",10},{"needScore",15},{"benefitScore",5},{"tech",15},{"econ",0},{"effort",5},{"riskScore",30},{"urgency",15},{"innovation",5}}));
T("ACR007-CT-64", "six hard-rule machine keys are exact", () => SetEq(new[]{"REQUIRED_EVALUATIONS_COMPLETE","BASE_NEED_G03_READY","TECHNICAL_ASSESSMENT_VALID","ACTIVE_STRATEGY_ALIGNED","HSE_ASSESSMENT_ACCEPTABLE","HSE_AND_TECH_VALID"},reg.GetProperty("hardRuleRegistry").EnumerateArray().Select(x=>S(x,"key"))));
T("ACR007-CT-65", "unknown hard-condition text fails closed", () => Eq("FAIL_CLOSED",S(reg.GetProperty("hardConditionMapping"),"unknownTextAction")));
T("ACR007-CT-66", "data readiness has exactly 12 checks", () => Eq(12,reg.GetProperty("dataReadiness").GetProperty("checks").GetArrayLength()));
T("ACR007-CT-67", "score model has nine metrics and 0..100 range", () => { Eq(9,reg.GetProperty("scoringModel").GetProperty("metricDefinitions").EnumerateObject().Count()); var r=reg.GetProperty("scoringModel").GetProperty("scoreRange"); Eq(0,I(r,"min")); Eq(100,I(r,"max")); });

var vocab=reg.GetProperty("specialistOutcomeVocabulary");
T("ACR007-CT-68", "authoritative specialist outcomes are current four-value vocabulary", () => SetEq(new[]{"CONFIRMED","CONDITIONAL","NOT_CONFIRMED","MORE_EVIDENCE"},Strings(vocab,"authoritativeCurrentOutcomes")));
T("ACR007-CT-69", "current accepted specialist outcomes are CONFIRMED/CONDITIONAL", () => SetEq(new[]{"CONFIRMED","CONDITIONAL"},Strings(vocab,"acceptedForApprove")));
T("ACR007-CT-70", "current blocking specialist outcomes are NOT_CONFIRMED/MORE_EVIDENCE", () => SetEq(new[]{"NOT_CONFIRMED","MORE_EVIDENCE"},Strings(vocab,"blockingForApprove")));
T("ACR007-CT-71", "legacy outcomes are read compatibility only", () => SetEq(new[]{"PASS","CONDITIONAL_PASS","RETURN","FAIL","NO_GO","APPROVE","APPROVED"},Strings(vocab,"legacyReadCompatibilityOnly")));
T("ACR007-CT-72", "new runtime may not emit legacy outcomes", () => False(B(vocab,"newRuntimeMayEmitLegacyOutcome")));
T("ACR007-CT-73", "legacy normalization is deterministic", () => { var n=vocab.GetProperty("legacyNormalization"); Eq("CONFIRMED",S(n,"PASS")); Eq("CONDITIONAL",S(n,"CONDITIONAL_PASS")); Eq("MORE_EVIDENCE",S(n,"RETURN")); Eq("NOT_CONFIRMED",S(n,"FAIL")); Eq("NOT_CONFIRMED",S(n,"NO_GO")); });
T("ACR007-CT-74", "HSE current accepted/blocking outcomes are exact", () => { var e=Rule("HSE_ASSESSMENT_ACCEPTABLE").GetProperty("evaluation"); SetEq(new[]{"CONFIRMED","CONDITIONAL"},Strings(e,"acceptedCurrentOutcomes")); SetEq(new[]{"NOT_CONFIRMED","MORE_EVIDENCE"},Strings(e,"blockingCurrentOutcomes")); });
T("ACR007-CT-75", "technical current accepted outcomes are exact", () => SetEq(new[]{"CONFIRMED","CONDITIONAL"},Strings(Rule("TECHNICAL_ASSESSMENT_VALID").GetProperty("evaluation"),"acceptedCurrentOutcomes")));
var specialist=reg.GetProperty("specialistApprovalPolicy");
T("ACR007-CT-76", "specialist policy uses current authoritative vocabulary", () => { SetEq(new[]{"NOT_CONFIRMED","MORE_EVIDENCE"},Strings(specialist,"authoritativeBlockingOutcomes")); SetEq(new[]{"CONFIRMED","CONDITIONAL"},Strings(specialist,"authoritativeAcceptedOutcomes")); Eq("APPROVE_ONLY",S(specialist,"blockingAppliesToOutcome")); True(B(specialist,"legacyValuesMustNormalizeBeforeEvaluation")); });
T("ACR007-CT-77", "technical assessment remains conditional not unconditional", () => False(B(reg.GetProperty("technicalRequirementPolicy"),"unconditionalTechnicalGate")));
T("ACR007-CT-78", "technical requirement sources are exact", () => SetEq(new[]{"CURRENT_EVALUATION_PLAN_HAS_REQUIRED_TECHNICAL_ASSESSOR","FROZEN_PROFILE_HAS_TECHNICAL_ASSESSMENT_VALID","FROZEN_PROFILE_HAS_HSE_AND_TECH_VALID"},Strings(reg.GetProperty("technicalRequirementPolicy"),"technicalRequiredWhen")));
T("ACR007-CT-79", "approval gates remain APPROVE-only", () => { var p=reg.GetProperty("approvalGatePolicy"); Eq("APPROVE",S(p,"appliesOnlyToFinalOutcome")); SetEq(new[]{"RETURN","HOLD","REJECT"},Strings(p,"negativeOutcomesNotBlockedByPositiveApprovalGates")); });
T("ACR007-CT-80", "decision profile freezes at FINAL_DECISION", () => { var p=reg.GetProperty("decisionProfileFreeze"); True(B(p,"openAssessmentUsesActiveProfileUntilFinalDecision")); False(B(p,"closedDecisionMayInferCurrentActiveProfile")); });
var di=reg.GetProperty("decisionInputPolicy");
T("ACR007-CT-81", "registry reason vocabulary and compatibility are exact", () => { SetEq(new[]{"G04_READY","G04_SOLUTION_INCOMPLETE","G04_EVIDENCE_INCOMPLETE","G04_COST_INCOMPLETE","G04_RISK_UNRESOLVED","G04_TECH_FAIL","G04_ALIGNMENT","OTHER"},Strings(di,"allowedReasonCodes")); Eq("G04_READY",S(di,"approveReasonCode")); False(B(di,"negativeOutcomesMayUseApproveReasonCode")); });
T("ACR007-CT-82", "registry decision comment/HOLD rules are exact", () => { True(B(di,"commentRequired")); Eq(15,I(di,"commentMinLength")); True(B(di,"holdReviewDateRequired")); });
var cf=reg.GetProperty("committeeFinalizationPolicy");
T("ACR007-CT-83", "registry requires completed committee stage for every final outcome", () => True(B(cf,"committeeRouteRequiresStageCompleteForEveryFinalOutcome")));
T("ACR007-CT-84", "registry requires positive committee result only for APPROVE", () => { True(B(cf,"approveAdditionallyRequiresApprovalRuleSatisfied")); True(B(cf,"negativeOutcomeAllowedAfterCompletedNonApprovingCommitteeResult")); });
var tc=reg.GetProperty("technicalConcurrencyPolicy");
T("ACR007-CT-85", "registry separates technical concurrency from business revision", () => { True(B(tc,"separateFromBusinessIdeaRevision")); True(B(tc,"everySuccessfulFinalDecisionIncrementsStateMutationVersion")); Eq(1,I(tc,"incrementExactlyBy")); });
T("ACR007-CT-86", "RETURN invalidates prior evaluation context", () => { var r=reg.GetProperty("returnVersionInvalidation"); True(B(r,"returnIncrementsBusinessIdeaRevision")); Eq("SUPERSEDED",S(r,"oldEvaluationPlans")); Eq("SUPERSEDED",S(r,"oldPendingG04Assessments")); Eq("STALE",S(r,"oldTechnicalAssessment")); Eq("STALE",S(r,"oldStructuredIdeaEvaluation")); });
T("ACR007-CT-87", "registry preserves no-runtime/no-live-readiness effects", () => { var a=reg.GetProperty("nonEffects").EnumerateArray().Select(x=>x.GetString()??"").ToArray(); True(a.Any(x=>x.Contains("Does not promote g04.final-decision",StringComparison.Ordinal))); True(a.Any(x=>x.Contains("Does not bind P5",StringComparison.Ordinal))); True(a.Any(x=>x.Contains("Does not claim physical Oracle",StringComparison.Ordinal))); });

var passed=0;
foreach(var test in tests)
{
    try { test.Run(); passed++; Console.WriteLine($"PASS {test.Id} {test.Name}"); }
    catch(Exception ex) { Console.Error.WriteLine($"FAIL {test.Id} {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed==tests.Count ? 0 : 1;

string S(JsonElement e,string p) => e.TryGetProperty(p,out var v) && v.ValueKind==JsonValueKind.String ? v.GetString()??"" : "";
bool B(JsonElement e,string p) => e.GetProperty(p).GetBoolean();
int I(JsonElement e,string p) => e.GetProperty(p).GetInt32();
string[] Strings(JsonElement e,string p) => e.GetProperty(p).EnumerateArray().Select(x=>x.GetString()??"").ToArray();
void True(bool v){if(!v)throw new Exception("expected true");}
void False(bool v){if(v)throw new Exception("expected false");}
void Eq(string expected,string actual){if(!string.Equals(expected,actual,StringComparison.Ordinal))throw new Exception($"expected '{expected}', actual '{actual}'");}
void Eq(int expected,int actual){if(expected!=actual)throw new Exception($"expected {expected}, actual {actual}");}
void Contains(string actual,string expected){if(!actual.Contains(expected,StringComparison.Ordinal))throw new Exception($"expected text containing '{expected}'");}
void SetEq(IEnumerable<string> expected,IEnumerable<string> actual){var a=expected.OrderBy(x=>x,StringComparer.Ordinal).ToArray();var b=actual.OrderBy(x=>x,StringComparer.Ordinal).ToArray();if(!a.SequenceEqual(b,StringComparer.Ordinal))throw new Exception($"set mismatch expected=[{string.Join(',',a)}], actual=[{string.Join(',',b)}]");}
void Frozen(JsonElement e){Eq("EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html",S(e,"file"));Eq("057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a",S(e,"sha256")); if(e.TryGetProperty("modifiedByThisDecision",out var m))False(m.GetBoolean()); if(e.TryGetProperty("modifiedByThisRecovery",out var r))False(r.GetBoolean());}
JsonElement Outcome(string value)=>acr.GetProperty("outcomes").EnumerateArray().Single(x=>S(x,"value")==value);
string[] AcrOutcomes()=>acr.GetProperty("outcomes").EnumerateArray().Select(x=>S(x,"value")).ToArray();
JsonElement Check(string id)=>rules.GetProperty("checks").EnumerateArray().Single(x=>S(x,"id")==id);
string[] ApprovalCheckIds()=>rules.GetProperty("checks").EnumerateArray().Select(x=>S(x,"id")).ToArray();
JsonElement Profile(string domain)=>reg.GetProperty("profiles").EnumerateArray().Single(x=>S(x,"domain")==domain);
JsonElement Rule(string key)=>reg.GetProperty("hardRuleRegistry").EnumerateArray().Single(x=>S(x,"key")==key);
void Weights(JsonElement w,IReadOnlyDictionary<string,int> expected){Eq(expected.Count,w.EnumerateObject().Count());foreach(var kv in expected)Eq(kv.Value,I(w,kv.Key));Eq(100,w.EnumerateObject().Sum(x=>x.Value.GetInt32()));}
