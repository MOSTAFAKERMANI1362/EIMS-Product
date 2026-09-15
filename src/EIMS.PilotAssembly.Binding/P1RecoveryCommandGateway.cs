using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.PilotAssembly.Core;

namespace EIMS.PilotAssembly.Binding;

public interface IP1AuthorityKernelExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(AuthorityCommand command, AuthorityActor actor, CancellationToken cancellationToken = default);
}

public interface IP1EvaluationCompletionExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(EvaluationCompletionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default);
}

public interface IP1G04VoteExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(G04VoteCommand command, AuthorityActor actor, CancellationToken cancellationToken = default);
}

public interface IP1G04FinalDecisionExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(G04FinalDecisionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default);
}

public sealed class AuthorityKernelExecutor(AuthorityKernel kernel) : IP1AuthorityKernelExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(AuthorityCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        kernel.ExecuteAsync(command, actor, cancellationToken);
}

public sealed class EvaluationCompletionExecutor(EvaluationCompletionService service) : IP1EvaluationCompletionExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(EvaluationCompletionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        service.CompleteAsync(command, actor, cancellationToken);
}

public sealed class G04VoteExecutor(G04VotingServiceWave8 service) : IP1G04VoteExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(G04VoteCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        service.VoteAsync(command, actor, cancellationToken);
}

public sealed class G04FinalDecisionExecutor(G04FinalDecisionServiceWave8 service) : IP1G04FinalDecisionExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(G04FinalDecisionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        service.DecideAsync(command, actor, cancellationToken);
}

/// <summary>
/// P5 -> P3 -> P1 binding adapter for the six mutation commands recovered through Wave 8.
/// The client supplies only selectors (assignmentId/requestedScope); P3 resolves the authoritative
/// Person/Role/Scope from the server-side directory. Commands not recovered for mutation remain fail-closed.
/// </summary>
public sealed class P1RecoveryCommandGateway(
    WindowsIdentityRbacResolver identityResolver,
    IP1AuthorityKernelExecutor authorityKernel,
    IP1EvaluationCompletionExecutor evaluationCompletion,
    IP1G04VoteExecutor g04Vote,
    IP1G04FinalDecisionExecutor g04FinalDecision,
    ICommandPolicyCatalog? catalog = null) : ICommandGateway
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave8();

    private static readonly HashSet<string> KernelCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "needs.submit-g03",
        "needs.g03-decision",
        "ideas.submit-g04"
    };

    public async Task<CommandAttemptResult> ExecuteAsync(CommandAttempt attempt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var commandName = attempt.Command?.Trim().Trim('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandName))
            return Deny(400, "P5_COMMAND_REQUIRED", "Command name is required.");

        if (!_catalog.TryGet(commandName, out var policy))
            return Deny(404, "P5_COMMAND_UNKNOWN", "Command is not declared in the recovered Product command catalog.");

        if (!policy.MutationContractRecovered)
            return Deny(503, "P5_COMMAND_NOT_RECOVERED",
                "The command exists in the Product catalog but its server mutation contract is not recovered/bound.");

        if (string.IsNullOrWhiteSpace(attempt.NetworkIdentity))
            return Deny(401, "P5_WINDOWS_IDENTITY_REQUIRED", "Authenticated Windows identity is required.");
        if (string.IsNullOrWhiteSpace(attempt.CorrelationId))
            return Deny(400, "P5_CORRELATION_REQUIRED", "Correlation ID is required.");
        if (string.IsNullOrWhiteSpace(attempt.IdempotencyKey))
            return Deny(400, "P5_IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key is required.");

        JsonDocument body;
        try
        {
            body = JsonDocument.Parse(string.IsNullOrWhiteSpace(attempt.RawBody) ? "{}" : attempt.RawBody);
        }
        catch (JsonException)
        {
            return Deny(400, "P5_COMMAND_BODY_INVALID_JSON", "Command body must be valid JSON.");
        }

        using (body)
        {
            if (body.RootElement.ValueKind != JsonValueKind.Object)
                return Deny(400, "P5_COMMAND_BODY_OBJECT_REQUIRED", "Command body must be a JSON object.");

            try
            {
                var assignmentId = String(body.RootElement, "assignmentId");
                var requestedScope = String(body.RootElement, "requestedScope");
                var resolved = await identityResolver.ResolveAsync(
                    new IdentityResolutionRequest(attempt.NetworkIdentity, assignmentId, requestedScope, DateTimeOffset.UtcNow),
                    cancellationToken);
                if (!resolved.Allowed || resolved.Actor is null)
                    return Deny(resolved.HttpStatus, resolved.Code,
                        resolved.Detail ?? "Authoritative Person/Assignment/Role/Scope resolution failed.");

                AuthorityResult result;
                if (KernelCommands.Contains(commandName))
                {
                    var aggregateId = RequireString(body.RootElement, "aggregateId");
                    var expectedVersion = RequireExpectedVersion(attempt);
                    result = await authorityKernel.ExecuteAsync(
                        new AuthorityCommand(
                            commandName,
                            aggregateId,
                            expectedVersion,
                            attempt.IdempotencyKey!,
                            attempt.CorrelationId!,
                            attempt.RawBody,
                            requestedScope),
                        resolved.Actor,
                        cancellationToken);
                }
                else if (string.Equals(commandName, "evaluation-assignments.complete", StringComparison.OrdinalIgnoreCase))
                {
                    var assessmentJson = RequireRawJson(body.RootElement, "assessment");
                    result = await evaluationCompletion.ExecuteAsync(
                        new EvaluationCompletionCommand(
                            RequireString(body.RootElement, "ideaId"),
                            RequireExpectedVersion(attempt),
                            RequireString(body.RootElement, "planId"),
                            RequireInt32(body.RootElement, "expectedPlanVersion"),
                            RequireString(body.RootElement, "evaluationAssignmentId"),
                            RequireInt32(body.RootElement, "expectedAssignmentVersion"),
                            attempt.IdempotencyKey!,
                            attempt.CorrelationId!,
                            assessmentJson,
                            requestedScope),
                        resolved.Actor,
                        cancellationToken);
                }
                else if (string.Equals(commandName, "g04.vote", StringComparison.OrdinalIgnoreCase))
                {
                    result = await g04Vote.ExecuteAsync(
                        new G04VoteCommand(
                            RequireString(body.RootElement, "planId"),
                            RequireString(body.RootElement, "assessmentId"),
                            RequireExpectedVersion(attempt),
                            RequireInt32(body.RootElement, "expectedCommitteeVersion"),
                            attempt.IdempotencyKey!,
                            attempt.CorrelationId!,
                            RequireString(body.RootElement, "vote"),
                            RequireString(body.RootElement, "note"),
                            requestedScope),
                        resolved.Actor,
                        cancellationToken);
                }
                else if (string.Equals(commandName, "g04.final-decision", StringComparison.OrdinalIgnoreCase))
                {
                    result = await g04FinalDecision.ExecuteAsync(
                        new G04FinalDecisionCommand(
                            RequireString(body.RootElement, "ideaId"),
                            RequireExpectedVersion(attempt),
                            RequireInt64(body.RootElement, "expectedStateMutationVersion"),
                            RequireString(body.RootElement, "planId"),
                            RequireInt32(body.RootElement, "expectedPlanVersion"),
                            RequireString(body.RootElement, "assessmentId"),
                            RequireInt32(body.RootElement, "expectedAssessmentDecisionVersion"),
                            RequireString(body.RootElement, "outcome"),
                            RequireString(body.RootElement, "reasonCode"),
                            RequireString(body.RootElement, "decisionComment"),
                            OptionalDate(body.RootElement, "reviewDate"),
                            attempt.IdempotencyKey!,
                            attempt.CorrelationId!,
                            requestedScope),
                        resolved.Actor,
                        cancellationToken);
                }
                else
                {
                    return Deny(503, "P5_COMMAND_DISPATCH_NOT_BOUND",
                        "Recovered mutation command has no P5 dispatcher binding.");
                }

                return Map(result);
            }
            catch (CommandEnvelopeException ex)
            {
                return Deny(400, ex.Code, ex.Message);
            }
        }
    }

    private static CommandAttemptResult Map(AuthorityResult result) =>
        new(result.HttpStatus, result.Code, result.Detail ?? result.Code, result.StateMutated);

    private static CommandAttemptResult Deny(int status, string code, string message) =>
        new(status, code, message, false);

    private static long RequireExpectedVersion(CommandAttempt attempt) =>
        attempt.ExpectedVersion ?? throw new CommandEnvelopeException("P5_EXPECTED_VERSION_REQUIRED", "If-Match expected version is required.");

    private static string RequireString(JsonElement root, string name) =>
        String(root, name) ?? throw new CommandEnvelopeException("P5_COMMAND_FIELD_REQUIRED", $"Required command field '{name}' is missing.");

    private static string? String(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new CommandEnvelopeException("P5_COMMAND_FIELD_TYPE_INVALID", $"Command field '{name}' must be a string.");
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int RequireInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
            throw new CommandEnvelopeException("P5_COMMAND_FIELD_TYPE_INVALID", $"Command field '{name}' must be an integer.");
        return parsed;
    }

    private static long RequireInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed))
            throw new CommandEnvelopeException("P5_COMMAND_FIELD_TYPE_INVALID", $"Command field '{name}' must be an integer.");
        return parsed;
    }

    private static string RequireRawJson(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new CommandEnvelopeException("P5_COMMAND_FIELD_REQUIRED", $"Required command field '{name}' is missing.");
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }

    private static DateTimeOffset? OptionalDate(JsonElement root, string name)
    {
        var text = String(root, name);
        if (text is null) return null;
        if (!DateTimeOffset.TryParse(text, out var value))
            throw new CommandEnvelopeException("P5_COMMAND_FIELD_TYPE_INVALID", $"Command field '{name}' must be an ISO date/time.");
        return value;
    }

    private sealed class CommandEnvelopeException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
