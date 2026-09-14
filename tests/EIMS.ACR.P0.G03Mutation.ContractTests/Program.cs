using System.Text.Json;

if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.G03Mutation.ContractTests <ACR-P0-003-json> <ACR-P0-002-json>");
    return 2;
}

using var acr3Doc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var acr2Doc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr3 = acr3Doc.RootElement;
var acr2 = acr2Doc.RootElement;
var commands = acr3.GetProperty("commands").EnumerateArray().ToArray();
var acr2Events = acr2.GetProperty("events").EnumerateArray().Select(x => S(x, "eventType")).ToHashSet(StringComparer.Ordinal);

var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));

JsonElement C(string command, string? outcome = null) => commands.Single(x =>
    S(x, "command") == command
    && (outcome is null
        ? !x.TryGetProperty("outcome", out _)
        : S(x, "outcome") == outcome));

var submit = C("needs.submit-g03");
var approve = C("needs.g03-decision", "APPROVE");
var ret = C("needs.g03-decision", "RETURN");

Add("G03M-CT-01", "ACR identity/schema/status are exact",
    S(acr3, "acrId") == "ACR-P0-003"
    && S(acr3, "schema") == "EIMS-ACR-G03-MUTATION-ROUTING-1.0"
    && S(acr3, "status") == "APPROVED_FOR_P1_IMPLEMENTATION");
Add("G03M-CT-02", "decision is explicitly post-freeze",
    S(acr3, "decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION");
Add("G03M-CT-03", "frozen product identity/hash are preserved",
    S(acr3.GetProperty("frozenProduct"), "file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html"
    && S(acr3.GetProperty("frozenProduct"), "sha256") == "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a"
    && !B(acr3.GetProperty("frozenProduct"), "modifiedByThisDecision"));
Add("G03M-CT-04", "historical mutation catalog remains unavailable/not claimed recovered",
    S(acr3.GetProperty("provenance"), "historicalOriginalMutationCatalogStatus") == "TBD_OR_UNAVAILABLE"
    && B(acr3.GetProperty("provenance"), "mustNotBeRepresentedAsRecoveredOriginal"));
Add("G03M-CT-05", "exactly three G03 mutation outcomes are defined", commands.Length == 3);

Add("G03M-CT-06", "submit transition is DRAFT to PENDING_G03_REVIEW",
    S(submit,"fromState") == "DRAFT" && S(submit,"toState") == "PENDING_G03_REVIEW");
Add("G03M-CT-07", "submit increments version exactly once", I(submit,"versionIncrement") == 1);
Add("G03M-CT-08", "submit review status and routing are exact",
    S(submit,"reviewStatusAfter") == "PENDING" && S(submit,"workRoutingRoleAfter") == "NEED_REVIEWER");
Add("G03M-CT-09", "submit event is exact and exists in ACR-P0-002",
    S(submit,"eventType") == "NeedSubmittedForG03Review.v1" && acr2Events.Contains(S(submit,"eventType")));
Add("G03M-CT-10", "submit does not fabricate reviewer decision history",
    !B(submit,"immutableDecisionRecordRequired"));

Add("G03M-CT-11", "approve transition is PENDING_G03_REVIEW to READY_FOR_IDEATION",
    S(approve,"fromState") == "PENDING_G03_REVIEW"
    && S(approve,"requiredReviewStatus") == "PENDING"
    && S(approve,"toState") == "READY_FOR_IDEATION");
Add("G03M-CT-12", "approve version/status/routing are exact",
    I(approve,"versionIncrement") == 1
    && S(approve,"reviewStatusAfter") == "APPROVED"
    && S(approve,"workRoutingRoleAfter") == "IDEA_OWNER");
Add("G03M-CT-13", "approve event is exact and exists in ACR-P0-002",
    S(approve,"eventType") == "NeedApprovedForIdeation.v1" && acr2Events.Contains(S(approve,"eventType")));
Add("G03M-CT-14", "approve creates append-only immutable decision record",
    B(approve,"immutableDecisionRecordRequired")
    && S(approve.GetProperty("decisionRecord"),"recordType") == "G03ReviewDecision"
    && B(approve.GetProperty("decisionRecord"),"appendOnly")
    && !B(approve.GetProperty("decisionRecord"),"returnNoteAllowed"));
var approveFields = Strings(approve.GetProperty("decisionRecord"), "requiredFields");
Add("G03M-CT-15", "approve decision record contains reviewer authority and three controls",
    new[] { "needId","decision","definitionComplete","measurable","solutionBiasFree","reviewerPersonId","reviewerAssignmentId","entityVersion","occurredAtUtc","correlationId" }
        .All(approveFields.Contains));

Add("G03M-CT-16", "return transition is PENDING_G03_REVIEW to DRAFT",
    S(ret,"fromState") == "PENDING_G03_REVIEW"
    && S(ret,"requiredReviewStatus") == "PENDING"
    && S(ret,"toState") == "DRAFT");
Add("G03M-CT-17", "return version/status/routing are exact",
    I(ret,"versionIncrement") == 1
    && S(ret,"reviewStatusAfter") == "RETURNED"
    && S(ret,"workRoutingRoleAfter") == "NEED_OWNER");
Add("G03M-CT-18", "return event is exact and exists in ACR-P0-002",
    S(ret,"eventType") == "NeedReturnedFromG03Review.v1" && acr2Events.Contains(S(ret,"eventType")));
var returnRecord = ret.GetProperty("decisionRecord");
var returnNotePolicy = returnRecord.GetProperty("returnNotePolicy");
Add("G03M-CT-19", "return creates append-only immutable decision record",
    B(ret,"immutableDecisionRecordRequired")
    && S(returnRecord,"recordType") == "G03ReviewDecision"
    && B(returnRecord,"appendOnly"));
Add("G03M-CT-20", "return note policy is authoritative, min-10 and excluded from integration event",
    I(returnNotePolicy,"trimmedMinLength") == 10
    && B(returnNotePolicy,"persistInAuthoritativeDecisionRecord")
    && !B(returnNotePolicy,"includeInIntegrationEvent"));
var returnFields = Strings(returnRecord,"requiredFields");
Add("G03M-CT-21", "return decision record requires note and reviewer authority context",
    new[] { "needId","decision","returnNote","reviewerPersonId","reviewerAssignmentId","entityVersion","occurredAtUtc","correlationId" }
        .All(returnFields.Contains));

Add("G03M-CT-22", "owner identity and scope are preserved by every mutation",
    commands.All(x => { var p = Strings(x,"preserve"); return p.Contains("ownerPersonId") && p.Contains("scope"); }));
Add("G03M-CT-23", "state version review routing and event are server-computed",
    commands.All(x => { var f = Strings(x,"serverComputedFields"); return new[] { "state","version","g03ReviewStatus","workRoutingRole","eventType" }.All(f.Contains); }));

var routing = acr3.GetProperty("workRoutingContract");
Add("G03M-CT-24", "workRoutingRole is workflow fact with zero authorization effect",
    S(routing,"field") == "workRoutingRole"
    && S(routing,"classification") == "WORKFLOW_ROUTING_FACT"
    && S(routing,"authorizationEffect") == "NONE"
    && B(routing,"serverComputed")
    && !B(routing,"clientSuppliedValueTrusted"));
var noRbacMutation = Strings(routing,"mustNotCreateOrModify");
Add("G03M-CT-25", "routing cannot mutate RBAC claims or directory identity",
    new[] { "RoleAssignmentEntry","WindowsClaim","PersonDirectoryEntry" }.All(noRbacMutation.Contains));

var history = acr3.GetProperty("decisionHistoryContract");
Add("G03M-CT-26", "G03 decision history is append-only and never overwritten",
    S(history,"recordType") == "G03ReviewDecision"
    && B(history,"appendOnly")
    && !B(history,"overwriteHistoricalDecision")
    && B(history,"sameTransactionAsAggregateMutation"));
Add("G03M-CT-27", "return note is authoritative decision data but forbidden from event",
    S(history,"returnNoteClassification") == "AUTHORITATIVE_DOMAIN_DECISION_DATA"
    && !B(history,"integrationEventMayContainReturnNote"));

var atomic = acr3.GetProperty("atomicPersistenceBoundary");
var atomicParts = Strings(atomic,"requiredComponents");
Add("G03M-CT-28", "atomic boundary contains state decision audit outbox idempotency",
    new[] { "AggregateState","G03ReviewDecisionWhenApplicable","Audit","Outbox","Idempotency" }.All(atomicParts.Contains)
    && B(atomic,"singleTransactionRequired")
    && B(atomic,"outboxPublishedOnlyAfterCommit")
    && B(atomic,"rollbackAllOnAnyFailure"));

var authority = acr3.GetProperty("securityAndAuthority");
Add("G03M-CT-29", "client cannot control state review routing or event and self-review is denied",
    !B(authority,"clientMaySetState")
    && !B(authority,"clientMaySetReviewStatus")
    && !B(authority,"clientMaySetWorkRoutingRole")
    && !B(authority,"clientMaySetEventType")
    && !B(authority,"needOwnerMaySelfReview")
    && B(authority,"authorizationStillRequiresServerPersonRoleScopeAssignment"));

var gate = acr3.GetProperty("p1ConsumptionGate");
Add("G03M-CT-30", "ACR defines mutation but cannot promote runtime alone",
    B(gate,"mutationContractDefined")
    && !B(gate,"mutationContractRecoveredFlagMayBeSetByThisAcrAlone"));
var stillRequired = Strings(gate,"stillRequiredBeforeRuntimePromotion");
Add("G03M-CT-31", "runtime promotion still requires planner persistence atomicity auth and P5 decision",
    new[] { "pureMutationPlannerImplementation","mutationPlannerContractTests","outcomeEventResolutionTests","immutableDecisionRecordPersistenceContract","atomicPersistenceTests","serverAuthorizationTests","P5CompositionDecision" }
        .All(stillRequired.Contains));
var nonEffects = Strings(acr3,"nonEffects");
Add("G03M-CT-32", "ACR explicitly leaves Product disabled and v6.360 unchanged",
    nonEffects.Contains("Does not enable any Product command")
    && nonEffects.Contains("Does not bind P5 to P1")
    && nonEffects.Contains("Does not modify v6.360"));

foreach (var r in results)
    Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Id} {r.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;

static string S(JsonElement e, string property) =>
    e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
static bool B(JsonElement e, string property) => e.GetProperty(property).GetBoolean();
static int I(JsonElement e, string property) => e.GetProperty(property).GetInt32();
static HashSet<string> Strings(JsonElement e, string property) =>
    e.GetProperty(property).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
