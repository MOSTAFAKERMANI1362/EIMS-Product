namespace EIMS.Authority.Recovery;

/// <summary>
/// Wave 8 route-integrity façade over the historical Wave 7 voting service.
/// Production composition from Wave 8 onward must use this service rather than composing G04VotingService directly.
/// </summary>
public sealed class G04VotingServiceWave8(
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

        var assessment = await store.GetG04AssessmentForPlanAsync(command.PlanId, cancellationToken);
        if (assessment is null || !string.Equals(assessment.AssessmentId, command.AssessmentId, StringComparison.Ordinal))
            return AuthorityResult.Deny(404, "P1_G04_ASSESSMENT_NOT_FOUND", command.CorrelationId);

        if (!G04RouteIntegrity.HasCompleteRoute(assessment))
            return AuthorityResult.Deny(409, "P1_G04_VOTE_ROUTE_CONTEXT_MISSING", command.CorrelationId,
                "G04 voting requires complete server-frozen decision route metadata.");

        if (!G04RouteIntegrity.IsCommitteeRoute(assessment))
            return AuthorityResult.Deny(409, "P1_G04_VOTE_ROUTE_NOT_COMMITTEE", command.CorrelationId,
                "Committee voting is not authorized for this G04 decision route.");

        var snapshot = await store.GetCommitteeSnapshotAsync(assessment.AssessmentId, cancellationToken);
        if (snapshot is not null && !SnapshotMatchesFrozenAssessment(snapshot, assessment))
            return AuthorityResult.Deny(409, "P1_G04_FROZEN_SNAPSHOT_ROUTE_MISMATCH", command.CorrelationId);

        var boundProvider = new FrozenRouteGovernanceProvider(governanceProvider, assessment);
        var historical = new G04VotingService(store, boundProvider, membershipResolver, _catalog);
        return await historical.VoteAsync(command, actor, cancellationToken);
    }

    private static bool SnapshotMatchesFrozenAssessment(
        G04CommitteeSnapshotEnvelope snapshot,
        G04AssessmentEnvelope assessment) =>
        string.Equals(snapshot.AssessmentId, assessment.AssessmentId, StringComparison.Ordinal)
        && string.Equals(snapshot.DecisionRoute, assessment.DecisionRoute, StringComparison.Ordinal)
        && string.Equals(snapshot.GovernanceProfileId, assessment.GovernanceProfileId, StringComparison.Ordinal)
        && string.Equals(snapshot.GovernanceProfileVersion, assessment.GovernanceProfileVersion, StringComparison.Ordinal)
        && string.Equals(snapshot.VoteRule, assessment.DecisionMethod, StringComparison.OrdinalIgnoreCase);

    private sealed class FrozenRouteGovernanceProvider(
        IG04GovernanceProfileProvider inner,
        G04AssessmentEnvelope frozenAssessment) : IG04GovernanceProfileProvider
    {
        public async ValueTask<G04GovernanceProfile?> ResolveAsync(
            G04AssessmentEnvelope assessment,
            AggregateSnapshot idea,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(assessment.AssessmentId, frozenAssessment.AssessmentId, StringComparison.Ordinal)
                || !G04RouteIntegrity.IsCommitteeRoute(frozenAssessment))
                return null;

            var profile = await inner.ResolveAsync(assessment, idea, cancellationToken);
            if (profile is null)
                return null;

            return string.Equals(profile.GovernanceProfileId?.Trim(), frozenAssessment.GovernanceProfileId, StringComparison.Ordinal)
                && string.Equals(profile.GovernanceProfileVersion?.Trim(), frozenAssessment.GovernanceProfileVersion, StringComparison.Ordinal)
                && string.Equals(profile.VoteRule?.Trim(), frozenAssessment.DecisionMethod, StringComparison.OrdinalIgnoreCase)
                ? profile
                : null;
        }
    }
}
