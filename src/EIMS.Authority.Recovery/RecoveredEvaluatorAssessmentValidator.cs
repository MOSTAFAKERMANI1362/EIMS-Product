using System.Text.Json;

namespace EIMS.Authority.Recovery;

public sealed class RecoveredEvaluatorAssessmentValidator : IEvaluatorAssessmentValidator
{
    public const string ExpectedRegistrySchema = "EIMS-EVALUATOR-ASSESSMENT-SCHEMA-REGISTRY-1.0";
    public const string ExpectedRecoveryStatus = "RECOVERED_FROM_FROZEN_V6_360_NOT_ORIGINAL_SERVER_SCHEMA";

    private readonly Dictionary<string, RoleContract> _roles;
    private readonly HashSet<string> _genericAnswers;
    private readonly HashSet<string> _genericOutcomes;
    private readonly int _genericNoteMin;
    private readonly int _genericEvidenceMin;
    private readonly int _genericCommentMin;
    private readonly int _genericScoreMin;
    private readonly int _genericScoreMax;

    public RecoveredEvaluatorAssessmentValidator(string registryJson)
    {
        using var document = JsonDocument.Parse(registryJson);
        var root = document.RootElement;
        if (S(root, "schema") != ExpectedRegistrySchema || S(root, "recoveryStatus") != ExpectedRecoveryStatus)
            throw new InvalidOperationException("Evaluator schema registry identity/provenance is not accepted.");

        var binding = root.GetProperty("serverBinding");
        if (!B(binding, "schemaVersionMustBeBoundToEvaluationAssignment")
            || !B(binding, "schemaSelectedByServerFromEvaluationRole")
            || B(binding, "clientMaySelectArbitrarySchemaVersion")
            || B(binding, "clientMaySubstituteDifferentRoleSchema"))
            throw new InvalidOperationException("Evaluator schema registry server-binding invariants are not satisfied.");

        var shared = root.GetProperty("sharedGenericSpecialistContract");
        var perQuestion = shared.GetProperty("perQuestion");
        var overall = shared.GetProperty("overall");
        _genericAnswers = OptionValues(perQuestion, "answerOptions");
        _genericOutcomes = OptionValues(overall, "outcomeOptions");
        _genericNoteMin = I(perQuestion, "noteMinLength");
        _genericEvidenceMin = I(overall, "evidenceRefMinLength");
        _genericCommentMin = I(overall, "commentMinLength");
        _genericScoreMin = I(overall, "scoreMin");
        _genericScoreMax = I(overall, "scoreMax");
        var genericRoles = shared.GetProperty("appliesToRoles").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _roles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in root.GetProperty("roleSchemas").EnumerateArray())
        {
            var role = S(source, "role");
            var id = S(source, "schemaId");
            var version = S(source, "schemaVersion");
            var kind = S(source, "kind");
            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("Evaluator role schema is incomplete.");

            if (kind == "GENERIC_SPECIALIST_CHECKLIST")
            {
                if (!genericRoles.Contains(role) || !B(source, "usesSharedGenericSpecialistContract"))
                    throw new InvalidOperationException($"Generic evaluator role '{role}' is not bound to the shared contract.");
                var questions = source.GetProperty("questions").EnumerateArray().Select(x => S(x, "key")).ToArray();
                _roles.Add(role, new(role, id, version, kind, Questions: questions));
            }
            else if (kind == "WEIGHTED_STRUCTURED_EVALUATION")
            {
                var criteria = Criteria(source);
                _roles.Add(role, new(role, id, version, kind,
                    Criteria: criteria,
                    Outcomes: OptionValues(source.GetProperty("recommendation"), "options"),
                    FinalNoteMin: I(source.GetProperty("evaluatorNote"), "minLength")));
            }
            else if (kind == "TECHNICAL_ASSESSMENT")
            {
                var condition = source.GetProperty("conditions");
                _roles.Add(role, new(role, id, version, kind,
                    Criteria: Criteria(source),
                    Outcomes: OptionValues(source.GetProperty("decision"), "options"),
                    Pilots: OptionValues(source.GetProperty("pilotRecommendation"), "options"),
                    EvidenceMin: I(source.GetProperty("evidenceRef"), "minLength"),
                    SummaryMin: I(source.GetProperty("summary"), "minLength"),
                    ConditionalMin: I(condition, "minLengthWhenRequired")));
            }
            else
                throw new InvalidOperationException($"Unsupported recovered evaluator schema kind '{kind}'.");
        }

        var expected = new[] { "IDEA_EVALUATOR", "TECHNICAL_ASSESSOR", "UNIT_OWNER_REVIEWER", "HSE_ASSESSOR", "FINANCIAL_ASSESSOR", "IT_ASSESSOR" };
        if (_roles.Count != expected.Length || expected.Any(x => !_roles.ContainsKey(x)))
            throw new InvalidOperationException("Evaluator schema registry must contain exactly the six recovered roles.");
    }

    public AssessmentValidationResult Validate(string role, string rawAssessmentJson)
    {
        if (string.IsNullOrWhiteSpace(role) || !_roles.TryGetValue(role.Trim(), out var schema))
            return AssessmentValidationResult.Fail("P1_EVALUATION_SCHEMA_NOT_BOUND", "No recovered schema is bound to evaluator role.");

        try
        {
            using var document = JsonDocument.Parse(rawAssessmentJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_SHAPE_INVALID");

            return schema.Kind switch
            {
                "GENERIC_SPECIALIST_CHECKLIST" => ValidateGeneric(schema, document.RootElement),
                "WEIGHTED_STRUCTURED_EVALUATION" => ValidateStructured(schema, document.RootElement),
                "TECHNICAL_ASSESSMENT" => ValidateTechnical(schema, document.RootElement),
                _ => AssessmentValidationResult.Fail("P1_EVALUATION_SCHEMA_KIND_UNSUPPORTED")
            };
        }
        catch (JsonException)
        {
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_JSON_INVALID");
        }
    }

    private AssessmentValidationResult ValidateGeneric(RoleContract schema, JsonElement root)
    {
        if (!Exact(root, "answers", "outcome", "score", "evidenceRef", "comment"))
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_FIELDS_INVALID");
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            return AssessmentValidationResult.Fail("P1_EVALUATION_ANSWERS_REQUIRED");

        var questions = schema.Questions ?? Array.Empty<string>();
        if (!Exact(answers, questions))
            return AssessmentValidationResult.Fail("P1_EVALUATION_QUESTION_SET_INVALID");

        var normalizedAnswers = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in questions.OrderBy(x => x, StringComparer.Ordinal))
        {
            var item = answers.GetProperty(key);
            if (item.ValueKind != JsonValueKind.Object || !Exact(item, "answer", "note"))
                return AssessmentValidationResult.Fail("P1_EVALUATION_ANSWER_SHAPE_INVALID", key);
            var answer = S(item, "answer");
            var note = S(item, "note").Trim();
            if (!_genericAnswers.Contains(answer) || note.Length < _genericNoteMin)
                return AssessmentValidationResult.Fail("P1_EVALUATION_ANSWER_INVALID", key);
            normalizedAnswers[key] = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["answer"] = answer, ["note"] = note };
        }

        var outcome = S(root, "outcome");
        if (!_genericOutcomes.Contains(outcome))
            return AssessmentValidationResult.Fail("P1_EVALUATION_OUTCOME_INVALID");
        if (!TryInt(root, "score", out var score) || score < _genericScoreMin || score > _genericScoreMax)
            return AssessmentValidationResult.Fail("P1_EVALUATION_SCORE_INVALID");
        var evidence = S(root, "evidenceRef").Trim();
        var comment = S(root, "comment").Trim();
        if (evidence.Length < _genericEvidenceMin) return AssessmentValidationResult.Fail("P1_EVALUATION_EVIDENCE_REQUIRED");
        if (comment.Length < _genericCommentMin) return AssessmentValidationResult.Fail("P1_EVALUATION_COMMENT_REQUIRED");

        return Pass(schema, outcome, new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["answers"] = normalizedAnswers,
            ["comment"] = comment,
            ["evidenceRef"] = evidence,
            ["outcome"] = outcome,
            ["score"] = score
        });
    }

    private AssessmentValidationResult ValidateStructured(RoleContract schema, JsonElement root)
    {
        if (!Exact(root, "criteria", "recommendation", "evaluatorNote"))
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_FIELDS_INVALID");
        if (!root.TryGetProperty("criteria", out var source) || source.ValueKind != JsonValueKind.Object)
            return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERIA_REQUIRED");

        var normalized = ValidateCriteria(schema, source, out var error);
        if (error is not null) return error;
        var outcome = S(root, "recommendation");
        if (!(schema.Outcomes ?? []).Contains(outcome)) return AssessmentValidationResult.Fail("P1_EVALUATION_RECOMMENDATION_INVALID");
        var note = S(root, "evaluatorNote").Trim();
        if (note.Length < schema.FinalNoteMin) return AssessmentValidationResult.Fail("P1_EVALUATION_FINAL_NOTE_REQUIRED");

        return Pass(schema, outcome, new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["criteria"] = normalized,
            ["evaluatorNote"] = note,
            ["recommendation"] = outcome
        });
    }

    private AssessmentValidationResult ValidateTechnical(RoleContract schema, JsonElement root)
    {
        var allowed = new HashSet<string>(["criteria", "decision", "pilotRecommendation", "evidenceRef", "summary", "conditions"], StringComparer.Ordinal);
        if (root.EnumerateObject().Any(x => !allowed.Contains(x.Name))
            || !new[] { "criteria", "decision", "pilotRecommendation", "evidenceRef", "summary" }.All(name => root.TryGetProperty(name, out _)))
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_FIELDS_INVALID");

        var source = root.GetProperty("criteria");
        if (source.ValueKind != JsonValueKind.Object) return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERIA_REQUIRED");
        var normalized = ValidateCriteria(schema, source, out var error);
        if (error is not null) return error;

        var outcome = S(root, "decision");
        if (!(schema.Outcomes ?? []).Contains(outcome)) return AssessmentValidationResult.Fail("P1_EVALUATION_TECHNICAL_DECISION_INVALID");
        var pilot = S(root, "pilotRecommendation");
        if (!(schema.Pilots ?? []).Contains(pilot)) return AssessmentValidationResult.Fail("P1_EVALUATION_PILOT_RECOMMENDATION_INVALID");
        var evidence = S(root, "evidenceRef").Trim();
        var summary = S(root, "summary").Trim();
        var conditions = root.TryGetProperty("conditions", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() ?? string.Empty : string.Empty;
        if (evidence.Length < schema.EvidenceMin) return AssessmentValidationResult.Fail("P1_EVALUATION_EVIDENCE_REQUIRED");
        if (summary.Length < schema.SummaryMin) return AssessmentValidationResult.Fail("P1_EVALUATION_SUMMARY_REQUIRED");
        if (outcome == "CONDITIONAL_PASS" && conditions.Length < schema.ConditionalMin) return AssessmentValidationResult.Fail("P1_EVALUATION_CONDITIONS_REQUIRED");

        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["criteria"] = normalized,
            ["decision"] = outcome,
            ["evidenceRef"] = evidence,
            ["pilotRecommendation"] = pilot,
            ["summary"] = summary
        };
        if (conditions.Length > 0) result["conditions"] = conditions;
        return Pass(schema, outcome, result);
    }

    private static SortedDictionary<string, object?> ValidateCriteria(RoleContract schema, JsonElement source, out AssessmentValidationResult? error)
    {
        error = null;
        var definitions = schema.Criteria ?? Array.Empty<Criterion>();
        if (!Exact(source, definitions.Select(x => x.Key)))
        {
            error = AssessmentValidationResult.Fail("P1_EVALUATION_CRITERIA_SET_INVALID");
            return new(StringComparer.Ordinal);
        }

        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in definitions.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var item = source.GetProperty(definition.Key);
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(x => x.Name is not ("score" or "note")) || !item.TryGetProperty("score", out _))
            {
                error = AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_SHAPE_INVALID", definition.Key);
                return normalized;
            }
            if (!TryInt(item, "score", out var score) || score < definition.Min || score > definition.Max)
            {
                error = AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_SCORE_INVALID", definition.Key);
                return normalized;
            }
            var note = item.TryGetProperty("note", out var noteValue) && noteValue.ValueKind == JsonValueKind.String ? noteValue.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (definition.NoteRequired && note.Length == 0)
            {
                error = AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_NOTE_REQUIRED", definition.Key);
                return normalized;
            }
            normalized[definition.Key] = note.Length == 0
                ? new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["score"] = score }
                : new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["note"] = note, ["score"] = score };
        }
        return normalized;
    }

    private static AssessmentValidationResult Pass(RoleContract schema, string outcome, object normalized) =>
        new(true, "P1_EVALUATION_ASSESSMENT_VALID", schema.SchemaId, schema.SchemaVersion, outcome,
            JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = false }));

    private static Criterion[] Criteria(JsonElement source) => source.GetProperty("criteria").EnumerateArray()
        .Select(x => new Criterion(S(x, "key"), I(x, "scoreMin"), I(x, "scoreMax"), B(x, "noteRequired"))).ToArray();

    private static HashSet<string> OptionValues(JsonElement source, string property) => source.GetProperty(property).EnumerateArray()
        .Select(x => S(x, "value")).ToHashSet(StringComparer.Ordinal);

    private static bool Exact(JsonElement source, params string[] expected) => Exact(source, (IEnumerable<string>)expected);
    private static bool Exact(JsonElement source, IEnumerable<string> expected)
    {
        var set = expected.ToHashSet(StringComparer.Ordinal);
        var actual = source.EnumerateObject().Select(x => x.Name).ToArray();
        return actual.Length == set.Count && actual.All(set.Contains);
    }

    private static string S(JsonElement source, string property) =>
        source.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static bool B(JsonElement source, string property) =>
        source.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    private static int I(JsonElement source, string property) => source.GetProperty(property).GetInt32();
    private static bool TryInt(JsonElement source, string property, out int value)
    {
        value = 0;
        return source.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value);
    }

    private sealed record Criterion(string Key, int Min, int Max, bool NoteRequired);
    private sealed record RoleContract(
        string Role,
        string SchemaId,
        string SchemaVersion,
        string Kind,
        IReadOnlyCollection<string>? Questions = null,
        IReadOnlyCollection<Criterion>? Criteria = null,
        HashSet<string>? Outcomes = null,
        HashSet<string>? Pilots = null,
        int FinalNoteMin = 0,
        int EvidenceMin = 0,
        int SummaryMin = 0,
        int ConditionalMin = 0);
}
