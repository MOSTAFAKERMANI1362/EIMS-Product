using System.Text.Json;

namespace EIMS.P0.MachineRecovery;

public sealed record RecoveryCheck(string Id, string Description, bool Passed);

public static class P0MachineRecoveryValidator
{
    public const string ExpectedSchema = "EIMS-P0-MACHINE-RECOVERY-1.0";
    public const string ExpectedStatus = "RECOVERED_REBASELINE_NOT_ORIGINAL";
    public const string FrozenSha256 = "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a";

    private static readonly string[] ExpectedCommandIds =
    {
        "g01.decide", "g02.decide", "needs.submit-g03", "needs.g03-decision", "ideas.submit-g04",
        "evaluation-assignments.complete", "g04.vote", "g04.final-decision", "portfolio.assign-accept",
        "executions.prepare", "executions.progress", "executions.submit-completion", "executions.completion-review",
        "benefits.accept", "benefits.measure", "benefits.verify", "benefits.attribution", "benefits.realize",
        "knowledge.validate", "knowledge.publish", "rewards.decide"
    };

    public static IReadOnlyList<RecoveryCheck> Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var checks = new List<RecoveryCheck>();
        void Add(string id, string description, bool passed) => checks.Add(new(id, description, passed));

        Add("P0R-01", "recovery schema identity", Str(root, "schema") == ExpectedSchema);
        Add("P0R-02", "recovery is explicitly not the missing original package", Str(root, "status") == ExpectedStatus);
        Add("P0R-03", "frozen v6.360 SHA is bound", Str(root.GetProperty("frozenProduct"), "sha256") == FrozenSha256);

        var provenance = root.GetProperty("provenance");
        Add("P0R-04", "original machine-readable package is declared unavailable", !Bool(provenance, "originalMachineReadablePackageAvailable"));
        Add("P0R-05", "recovery cannot impersonate original artifacts", !Bool(provenance, "recoveryMayImpersonateOriginal"));
        Add("P0R-06", "all seven referenced missing original artifacts remain explicit", provenance.GetProperty("missingOriginalArtifacts").GetArrayLength() == 7);

        var catalog = root.GetProperty("commandCatalog");
        var expectedOriginal = Int(catalog, "originalP1CompletionReviewCount");
        var recovered = Int(catalog, "recoveredP0ApiCommandCount");
        var unidentified = Int(catalog, "unidentifiedOriginalCommandCount");
        var commands = catalog.GetProperty("commands").EnumerateArray().ToArray();
        Add("P0R-07", "original/recovered/unidentified command cardinality is arithmetically consistent", expectedOriginal == 28 && recovered == 21 && unidentified == 7 && recovered + unidentified == expectedOriginal);
        Add("P0R-08", "exactly 21 API-draft mutating commands are recovered", commands.Length == 21);
        Add("P0R-09", "recovered command IDs are unique", Unique(commands.Select(x => Str(x, "id"))));
        Add("P0R-10", "recovered command endpoints are unique", Unique(commands.Select(x => Str(x, "endpoint"))));
        Add("P0R-11", "all recovered command endpoints remain POST", commands.All(x => Str(x, "method") == "POST"));
        Add("P0R-12", "all recovered API commands retain PROPOSED_FOR_P1 source status", commands.All(x => Str(x, "sourceStatus") == "PROPOSED_FOR_P1"));
        Add("P0R-13", "all recovered commands are marked partial evidence", commands.All(x => Str(x, "recoveryStatus") == "PARTIAL_EVIDENCE"));
        Add("P0R-14", "no recovered product command is executable", commands.All(x => !Bool(x, "executable")));
        Add("P0R-15", "no command claims a complete recovered state contract", commands.All(x => !Bool(x, "completeStateContract")));
        Add("P0R-16", "no command claims a complete recovered rule contract", commands.All(x => !Bool(x, "completeRuleContract")));
        Add("P0R-17", "no command claims a complete recovered event contract", commands.All(x => !Bool(x, "completeEventContract")));
        Add("P0R-18", "every fail-closed command carries a reason", commands.All(x => !string.IsNullOrWhiteSpace(Str(x, "failClosedReason"))));
        Add("P0R-19", "seven missing original command identities are not invented", catalog.GetProperty("unidentifiedOriginalCommandIds").ValueKind == JsonValueKind.Null && Bool(catalog, "mustNotInferMissingCommandIdentities"));

        var eventRecovery = root.GetProperty("eventRecovery");
        Add("P0R-20", "G01 to Case official event remains unresolved", Str(eventRecovery, "g01ToCaseOfficialEvent") == "TBD_UNRECOVERED");
        Add("P0R-21", "G02 to Need official event remains unresolved", Str(eventRecovery, "g02ToNeedOfficialEvent") == "TBD_UNRECOVERED");
        Add("P0R-22", "original P1 event stabilization is recorded without fabricating names", Bool(eventRecovery, "originalP1ReviewSaysTwoUnnamedP0EventsWereStabilized") && !Bool(eventRecovery, "stabilizedNamesRecovered"));
        Add("P0R-23", "known explicit Idea approval event is retained", ArrayStrings(eventRecovery, "knownExplicitVersionedEvents").Contains("IdeaApprovedForPortfolio.v1", StringComparer.Ordinal));
        Add("P0R-24", "known explicit Benefit handoff event is retained", ArrayStrings(eventRecovery, "knownExplicitVersionedEvents").Contains("BenefitHandoffRequested.v1", StringComparer.Ordinal));

        var policy = root.GetProperty("executionPolicy");
        Add("P0R-25", "product commands default fail closed", Str(policy, "productCommandDefault") == "FAIL_CLOSED");
        Add("P0R-26", "recovery cannot auto-enable a product command", Bool(policy, "noRecoveredCommandIsAutomaticallyExecutable"));
        var required = ArrayStrings(policy, "requiredForExecutable");
        Add("P0R-27", "executable gate requires complete state, rule and event contracts", required.Contains("completeStateContract") && required.Contains("completeRuleContract") && required.Contains("eventContract"));
        Add("P0R-28", "executable gate retains concurrency, idempotency and atomic persistence", required.Contains("version") && required.Contains("idempotency") && required.Contains("atomicPersistence"));

        var invariants = root.GetProperty("productInvariants").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        Add("P0R-29", "Dynamic Evaluation Plan remains G04 source of truth", invariants.Any(x => x.Contains("Dynamic Evaluation Plan", StringComparison.Ordinal)));
        Add("P0R-30", "100 percent progress is not Completion", invariants.Any(x => x.Contains("Progress 100 percent is not Completion", StringComparison.Ordinal)));
        Add("P0R-31", "Benefit phases remain distinct", invariants.Any(x => x.Contains("Benefit Claim, Verified, Attributed and Realized", StringComparison.Ordinal)));

        var sod = root.GetProperty("separationOfDuties").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        Add("P0R-32", "six frozen SoD boundaries are retained", sod.Length == 6);
        Add("P0R-33", "G04 committee and final decision authority remain separated", sod.Any(x => x.Contains("G04 Committee Member", StringComparison.Ordinal) && x.Contains("not final", StringComparison.Ordinal)));
        Add("P0R-34", "Knowledge validation and publication roles remain separated", sod.Any(x => x.Contains("Knowledge Steward", StringComparison.Ordinal) && x.Contains("Knowledge Publisher", StringComparison.Ordinal)));

        var states = root.GetProperty("stateFragments");
        Add("P0R-35", "G03 submission target state is explicit", Str(states.GetProperty("needs.submit-g03"), "targetState") == "PENDING_G03_REVIEW");
        Add("P0R-36", "G04 approval fragment retains versioned Portfolio event", G04ApprovalEvent(states.GetProperty("g04.final-decision")) == "IdeaApprovedForPortfolio.v1");
        Add("P0R-37", "Benefit recovered state fragments preserve ordered milestones", BenefitFragmentsAreConsistent(states));
        Add("P0R-38", "Execution completion submission requires ACTIVE and enters COMPLETION_REVIEW", Str(states.GetProperty("executions.submit-completion"), "fromState") == "ACTIVE" && Str(states.GetProperty("executions.submit-completion"), "toState") == "COMPLETION_REVIEW");
        Add("P0R-39", "Execution independent completion fragment ends at COMPLETED", Str(states.GetProperty("executions.completion-review"), "approveTo") == "COMPLETED");
        Add("P0R-40", "G02 Need creation fragment starts Need at DRAFT", G02NeedInitialState(states.GetProperty("g02.decide")) == "DRAFT");

        var authority = root.GetProperty("serverAuthorityInvariants").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        Add("P0R-41", "server authority retains expectedVersion enforcement", authority.Any(x => x.Contains("expectedVersion", StringComparison.Ordinal)));
        Add("P0R-42", "server authority retains atomic State+Audit+Outbox", authority.Any(x => x.Contains("State + Audit + Event Outbox", StringComparison.Ordinal)));
        Add("P0R-43", "UI visibility is never accepted as authorization", authority.Any(x => x.Contains("UI role visibility is never", StringComparison.Ordinal)));
        Add("P0R-44", "audit identity/assignment/version/correlation evidence is retained", authority.Any(x => x.Contains("PersonID + Role + Assignment + IdentitySource + EntityVersion + RuleSet + Timestamp + Correlation", StringComparison.Ordinal)));

        var tbd = root.GetProperty("openTbd").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        Add("P0R-45", "Oracle environment remains TBD rather than guessed", tbd.Any(x => x.Contains("Oracle version/connectivity/service account/schema owner", StringComparison.Ordinal)));
        Add("P0R-46", "production archive retention remains organizational TBD", tbd.Any(x => x.Contains("Production archive retention", StringComparison.Ordinal)));

        Add("P0R-47", "critical recovered roles match P0 API draft", Role(commands, "g01.decide") == "INTAKE_STEWARD" && Role(commands, "g02.decide") == "CASE_REVIEWER" && Role(commands, "g04.final-decision") == "IDEA_DECISION" && Role(commands, "knowledge.publish") == "KNOWLEDGE_PUBLISHER" && Role(commands, "rewards.decide") == "REWARD_COMMITTEE");
        Add("P0R-48", "no command can become executable by a single completeness flag", commands.All(x => !(Bool(x, "completeStateContract") && Bool(x, "executable"))));
        Add("P0R-49", "recovered command IDs align exactly with the P1 authority recovery catalog", commands.Select(x => Str(x, "id")).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(ExpectedCommandIds.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal));

        return checks;
    }

    public static bool IsValid(string json) => Validate(json).All(x => x.Passed);

    private static string Str(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static bool Bool(JsonElement element, string property) => element.TryGetProperty(property, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) && value.GetBoolean();
    private static int Int(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : int.MinValue;
    private static bool Unique(IEnumerable<string> values) { var a = values.ToArray(); return a.All(x => !string.IsNullOrWhiteSpace(x)) && a.Distinct(StringComparer.Ordinal).Count() == a.Length; }
    private static string[] ArrayStrings(JsonElement element, string property) => element.GetProperty(property).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
    private static string Role(JsonElement[] commands, string id) => Str(commands.Single(x => Str(x, "id") == id), "requiredRole");

    private static string G04ApprovalEvent(JsonElement fragment)
    {
        foreach (var outcome in fragment.GetProperty("outcomes").EnumerateArray())
            if (Str(outcome, "decision") == "APPROVE") return Str(outcome, "event");
        return string.Empty;
    }

    private static string G02NeedInitialState(JsonElement fragment)
    {
        foreach (var outcome in fragment.GetProperty("outcomes").EnumerateArray())
            if (Str(outcome, "decision") == "NEED_CANDIDATE" && outcome.TryGetProperty("creates", out var creates)) return Str(creates, "initialState");
        return string.Empty;
    }

    private static bool BenefitFragmentsAreConsistent(JsonElement states)
    {
        return Pair(states, "benefits.accept", "OBLIGATION_PENDING_ACCEPTANCE", "BASELINE_REQUIRED")
            && Pair(states, "benefits.measure", "MEASUREMENT_PENDING", "MEASURED")
            && Pair(states, "benefits.verify", "MEASURED", "VERIFIED")
            && Pair(states, "benefits.attribution", "VERIFIED", "VALIDATED")
            && Pair(states, "benefits.realize", "VALIDATED", "REALIZED");
    }

    private static bool Pair(JsonElement states, string name, string from, string to)
    {
        var item = states.GetProperty(name);
        return Str(item, "fromState") == from && Str(item, "toState") == to;
    }
}
