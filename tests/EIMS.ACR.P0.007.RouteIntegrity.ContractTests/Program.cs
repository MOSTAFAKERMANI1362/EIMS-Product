using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.007.RouteIntegrity.ContractTests <route-integrity-json>");
    return 2;
}

using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = doc.RootElement;
var checks = new List<(string Id,string Name,bool Pass)>();
void C(string id,string name,bool pass)=>checks.Add((id,name,pass));
string S(JsonElement e,string p)=>e.TryGetProperty(p,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
bool B(JsonElement e,string p)=>e.TryGetProperty(p,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False&&v.GetBoolean();
string[] A(JsonElement e,string p)=>e.GetProperty(p).EnumerateArray().Select(x=>x.GetString()??"").ToArray();

C("ROUTE-CT-01","schema and ACR identity are exact",S(root,"schema")=="EIMS-ACR-G04-ROUTE-INTEGRITY-ADDENDUM-1.0"&&S(root,"acrId")=="ACR-P0-007");
C("ROUTE-CT-02","hardening is explicitly post-freeze",S(root,"decisionClass")=="POST_FREEZE_ARCHITECTURE_DECISION_HARDENING");
var product=root.GetProperty("sourceProduct");
C("ROUTE-CT-03","frozen v6.360 remains exact and unmodified",S(product,"sha256")=="057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a"&&!B(product,"modifiedByThisDecision"));

var problem=root.GetProperty("problem");
C("ROUTE-CT-04","current recovered plan route gap is explicit",!B(problem,"currentRecoveredEvaluationPlanEnvelopeCarriesDecisionRoute"));
C("ROUTE-CT-05","current recovered assessment route gap is explicit",!B(problem,"currentRecoveredG04AssessmentEnvelopeCarriesDecisionRoute"));
C("ROUTE-CT-06","Wave7 cannot infer committee route from pending state",B(problem,"wave7VotingCanThereforeNotProveCommitteeRouteFromAssessmentAlone"));

var frozen=root.GetProperty("frozenEvidence");
C("ROUTE-CT-07","frozen product distinguishes route and method",B(frozen,"decisionRouteVisibleInFinalDecisionContext")&&B(frozen,"routeSeparatesCommitteeFromIndividualDecision")&&B(frozen,"decisionMethodVisibleAlongsideRoute"));
C("ROUTE-CT-08","committee vote is route-specific",B(frozen,"committeeVoteAppliesOnlyWhenDecisionRouteIsG04Committee"));
C("ROUTE-CT-09","baseline route IDs are retained as evidence",A(frozen,"baselineRouteIds").ToHashSet().SetEquals(new[]{"G04_COMMITTEE","UNIT_RND_DECISION"}));

var route=root.GetProperty("authoritativeRouteContract");
C("ROUTE-CT-10","route is server-computed then frozen",S(route,"source")=="SERVER_COMPUTED_AT_EVALUATION_PLAN_CREATION_AND_FROZEN_IN_G04_ASSESSMENT");
C("ROUTE-CT-11","client cannot override route",!B(route,"clientMaySetOrOverride"));
C("ROUTE-CT-12","missing and unknown routes fail closed",S(route,"missingRouteAction")=="FAIL_CLOSED"&&S(route,"unknownRouteAction")=="FAIL_CLOSED");
var required=new[]{"decisionRoute","decisionRouteKind","decisionMethod","governanceProfileId","governanceProfileVersion"};
C("ROUTE-CT-13","plan persists complete route context",A(route,"planMustCarry").ToHashSet().SetEquals(required));
C("ROUTE-CT-14","assessment persists complete route context",A(route,"assessmentMustCarry").ToHashSet().SetEquals(required));
C("ROUTE-CT-15","assessment route must exactly match plan snapshot",B(route,"assessmentValuesMustExactlyMatchPlanSnapshot"));
C("ROUTE-CT-16","route kinds are exactly COMMITTEE and INDIVIDUAL",A(route,"routeKinds").ToHashSet().SetEquals(new[]{"COMMITTEE","INDIVIDUAL"}));
var committee=route.GetProperty("committeeRoute");
C("ROUTE-CT-17","committee route identity and kind are exact",S(committee,"routeId")=="G04_COMMITTEE"&&S(committee,"routeKind")=="COMMITTEE");
C("ROUTE-CT-18","committee route permits voting and requires snapshot",B(committee,"votingCommandAllowed")&&B(committee,"committeeSnapshotRequiredBeforeFirstVoteCommit"));
var individual=route.GetProperty("individualRoute");
C("ROUTE-CT-19","individual route does not require committee snapshot",!B(individual,"committeeSnapshotRequired"));
C("ROUTE-CT-20","individual route forbids committee vote",!B(individual,"votingCommandAllowed"));
C("ROUTE-CT-21","individual route remains config-capable",B(individual,"routeIdMayBeConfigurationDriven")&&S(individual,"baselineExampleRouteId")=="UNIT_RND_DECISION");

var method=root.GetProperty("decisionMethodCoherence");
C("ROUTE-CT-22","committee methods match supported vote rules",A(method.GetProperty("committee"),"allowed").ToHashSet().SetEquals(new[]{"MAJORITY","CONSENSUS","CHAIR_TIEBREAK"})&&B(method.GetProperty("committee"),"mustMatchFrozenCommitteeVoteRule"));
C("ROUTE-CT-23","individual method is non-committee",S(method.GetProperty("individual"),"value")=="INDIVIDUAL_GOVERNANCE_DECISION"&&B(method.GetProperty("individual"),"committeeVoteRuleForbidden"));
C("ROUTE-CT-24","configuration drift cannot change frozen assessment route",!B(method,"configurationDriftAfterAssessmentCreationMayAlterRoute"));

var w7=root.GetProperty("wave7Hardening");
C("ROUTE-CT-25","g04.vote must read authoritative assessment route",B(w7,"g04VoteMustReadRouteFromAuthoritativeAssessmentContext"));
C("ROUTE-CT-26","g04.vote only allows committee route",S(w7,"g04VoteAllowedOnlyForRouteKind")=="COMMITTEE"&&S(w7,"g04VoteAllowedOnlyForRouteId")=="G04_COMMITTEE");
C("ROUTE-CT-27","individual-route vote has stable deny code",S(w7,"individualAssessmentVoteResult")=="DENY"&&S(w7,"individualAssessmentVoteErrorCode")=="P1_G04_VOTE_ROUTE_NOT_COMMITTEE");
C("ROUTE-CT-28","client route and stale committee snapshot cannot elevate route",B(w7,"clientSuppliedRouteIgnored")&&B(w7,"existingCommitteeSnapshotCannotConvertIndividualAssessmentToCommittee"));

var final=root.GetProperty("finalDecisionRouteRules");
C("ROUTE-CT-29","final decision consumes frozen assessment route",B(final,"g04FinalDecisionUsesFrozenAssessmentRoute"));
C("ROUTE-CT-30","committee route requires completed stage for all outcomes and positive result for approve",B(final.GetProperty("committeeRoute"),"allFinalOutcomesRequireVotingStageCompleted")&&B(final.GetProperty("committeeRoute"),"approveAdditionallyRequiresApprovalRuleSatisfied"));
C("ROUTE-CT-31","individual final decision does not synthesize committee evidence",B(final.GetProperty("individualRoute"),"committeeStateMustNotBeRequired")&&B(final.GetProperty("individualRoute"),"committeeApprovalMustNotBeSynthesized"));
C("ROUTE-CT-32","route mismatches deny",S(final,"routeMismatchBetweenPlanAndAssessment")=="DENY"&&S(final,"routeMismatchBetweenCommitteeSnapshotAndAssessment")=="DENY");

var migration=root.GetProperty("persistenceMigration");
C("ROUTE-CT-33","both plan and assessment envelopes require extension",B(migration,"extendEvaluationPlanEnvelope")&&B(migration,"extendG04AssessmentEnvelope"));
C("ROUTE-CT-34","new route fields are server controlled",B(migration,"newFieldsAreServerControlled"));
C("ROUTE-CT-35","historical recovery artifacts stay immutable",!B(migration,"historicalRecoveryArtifactsRewritten"));
C("ROUTE-CT-36","production missing route fails closed",S(migration,"productionDefaultForMissingRoute")=="FAIL_CLOSED");

var gate=root.GetProperty("runtimePromotionGate");
C("ROUTE-CT-37","addendum alone cannot promote final decision",!B(gate,"g04FinalDecisionPromotionAllowedByAddendumAlone"));
var still=A(gate,"requiredBeforeWave8Merge");
C("ROUTE-CT-38","Wave8 requires route persistence and Wave7 hardening",still.Contains("EvaluationPlanEnvelope route extension")&&still.Contains("G04AssessmentEnvelope route extension")&&still.Contains("Wave7 g04.vote committee-route enforcement"));
C("ROUTE-CT-39","Wave8 requires negative individual voting and mismatch tests",still.Contains("individual-route negative voting tests")&&still.Contains("route mismatch tests"));

var non=A(root,"nonEffects");
C("ROUTE-CT-40","safety boundaries remain explicit",non.Contains("Does not modify frozen v6.360")&&non.Contains("Does not promote g04.final-decision")&&non.Contains("Does not bind P5")&&non.Contains("Does not claim Oracle, Domain, or Network Pilot readiness"));

foreach(var c in checks) Console.WriteLine($"{(c.Pass?"PASS":"FAIL")} {c.Id} {c.Name}");
var passed=checks.Count(x=>x.Pass);
Console.WriteLine($"RESULT {passed}/{checks.Count} PASS");
return passed==checks.Count?0:1;
