using System.Text.Json;

namespace EIMS.Authority.Recovery;

public sealed class G01RuleExecutionComposer(IRuleEvaluator evaluator)
{
    public async ValueTask<IReadOnlyCollection<G01RuleExecutionSnapshot>> ComposeAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(command.CommandName, "g01.decide", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<G01RuleExecutionSnapshot>();

        using var document = JsonDocument.Parse(command.RawBody);
        var root = document.RootElement;

        var results = new List<G01RuleExecutionSnapshot>(4)
        {
            await EvaluateRule("R02", root, evaluator, command, actor, aggregate, policy, cancellationToken),
            await EvaluateRule("R03", root, evaluator, command, actor, aggregate, policy, cancellationToken),
            await EvaluateRule("R04", root, evaluator, command, actor, aggregate, policy, cancellationToken),
            await EvaluateRule("R05", root, evaluator, command, actor, aggregate, policy, cancellationToken)
        };
        return results.AsReadOnly();
    }

    private static async ValueTask<G01RuleExecutionSnapshot> EvaluateRule(
        string ruleId,
        JsonElement root,
        IRuleEvaluator evaluator,
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken)
    {
        var body = ruleId switch
        {
            "R02" => JsonSerializer.Serialize(new
            {
                originChannel = StringValue(root, "originChannel"),
                originDetail = StringValue(root, "originDetail")
            }),
            "R03" => BuildR03Body(root),
            "R04" => JsonSerializer.Serialize(new
            {
                sourceType = StringValue(root, "sourceType"),
                unit = StringValue(root, "unit")
            }),
            "R05" => JsonSerializer.Serialize(new
            {
                title = StringValue(root, "title"),
                desc = StringValue(root, "desc")
            }),
            _ => "{}"
        };

        var scopedCommand = command with { RawBody = body };
        var result = await evaluator.EvaluateAsync(scopedCommand, actor, aggregate, policy, cancellationToken);
        var outcome = result.Code switch
        {
            "G01_R03_WARNING" => "WARNING",
            _ when result.Code.StartsWith($"G01_{ruleId}_PASS", StringComparison.OrdinalIgnoreCase) => "PASS",
            _ when result.Code.StartsWith($"G01_{ruleId}_FAIL", StringComparison.OrdinalIgnoreCase) => "FAIL",
            _ when result.Code.StartsWith($"G01_{ruleId}_WARNING", StringComparison.OrdinalIgnoreCase) => "WARNING",
            _ => "ERROR"
        };

        return new G01RuleExecutionSnapshot(ruleId, outcome);
    }

    private static string BuildR03Body(JsonElement root)
    {
        if (root.TryGetProperty("r03Review", out var review))
            return JsonSerializer.Serialize(new { r03Review = review });

        return "{\"ruleResults\":{\"R03\":null}}";
    }

    private static string? StringValue(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public static class G01DecisionSnapshotFactory
{
    public static G01DecisionSnapshotEnvelope Create(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot before,
        CommandPolicy policy,
        DomainDecisionEnvelope decision,
        string gateOutcome,
        IReadOnlyCollection<G01RuleExecutionSnapshot> executions,
        DateTimeOffset createdAt)
    {
        var role = actor.Roles.FirstOrDefault(x => string.Equals(x, "INTAKE_STEWARD", StringComparison.OrdinalIgnoreCase))
            ?? actor.Roles.FirstOrDefault()
            ?? string.Empty;
        var scope = before.Scope ?? string.Empty;
        var authorization = new G01DecisionAuthorizationContext(
            actor.PersonId, role, "G01.DECIDE", scope, actor.AssignmentId);

        var reasonCode = ReadString(command.RawBody, "reasonCode");
        var comment = ReadString(command.RawBody, "comment");
        var observationVersion = before.Version;
        var ruleSetId = policy.RuleSet;
        var ruleSetVersion = "1.0";
        var schemaVersion = "1.0";
        var fingerprintMaterial = string.Join("\n", new[]
        {
            decision.DecisionId,
            before.AggregateId,
            observationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ruleSetId,
            ruleSetVersion,
            gateOutcome,
            string.Join("|", executions.Select(x => $"{x.RuleId}={x.Outcome}")),
            authorization.PrincipalId,
            authorization.Role,
            authorization.Capability,
            authorization.Scope,
            authorization.AssignmentId,
            decision.Outcome,
            reasonCode ?? string.Empty,
            comment ?? decision.Note ?? string.Empty,
            createdAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            schemaVersion
        });
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprintMaterial)))
            .ToLowerInvariant();

        return new G01DecisionSnapshotEnvelope(
            $"SNAP-{decision.DecisionId}",
            decision.DecisionId,
            before.AggregateId,
            observationVersion,
            ruleSetId,
            ruleSetVersion,
            gateOutcome,
            Array.AsReadOnly(executions.ToArray()),
            authorization,
            decision.Outcome,
            reasonCode,
            comment ?? decision.Note,
            createdAt,
            schemaVersion,
            fingerprint);
    }

    private static string? ReadString(string rawBody, string propertyName)
    {
        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
