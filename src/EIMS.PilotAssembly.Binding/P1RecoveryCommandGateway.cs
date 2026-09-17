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

public interface IP1PortfolioExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(PortfolioCommandWave9 command, AuthorityActor actor, CancellationToken cancellationToken = default);
}

public interface IP1ExecutionExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(ExecutionCommandWave10 command, AuthorityActor actor, CancellationToken cancellationToken = default);
}

public interface IP1BenefitExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(BenefitCommandWave11 command, AuthorityActor actor, CancellationToken cancellationToken = default);
}

public interface IP1KnowledgeExecutor
{
    ValueTask<AuthorityResult> ExecuteAsync(KnowledgeCommandWave13 command, AuthorityActor actor, CancellationToken cancellationToken = default);
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

public sealed class PortfolioExecutor(PortfolioServiceWave9 service) : IP1PortfolioExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(PortfolioCommandWave9 command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        service.ExecuteAsync(command, actor, cancellationToken);
}

/// <summary>
/// Production Execution binding intentionally wraps the guarded Wave10 facade, never the inner service.
/// </summary>
public sealed class ExecutionExecutor(ExecutionServiceWave10Guarded service) : IP1ExecutionExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(ExecutionCommandWave10 command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        service.ExecuteAsync(command, actor, cancellationToken);
}

public sealed class BenefitExecutor(BenefitServiceWave11 service) : IP1BenefitExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(BenefitCommandWave11 command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        service.ExecuteAsync(command, actor, cancellationToken);
}

public sealed class KnowledgeExecutor(KnowledgeServiceWave13 service) : IP1KnowledgeExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(KnowledgeCommandWave13 command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        service.ExecuteAsync(command, actor, cancellationToken);
}

/// <summary>
/// P5 -> P3 -> P1 binding adapter for the thirty-two user mutation commands recovered through Wave13.
/// The client supplies only selectors (assignmentId/requestedScope); P3 resolves authoritative
/// Person/Role/Scope from the server-side directory. Client ownership/author claims are never authority.
/// Commands not recovered for mutation remain fail-closed.
/// System-only Portfolio eligibility, Execution handoff intake and Benefit obligation intake remain outside
/// this user-command gateway. Knowledge source Benefit evidence and author policy are resolved server-side.
/// </summary>
public sealed class P1RecoveryCommandGateway(
    WindowsIdentityRbacResolver identityResolver,
    IP1AuthorityKernelExecutor authorityKernel,
    IP1EvaluationCompletionExecutor evaluationCompletion,
    IP1G04VoteExecutor g04Vote,
    IP1G04FinalDecisionExecutor g04FinalDecision,
    ICommandPolicyCatalog? catalog = null,
    IP1PortfolioExecutor? portfolio = null,
    IP1ExecutionExecutor? execution = null,
    IP1BenefitExecutor? benefit = null,
    IP1KnowledgeExecutor? knowledge = null) : ICommandGateway
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave13();
    private readonly IP1PortfolioExecutor? _portfolio = portfolio;
    private readonly IP1ExecutionExecutor? _execution = execution;
    private readonly IP1BenefitExecutor? _benefit = benefit;
    private readonly IP1KnowledgeExecutor? _knowledge = knowledge;

    private static readonly HashSet<string> KernelCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "needs.submit-g03",
        "needs.g03-decision",
        "ideas.submit-g04"
    };

    private static readonly HashSet<string> PortfolioCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "portfolio.assign-candidate",
        "portfolio.membership-decision",
        "portfolio.generate-execution-recommendation",
        "portfolio.approve-execution-recommendation",
        "portfolio.bind-approved-baseline",
        "portfolio.request-execution-handoff"
    };

    private static readonly HashSet<string> ExecutionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "executions.approve-charter",
        "executions.approve-plan-baseline",
        "executions.start",
        "executions.progress",
        "executions.submit-completion",
        "executions.completion-review",
        "executions.request-benefit-handoff",
        "executions.begin-closure",
        "executions.close"
    };

    private static readonly HashSet<string> BenefitCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "benefits.accept",
        "benefits.set-baseline",
        "benefits.approve-measurement-plan",
        "benefits.measure",
        "benefits.verify",
        "benefits.attribution",
        "benefits.realize",
        "benefits.close"
    };

    private static readonly HashSet<string> KnowledgeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "knowledge.create-draft",
        "knowledge.validate",
        "knowledge.publish"
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
                else if (PortfolioCommands.Contains(commandName))
                {
                    if (_portfolio is null)
                        return Deny(503, "P5_COMMAND_DISPATCH_NOT_BOUND",
                            "Portfolio mutation is recovered but the P1 Wave9 Portfolio executor is not composed into this gateway.");

                    result = await _portfolio.ExecuteAsync(
                        new PortfolioCommandWave9(
                            commandName,
                            RequireString(body.RootElement, "candidateId"),
                            RequireExpectedVersion(attempt),
                            attempt.IdempotencyKey!,
                            attempt.CorrelationId!,
                            PortfolioId: String(body.RootElement, "portfolioId"),
                            MembershipDecision: String(body.RootElement, "membershipDecision"),
                            RecommendationId: String(body.RootElement, "recommendationId"),
                            GovernanceDecisionRef: String(body.RootElement, "governanceDecisionRef"),
                            ApprovedBaselineRef: String(body.RootElement, "approvedBaselineRef"),
                            RequestedScope: requestedScope),
                        resolved.Actor,
                        cancellationToken);
                }
                else if (ExecutionCommands.Contains(commandName))
                {
                    if (_execution is null)
                        return Deny(503, "P5_COMMAND_DISPATCH_NOT_BOUND",
                            "Execution mutation is recovered but the guarded P1 Wave10 Execution executor is not composed into this gateway.");

                    result = await _execution.ExecuteAsync(
                        new ExecutionCommandWave10(
                            commandName,
                            RequireString(body.RootElement, "executionId"),
                            RequireExpectedVersion(attempt),
                            attempt.IdempotencyKey!,
                            attempt.CorrelationId!,
                            RequestedScope: requestedScope,
                            ProgressPercent: OptionalInt32(body.RootElement, "progressPercent"),
                            CompletionDossierRef: String(body.RootElement, "completionDossierRef"),
                            CompletionDecision: String(body.RootElement, "completionDecision"),
                            ReviewNote: String(body.RootElement, "reviewNote")),
                        resolved.Actor,
                        cancellationToken);
                }
                else if (BenefitCommands.Contains(commandName))
                {
                    if (_benefit is null)
                        return Deny(503, "P5_COMMAND_DISPATCH_NOT_BOUND",
                            "Benefit mutation is recovered but the P1 Wave11 Benefit executor is not composed into this gateway.");

                    result = await _benefit.ExecuteAsync(
                        new BenefitCommandWave11(
                            commandName,
                            RequireString(body.RootElement, "benefitId"),
                            RequireExpectedVersion(attempt),
                            attempt.IdempotencyKey!,
                            attempt.CorrelationId!,
                            RequestedScope: requestedScope,
                            BaselineEvidenceRef: String(body.RootElement, "baselineEvidenceRef"),
                            TargetEvidenceRef: String(body.RootElement, "targetEvidenceRef"),
                            MeasurementPlanRef: String(body.RootElement, "measurementPlanRef"),
                            MeasurementDossierRef: String(body.RootElement, "measurementDossierRef"),
                            VerificationDossierRef: String(body.RootElement, "verificationDossierRef"),
                            AttributionDossierRef: String(body.RootElement, "attributionDossierRef"),
                            RealizationDossierRef: String(body.RootElement, "realizationDossierRef")),
                        resolved.Actor,
                        cancellationToken);
                }
                else if (KnowledgeCommands.Contains(commandName))
                {
                    if (_knowledge is null)
                        return Deny(503, "P5_COMMAND_DISPATCH_NOT_BOUND",
                            "Knowledge mutation is recovered but the P1 Wave13 Knowledge executor is not composed into this gateway.");

                    var createDraft = string.Equals(commandName, "knowledge.create-draft", StringComparison.OrdinalIgnoreCase);
                    result = await _knowledge.ExecuteAsync(
                        new KnowledgeCommandWave13(
                            commandName,
                            KnowledgeId: createDraft ? String(body.RootElement, "knowledgeId") : RequireString(body.RootElement, "knowledgeId"),
                            BenefitId: createDraft ? RequireString(body.RootElement, "benefitId") : String(body.RootElement, "benefitId"),
                            ExpectedVersion: RequireExpectedVersion(attempt),
                            IdempotencyKey: attempt.IdempotencyKey!,
                            CorrelationId: attempt.CorrelationId!,
                            RequestedScope: requestedScope,
                            Decision: String(body.RootElement, "decision"),
                            Note: String(body.RootElement, "note"),
                            PublicationDossierRef: String(body.RootElement, "publicationDossierRef")),
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

    private static int? OptionalInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
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
