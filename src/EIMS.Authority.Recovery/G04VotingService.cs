using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

public sealed class G04VotingService(
    IG04VotingStore store,
    IG04GovernanceProfileProvider governanceProvider,
    IG04CommitteeMembershipResolver membershipResolver,
    ICommandPolicyCatalog? catalog = null)
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave7();

    public async ValueTask<AuthorityResult> VoteAsync(
        G04VoteCommand command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.NetworkIdentity)
            || string.IsNullOrWhiteSpace(actor.IdentitySource)
            || string.IsNullOrWhiteSpace(actor.AssignmentId))
            return AuthorityResult.Deny(401, "P1_IDENTITY_ASSIGNMENT_REQUIRED", command.CorrelationId);

        if (string.IsNullOrWhiteSpace(command.PlanId)
            || string.IsNullOrWhiteSpace(command.AssessmentId)
            || string.IsNullOrWhiteSpace(command.CorrelationId))
            return AuthorityResult.Deny(400, "P1_G04_VOTE_CONTEXT_REQUIRED", command.CorrelationId);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return AuthorityResult.Deny(400, "P1_IDEMPOTENCY_KEY_REQUIRED", command.CorrelationId);

        if (!_catalog.TryGet("g04.vote", out var policy)
            || !policy.StateContractRecovered
            || !policy.RuleContractRecovered
            || !policy.EventContractRecovered
            || !policy.MutationContractRecovered)
            return AuthorityResult.Deny(503, "P1_G04_VOTE_CONTRACT_NOT_BOUND", command.CorrelationId);

        var vote = command.Vote?.Trim().ToUpperInvariant() ?? string.Empty;
        var note = command.Note?.Trim() ?? string.Empty;
        if (vote is not ("APPROVE" or "REJECT"))
            return AuthorityResult.Deny(422, "P1_G04_VOTE_VALUE_INVALID", command.CorrelationId);
        if (note.Length < 10)
            return AuthorityResult.Deny(422, "P1_G04_VOTE_NOTE_REQUIRED", command.CorrelationId);
        if (command.ExpectedCommitteeVersion < 0)
            return AuthorityResult.Deny(400, "P1_G04_COMMITTEE_VERSION_INVALID", command.CorrelationId);

        var assessment = await store.GetG04AssessmentForPlanAsync(command.PlanId, cancellationToken);
        if (assessment is null || !string.Equals(assessment.AssessmentId, command.AssessmentId, StringComparison.Ordinal))
            return AuthorityResult.Deny(404, "P1_G04_ASSESSMENT_NOT_FOUND", command.CorrelationId);
        if (!string.Equals(assessment.State, "PENDING", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_G04_ASSESSMENT_NOT_PENDING", command.CorrelationId);

        var idea = await store.GetAggregateAsync(assessment.IdeaId, cancellationToken);
        if (idea is null || !string.Equals(idea.AggregateType, "Idea", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(404, "P1_IDEA_NOT_FOUND", command.CorrelationId);
        if (idea.Version != assessment.IdeaVersion || idea.Version != command.ExpectedIdeaVersion)
            return AuthorityResult.Deny(409, "P1_VERSION_CONFLICT", command.CorrelationId);
        if (string.IsNullOrWhiteSpace(idea.Scope))
            return AuthorityResult.Deny(409, "P1_G04_SCOPE_CONTEXT_MISSING", command.CorrelationId);

        var actorRoles = actor.Roles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (actorRoles.Length != 1 || !string.Equals(actorRoles[0], "G04_COMMITTEE_MEMBER", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_G04_COMMITTEE_ROLE_REQUIRED", command.CorrelationId);
        if (!actor.Scopes.Contains(idea.Scope, StringComparer.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_G04_COMMITTEE_SCOPE_DENIED", command.CorrelationId);
        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && (!actor.Scopes.Contains(command.RequestedScope.Trim(), StringComparer.OrdinalIgnoreCase)
                || !string.Equals(command.RequestedScope.Trim(), idea.Scope, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "P1_G04_COMMITTEE_SCOPE_DENIED", command.CorrelationId);

        var fingerprint = Fingerprint(command, actor, vote, note);
        var prior = await store.GetVoteIdempotencyAsync(assessment.AssessmentId, command.IdempotencyKey, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", command.CorrelationId);
            return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = command.CorrelationId };
        }

        var snapshot = await store.GetCommitteeSnapshotAsync(assessment.AssessmentId, cancellationToken);
        var state = await store.GetCommitteeStateAsync(assessment.AssessmentId, cancellationToken);
        var effectiveBefore = await store.GetEffectiveVotesAsync(assessment.AssessmentId, cancellationToken);

        if ((snapshot is null) != (state is null))
            return AuthorityResult.Deny(500, "P1_G04_COMMITTEE_CONTEXT_INCONSISTENT", command.CorrelationId);
        if (state is not null && string.Equals(state.VotingStageState, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_G04_VOTING_STAGE_COMPLETED", command.CorrelationId);

        var currentVersion = state?.CommitteeVersion ?? 0;
        if (currentVersion != command.ExpectedCommitteeVersion)
            return AuthorityResult.Deny(409, "P1_G04_COMMITTEE_VERSION_CONFLICT", command.CorrelationId);

        if (snapshot is null)
        {
            var profile = await governanceProvider.ResolveAsync(assessment, idea, cancellationToken);
            if (profile is null || !ValidProfile(profile))
                return AuthorityResult.Deny(503, "P1_G04_GOVERNANCE_PROFILE_NOT_BOUND", command.CorrelationId);

            var members = await membershipResolver.ResolveAsync(assessment, idea, profile, cancellationToken);
            var normalizedMembers = NormalizeMembers(members);
            if (!ValidMembers(profile, normalizedMembers))
                return AuthorityResult.Deny(503, "P1_G04_COMMITTEE_MEMBERSHIP_NOT_BOUND", command.CorrelationId);

            var now = DateTimeOffset.UtcNow;
            snapshot = new G04CommitteeSnapshotEnvelope(
                $"G04CS-{Guid.NewGuid():N}", assessment.AssessmentId, idea.AggregateId, idea.Version, idea.Scope,
                "G04_COMMITTEE", profile.GovernanceProfileId.Trim(), profile.GovernanceProfileVersion.Trim(),
                profile.QuorumRequired, profile.VoteRule.Trim().ToUpperInvariant(), profile.ChairPersonId.Trim(),
                profile.ApprovalAuthority.Trim(), profile.ApprovalRef.Trim(), normalizedMembers, now, command.CorrelationId);
        }
        else if (!SnapshotContextMatches(snapshot, assessment, idea))
            return AuthorityResult.Deny(409, "P1_G04_FROZEN_SNAPSHOT_CONTEXT_MISMATCH", command.CorrelationId);

        var member = snapshot.Members.SingleOrDefault(x => string.Equals(x.PersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase));
        if (member is null)
            return AuthorityResult.Deny(403, "P1_G04_ACTOR_NOT_FROZEN_MEMBER", command.CorrelationId);
        if (!member.SourceRoles.Contains("G04_COMMITTEE_MEMBER", StringComparer.OrdinalIgnoreCase)
            || !member.AuthorityAssignmentIds.Contains(actor.AssignmentId, StringComparer.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_G04_FROZEN_MEMBER_AUTHORITY_MISMATCH", command.CorrelationId);

        var priorEffective = effectiveBefore.SingleOrDefault(x => string.Equals(x.PersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase));
        var revisionNumber = (priorEffective?.RevisionNumber ?? 0) + 1;
        var nextCommitteeVersion = currentVersion + 1;
        var timestamp = DateTimeOffset.UtcNow;
        var revision = new G04VoteRevisionEnvelope(
            $"G04VR-{Guid.NewGuid():N}", assessment.AssessmentId, snapshot.SnapshotId, nextCommitteeVersion,
            actor.PersonId, actor.AssignmentId, vote, note, revisionNumber, priorEffective?.VoteRevisionId,
            timestamp, command.CorrelationId);
        var effectiveAfter = new G04EffectiveVoteEnvelope(
            assessment.AssessmentId, actor.PersonId, revision.VoteRevisionId, vote, revisionNumber, timestamp);

        var projected = effectiveBefore
            .Where(x => !string.Equals(x.PersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase))
            .Append(effectiveAfter)
            .ToArray();
        var approveCount = projected.Count(x => string.Equals(x.Vote, "APPROVE", StringComparison.Ordinal));
        var rejectCount = projected.Count(x => string.Equals(x.Vote, "REJECT", StringComparison.Ordinal));
        var quorumReached = projected.Length >= snapshot.QuorumRequired;
        var approvalSatisfied = quorumReached && EvaluateApproval(snapshot, projected, approveCount, rejectCount);
        var completed = quorumReached && (approvalSatisfied || projected.Length == snapshot.Members.Count);

        var stateAfter = new G04CommitteeStateEnvelope(
            assessment.AssessmentId, snapshot.SnapshotId, nextCommitteeVersion,
            completed ? "COMPLETED" : "OPEN", quorumReached, approvalSatisfied,
            projected.Length, approveCount, rejectCount, timestamp, command.CorrelationId,
            completed ? timestamp : null);

        var audit = new AuditEnvelope(
            $"AUD-{Guid.NewGuid():N}", actor.PersonId, actor.NetworkIdentity, actor.IdentitySource, actor.Roles,
            actor.AssignmentId, assessment.AssessmentId, nextCommitteeVersion, policy.RuleSet, timestamp,
            command.CorrelationId, "g04.vote");

        var eventPayload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["g04AssessmentId"] = assessment.AssessmentId,
            ["ideaId"] = idea.AggregateId,
            ["ideaVersion"] = idea.Version.ToString(CultureInfo.InvariantCulture),
            ["committeeSnapshotId"] = snapshot.SnapshotId,
            ["committeeVersion"] = nextCommitteeVersion.ToString(CultureInfo.InvariantCulture),
            ["voterPersonId"] = actor.PersonId,
            ["vote"] = vote,
            ["votingStageState"] = stateAfter.VotingStageState,
            ["effectiveVoteCount"] = stateAfter.EffectiveVoteCount.ToString(CultureInfo.InvariantCulture),
            ["approveCount"] = stateAfter.ApproveCount.ToString(CultureInfo.InvariantCulture),
            ["rejectCount"] = stateAfter.RejectCount.ToString(CultureInfo.InvariantCulture),
            ["quorumReached"] = stateAfter.QuorumReached ? "true" : "false",
            ["approvalRuleSatisfied"] = stateAfter.ApprovalRuleSatisfied ? "true" : "false"
        };

        var events = new List<OutboxEnvelope>
        {
            new($"MSG-{Guid.NewGuid():N}", "G04CommitteeVoteRecorded.v1", assessment.AssessmentId,
                nextCommitteeVersion, command.CorrelationId, timestamp, eventPayload)
        };
        if (completed)
        {
            events.Add(new OutboxEnvelope(
                $"MSG-{Guid.NewGuid():N}", "G04CommitteeVotingCompleted.v1", assessment.AssessmentId,
                nextCommitteeVersion, command.CorrelationId, timestamp,
                new Dictionary<string, string>(eventPayload, StringComparer.Ordinal)));
        }

        return await store.CommitG04VoteAsync(
            new G04VoteRequest(command, actor, idea, assessment,
                state is null ? null : snapshot, state, effectiveBefore, fingerprint),
            new G04VoteCommit(snapshot, stateAfter, revision, effectiveAfter, audit, events.AsReadOnly()),
            cancellationToken);
    }

    public static string Fingerprint(G04VoteCommand command, AuthorityActor actor, string normalizedVote, string normalizedNote)
    {
        var material = string.Join("\n", new[]
        {
            "g04.vote", command.PlanId, command.AssessmentId,
            command.ExpectedIdeaVersion.ToString(CultureInfo.InvariantCulture),
            command.ExpectedCommitteeVersion.ToString(CultureInfo.InvariantCulture),
            actor.PersonId, actor.AssignmentId, normalizedVote, normalizedNote
        });
        return Sha256(material);
    }

    private static bool ValidProfile(G04GovernanceProfile profile)
    {
        var rule = profile.VoteRule?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(profile.GovernanceProfileId)
            || string.IsNullOrWhiteSpace(profile.GovernanceProfileVersion)
            || profile.QuorumRequired < 1
            || rule is not ("MAJORITY" or "CONSENSUS" or "CHAIR_TIEBREAK")
            || string.IsNullOrWhiteSpace(profile.ApprovalAuthority)
            || string.IsNullOrWhiteSpace(profile.ApprovalRef))
            return false;
        return rule != "CHAIR_TIEBREAK" || !string.IsNullOrWhiteSpace(profile.ChairPersonId);
    }

    private static IReadOnlyCollection<G04CommitteeMemberEnvelope> NormalizeMembers(IReadOnlyCollection<G04CommitteeMemberEnvelope> members) =>
        Array.AsReadOnly(members
            .Where(x => x is not null)
            .Select(x => new G04CommitteeMemberEnvelope(
                x.PersonId.Trim(), x.DisplayName.Trim(),
                Array.AsReadOnly(x.SourceRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray()),
                Array.AsReadOnly(x.AuthorityAssignmentIds.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray())))
            .OrderBy(x => x.PersonId, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static bool ValidMembers(G04GovernanceProfile profile, IReadOnlyCollection<G04CommitteeMemberEnvelope> members)
    {
        if (members.Count < profile.QuorumRequired
            || members.Count == 0
            || members.Any(x => string.IsNullOrWhiteSpace(x.PersonId)
                || x.SourceRoles.Count == 0
                || !x.SourceRoles.Contains("G04_COMMITTEE_MEMBER", StringComparer.OrdinalIgnoreCase)
                || x.AuthorityAssignmentIds.Count == 0)
            || members.Select(x => x.PersonId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Count)
            return false;

        return !string.Equals(profile.VoteRule, "CHAIR_TIEBREAK", StringComparison.OrdinalIgnoreCase)
            || members.Any(x => string.Equals(x.PersonId, profile.ChairPersonId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SnapshotContextMatches(G04CommitteeSnapshotEnvelope snapshot, G04AssessmentEnvelope assessment, AggregateSnapshot idea) =>
        string.Equals(snapshot.AssessmentId, assessment.AssessmentId, StringComparison.Ordinal)
        && string.Equals(snapshot.IdeaId, idea.AggregateId, StringComparison.Ordinal)
        && snapshot.IdeaVersion == idea.Version
        && string.Equals(snapshot.Scope, idea.Scope, StringComparison.OrdinalIgnoreCase)
        && string.Equals(snapshot.DecisionRoute, "G04_COMMITTEE", StringComparison.Ordinal)
        && snapshot.QuorumRequired >= 1
        && snapshot.Members.Count >= snapshot.QuorumRequired;

    private static bool EvaluateApproval(
        G04CommitteeSnapshotEnvelope snapshot,
        IReadOnlyCollection<G04EffectiveVoteEnvelope> effectiveVotes,
        int approveCount,
        int rejectCount)
    {
        return snapshot.VoteRule switch
        {
            "MAJORITY" => approveCount > rejectCount,
            "CONSENSUS" => effectiveVotes.Count > 0 && approveCount == effectiveVotes.Count,
            "CHAIR_TIEBREAK" => approveCount == rejectCount
                ? effectiveVotes.Any(x => string.Equals(x.PersonId, snapshot.ChairPersonId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Vote, "APPROVE", StringComparison.Ordinal))
                : approveCount > rejectCount,
            _ => false
        };
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
