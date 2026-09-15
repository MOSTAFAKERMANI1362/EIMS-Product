using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EIMS.Authority.Recovery;

public sealed class RecoveredEvaluatorAssessmentValidator : IEvaluatorAssessmentValidator
{
    public const string ExpectedRegistrySchema = "EIMS-EVALUATOR-ASSESSMENT-SCHEMA-REGISTRY-1.0";
    public const string ExpectedRecoveryStatus = "RECOVERED_FROM_FROZEN_V6_360_NOT_ORIGINAL_SERVER_SCHEMA";

    private readonly string[] _genericAnswers;
    private readonly string[] _genericOutcomes;
    private readonly int _genericNoteMin;
    private readonly int _genericEvidenceMin;
    private readonly int _genericCommentMin;
    private readonly int _genericScoreMin;
    private readonly int _genericScoreMax;
    private readonly Dictionary<string, RoleSchema> _roles;

    public RecoveredEvaluatorAssessmentValidator(string registryJson)
    {
        using var document = JsonDocument.Parse(registryJson);
        var root = document.RootElement;
        if (GetString(root, "schema") != ExpectedRegistrySchema
            || GetString(root, "recoveryStatus") != ExpectedRecoveryStatus)
            throw new InvalidOperationException("Evaluator schema registry identity/provenance is not accepted.");

        var binding = root.GetProperty("serverBinding");
        if (!GetBool(binding, "schemaVersionMustBeBoundToEvaluationAssignment")
            || !GetBool(binding, "schemaSelectedByServerFromEvaluationRole")
            || GetBool(binding, "clientMaySelectArbitrarySchemaVersion")
            || GetBool(binding, "clientMaySubstituteDifferentRoleSchema"))
            throw new InvalidOperationException("Evaluator schema registry server-binding invariants are not satisfied.");

        var shared = root.GetProperty("sharedGenericSpecialistContract");
        var perQuestion = shared.GetProperty("perQuestion");
        var overall = shared.GetProperty("overall");
        _genericAnswers = OptionValues(perQuestion, "answerOptions");
        _genericOutcomes = OptionValues(overall, "outcomeOptions");
        _genericNoteMin = perQuestion.GetProperty("noteMinLength").GetInt32();
        _genericEvidenceMin = overall.GetProperty("evidenceRefMinLength").GetInt32();
        _genericCommentMin = overall.GetProperty("commentMinLength").GetInt32();
        _genericScoreMin = overall.GetProperty("scoreMin").GetInt32();
        _genericScoreMax = overall.GetProperty("scoreMax").GetInt32();

        var genericRoles = shared.GetProperty("appliesToRoles").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _roles = new Dictionary<string, RoleSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in root.GetProperty("roleSchemas").EnumerateArray())
        {
            var roleCode = GetString(role, "role");
            var schemaId = GetString(role, "schemaId");
            var schemaVersion = GetString(role, "schemaVersion");
            var kind = GetString(role, "kind");
            if (string.IsNullOrWhiteSpace(roleCode) || string.IsNullOrWhiteSpace(schemaId) || string.IsNullOrWhiteSpace(schemaVersion))
                throw new InvalidOperationException("Evaluator role schema is incomplete.");

            if (string.Equals(kind, "GENERIC_SPECIALIST_CHECKLIST", StringComparison.Ordinal))
            {
                if (!genericRoles.Contains(roleCode) || !GetBool(role, "usesSharedGenericSpecialistContract"))
                    throw new InvalidOperationException($"Generic evaluator role '{roleCode}' is not bound to the shared contract.");

                var questions = role.GetProperty("questions").EnumerateArray()
                    .Select(x => GetString(x, "key"))
                    .ToArray();
                _roles.Add(roleCode, new RoleSchema(roleCode, schemaId, schemaVersion, kind, questions));
                continue;
            }

            if (string.Equals(kind, "WEIGHTED_STRUCTURED_EVALUATION", StringComparison.Ordinal))
            {
                var criteria = role.GetProperty("criteria").EnumerateArray()
                    .Select(x => new Criterion(
                        GetString(x, "key"),
                        x.GetProperty("scoreMin").GetInt32(),
                        x.GetProperty("scoreMax").GetInt32(),
                        GetBool(x, "noteRequired")))
                    .ToArray();
                var recommendation = OptionValues(role.GetProperty("recommendation"), "options");
                var noteMin = role.GetProperty("evaluatorNote").GetProperty("minLength").GetInt32();
                _roles.Add(roleCode, new RoleSchema(roleCode, schemaId, schemaVersion, kind, Criteria: criteria, OutcomeOptions: recommendation, FinalNoteMin: noteMin));
                continue;
            }

            if (string.Equals(kind, "TECHNICAL_ASSESSMENT", StringComparison.Ordinal))
            {
                var criteria = role.GetProperty("criteria").EnumerateArray()
                    .Select(x => new Criterion(
                        GetString(x, "key"),
                        x.GetProperty("scoreMin").GetInt32(),
                        x.GetProperty("scoreMax").GetInt32(),
                        GetBool(x, "noteRequired")))
                    .ToArray();
                var decisions = OptionValues(role.GetProperty("decision"), "options");
                var pilots = OptionValues(role.GetProperty("pilotRecommendation"), "options");
                var evidenceMin = role.GetProperty("evidenceRef").GetProperty("minLength").GetInt32();
                var summaryMin = role.GetProperty("summary").GetProperty("minLength").GetInt32();
                var condition = role.GetProperty("conditions");
                var conditionMin = condition.GetProperty("minLengthWhenRequired").GetInt32();
                _roles.Add(roleCode, new RoleSchema(roleCode, schemaId, schemaVersion, kind, Criteria: criteria, OutcomeOptions: decisions, PilotOptions: pilots, EvidenceMin: evidenceMin, SummaryMin: summaryMin, ConditionalTextMin: conditionMin));
                continue;
            }

            throw new InvalidOperationException($"Unsupported recovered evaluator schema kind '{kind}'.");
        }

        var expectedRoles = new[]
        {
            "IDEA_EVALUATOR", "TECHNICAL_ASSESSOR", "UNIT_OWNER_REVIEWER",
            "HSE_ASSESSOR", "FINANCIAL_ASSESSOR", "IT_ASSESSOR"
        };
        if (_roles.Count != expectedRoles.Length || expectedRoles.Any(x => !_roles.ContainsKey(x)))
            throw new InvalidOperationException("Evaluator schema registry must contain exactly the six recovered roles.");
    }

    public AssessmentValidationResult Validate(string role, string rawAssessmentJson)
    {
        if (string.IsNullOrWhiteSpace(role) || !_roles.TryGetValue(role.Trim(), out var schema))
            return AssessmentValidationResult.Fail("P1_EVALUATION_SCHEMA_NOT_BOUND", "No recovered schema is bound to evaluator role.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawAssessmentJson);
        }
        catch (JsonException)
        {
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_JSON_INVALID", "Assessment body is not valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_SHAPE_INVALID", "Assessment body must be a JSON object.");

            return schema.Kind switch
            {
                "GENERIC_SPECIALIST_CHECKLIST" => ValidateGeneric(schema, document.RootElement),
                "WEIGHTED_STRUCTURED_EVALUATION" => ValidateStructured(schema, document.RootElement),
                "TECHNICAL_ASSESSMENT" => ValidateTechnical(schema, document.RootElement),
                _ => AssessmentValidationResult.Fail("P1_EVALUATION_SCHEMA_KIND_UNSUPPORTED")
            };
        }
    }

    private AssessmentValidationResult ValidateGeneric(RoleSchema schema, JsonElement root)
    {
        var allowedTop = new HashSet<string>(new[] { "answers", "outcome", "score", "evidenceRef", "comment" }, StringComparer.Ordinal);
        if (!ExactProperties(root, allowedTop))
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_FIELDS_INVALID", "Generic specialist assessment contains missing or unknown top-level fields.");

        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            return AssessmentValidationResult.Fail("P1_EVALUATION_ANSWERS_REQUIRED");

        var expectedQuestions = schema.QuestionKeys ?? Array.Empty<string>();
        if (!ExactProperties(answers, expectedQuestions.ToHashSet(StringComparer.Ordinal)))
            return AssessmentValidationResult.Fail("P1_EVALUATION_QUESTION_SET_INVALID", "Answer keys must match the recovered role question set exactly.");

        var normalizedAnswers = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in expectedQuestions.OrderBy(x => x, StringComparer.Ordinal))
        {
            var item = answers.GetProperty(key);
            if (item.ValueKind != JsonValueKind.Object || !ExactProperties(item, new HashSet<string>(new[] { "answer", "note" }, StringComparer.Ordinal)))
                return AssessmentValidationResult.Fail("P1_EVALUATION_ANSWER_SHAPE_INVALID", key);

            var answer = GetString(item, "answer");
            var note = GetString(item, "note").Trim();
            if (!_genericAnswers.Contains(answer, StringComparer.Ordinal) || note.Length < _genericNoteMin)
                return AssessmentValidationResult.Fail("P1_EVALUATION_ANSWER_INVALID", key);

            normalizedAnswers[key] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["answer"] = answer,
                ["note"] = note
            };
        }

        var outcome = GetString(root, "outcome");
        if (!_genericOutcomes.Contains(outcome, StringComparer.Ordinal))
            return AssessmentValidationResult.Fail("P1_EVALUATION_OUTCOME_INVALID");

        if (!TryInt(root, "score", out var score) || score < _genericScoreMin || score > _genericScoreMax)
            return AssessmentValidationResult.Fail("P1_EVALUATION_SCORE_INVALID");

        var evidence = GetString(root, "evidenceRef").Trim();
        var comment = GetString(root, "comment").Trim();
        if (evidence.Length < _genericEvidenceMin)
            return AssessmentValidationResult.Fail("P1_EVALUATION_EVIDENCE_REQUIRED");
        if (comment.Length < _genericCommentMin)
            return AssessmentValidationResult.Fail("P1_EVALUATION_COMMENT_REQUIRED");

        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["answers"] = normalizedAnswers,
            ["comment"] = comment,
            ["evidenceRef"] = evidence,
            ["outcome"] = outcome,
            ["score"] = score
        };
        return Pass(schema, outcome, normalized);
    }

    private AssessmentValidationResult ValidateStructured(RoleSchema schema, JsonElement root)
    {
        var allowedTop = new HashSet<string>(new[] { "criteria", "recommendation", "evaluatorNote" }, StringComparer.Ordinal);
        if (!ExactProperties(root, allowedTop))
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_FIELDS_INVALID");

        if (!root.TryGetProperty("criteria", out var criteria) || criteria.ValueKind != JsonValueKind.Object)
            return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERIA_REQUIRED");

        var definitions = schema.Criteria ?? Array.Empty<Criterion>();
        if (!ExactProperties(criteria, definitions.Select(x => x.Key).ToHashSet(StringComparer.Ordinal)))
            return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERIA_SET_INVALID");

        var normalizedCriteria = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in definitions.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var item = criteria.GetProperty(definition.Key);
            if (item.ValueKind != JsonValueKind.Object)
                return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_SHAPE_INVALID", definition.Key);

            var allowed = definition.NoteRequired
                ? new HashSet<string>(new[] { "score", "note" }, StringComparer.Ordinal)
                : new HashSet<string>(new[] { "score", "note" }, StringComparer.Ordinal);
            if (item.EnumerateObject().Any(x => !allowed.Contains(x.Name)) || !item.TryGetProperty("score", out _))
                return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_SHAPE_INVALID", definition.Key);

            if (!TryInt(item, "score", out var score) || score < definition.ScoreMin || score > definition.ScoreMax)
                return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_SCORE_INVALID", definition.Key);

            var note = item.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String
                ? noteElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (definition.NoteRequired && note.Length == 0)
                return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_NOTE_REQUIRED", definition.Key);

            normalizedCriteria[definition.Key] = note.Length == 0
                ? new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["score"] = score }
                : new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["note"] = note, ["score"] = score };
        }

        var recommendation = GetString(root, "recommendation");
        if (!(schema.OutcomeOptions ?? Array.Empty<string>()).Contains(recommendation, StringComparer.Ordinal))
            return AssessmentValidationResult.Fail("P1_EVALUATION_RECOMMENDATION_INVALID");
        var evaluatorNote = GetString(root, "evaluatorNote").Trim();
        if (evaluatorNote.Length < schema.FinalNoteMin)
            return AssessmentValidationResult.Fail("P1_EVALUATION_FINAL_NOTE_REQUIRED");

        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["criteria"] = normalizedCriteria,
            ["evaluatorNote"] = evaluatorNote,
            ["recommendation"] = recommendation
        };
        return Pass(schema, recommendation, normalized);
    }

    private AssessmentValidationResult ValidateTechnical(RoleSchema schema, JsonElement root)
    {
        var allowedTop = new HashSet<string>(new[] { "criteria", "decision", "pilotRecommendation", "evidenceRef", "summary", "conditions" }, StringComparer.Ordinal);
        if (root.EnumerateObject().Any(x => !allowedTop.Contains(x.Name))
            || !new[] { "criteria", "decision", "pilotRecommendation", "evidenceRef", "summary" }.All(root.TryGetProperty))
            return AssessmentValidationResult.Fail("P1_EVALUATION_ASSESSMENT_FIELDS_INVALID");

        var criteria = root.GetProperty("criteria");
        if (criteria.ValueKind != JsonValueKind.Object)
            return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERIA_REQUIRED");
        var definitions = schema.Criteria ?? Array.Empty<Criterion>();
        if (!ExactProperties(criteria, definitions.Select(x => x.Key).ToHashSet(StringComparer.Ordinal)))
            return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERIA_SET_INVALID");

        var normalizedCriteria = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in definitions.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var item = criteria.GetProperty(definition.Key);
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(x => x.Name is not ("score" or "note")) || !item.TryGetProperty("score", out _))
                return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_SHAPE_INVALID", definition.Key);
            if (!TryInt(item, "score", out var score) || score < definition.ScoreMin || score > definition.ScoreMax)
                return AssessmentValidationResult.Fail("P1_EVALUATION_CRITERION_SCORE_INVALID", definition.Key);
            var note = item.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String
                ? noteElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            normalizedCriteria[definition.Key] = note.Length == 0
                ? new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["score"] = score }
                : new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["note"] = note, ["score"] = score };
        }

        var decision = GetString(root, "decision");
        if (!(schema.OutcomeOptions ?? Array.Empty<string>()).Contains(decision, StringComparer.Ordinal))
            return AssessmentValidationResult.Fail("P1_EVALUATION_TECHNICAL_DECISION_INVALID");
        var pilot = GetString(root, "pilotRecommendation");
        if (!(schema.PilotOptions ?? Array.Empty<string>()).Contains(pilot, StringComparer.Ordinal))
            return AssessmentValidationResult.Fail("P1_EVALUATION_PILOT_RECOMMENDATION_INVALID");

        var evidence = GetString(root, "evidenceRef").Trim();
        var summary = GetString(root, "summary").Trim();
        var conditions = root.TryGetProperty("conditions", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() ?? string.Empty : string.Empty;
        if (evidence.Length < schema.EvidenceMin)
            return AssessmentValidationResult.Fail("P1_EVALUATION_EVIDENCE_REQUIRED");
        if (summary.Length < schema.SummaryMin)
            return AssessmentValidationResult.Fail("P1_EVALUATION_SUMMARY_REQUIRED");
        if (string.Equals(decision, "CONDITIONAL_PASS", StringComparison.Ordinal) && conditions.Length < schema.ConditionalTextMin)
            return AssessmentValidationResult.Fail("P1_EVALUATION_CONDITIONS_REQUIRED");

        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["criteria"] = normalizedCriteria,
            ["decision"] = decision,
            ["evidenceRef"] = evidence,
            ["pilotRecommendation"] = pilot,
            ["summary"] = summary
        };
        if (conditions.Length > 0)
            normalized["conditions"] = conditions;
        return Pass(schema, decision, normalized);
    }

    private static AssessmentValidationResult Pass(RoleSchema schema, string outcome, object normalized)
    {
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = false });
        return new AssessmentValidationResult(true, "P1_EVALUATION_ASSESSMENT_VALID", schema.SchemaId, schema.SchemaVersion, outcome, json);
    }

    private static bool ExactProperties(JsonElement element, HashSet<string> expected)
    {
        var actual = element.EnumerateObject().Select(x => x.Name).ToArray();
        return actual.Length == expected.Count && actual.All(expected.Contains);
    }

    private static string[] OptionValues(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(x => GetString(x, "value")).ToArray();

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static bool TryInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value);
    }

    private sealed record Criterion(string Key, int ScoreMin, int ScoreMax, bool NoteRequired);

    private sealed record RoleSchema(
        string Role,
        string SchemaId,
        string SchemaVersion,
        string Kind,
        IReadOnlyCollection<string>? QuestionKeys = null,
        IReadOnlyCollection<Criterion>? Criteria = null,
        IReadOnlyCollection<string>? OutcomeOptions = null,
        IReadOnlyCollection<string>? PilotOptions = null,
        int FinalNoteMin = 0,
        int EvidenceMin = 0,
        int SummaryMin = 0,
        int ConditionalTextMin = 0);
}
