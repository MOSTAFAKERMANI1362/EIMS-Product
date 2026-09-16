using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("W9-EH-01 committed G04 approval event creates candidate", ApprovalEventCreatesCandidate),
    ("W9-EH-02 server readiness facts override payload claims", ServerReadinessFactsAreAuthoritative),
    ("W9-EH-03 server policy exclusion blocks candidate", ServerPolicyExclusionBlocks),
    ("W9-EH-04 unrelated event is not an eligibility trigger", UnrelatedEventRejected),
    ("W9-EH-05 missing server facts fails closed", MissingFactsFailsClosed)
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        passed++;
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

async Task ApprovalEventCreatesCandidate()
{
    var store = new PortfolioTransactionalStoreWave9();
    var provider = new FakeFactsProvider(new PortfolioEligibilityFactsWave9(0, false));
    var handler = new PortfolioEligibilityEventHandlerWave9(new PortfolioServiceWave9(store), provider);
    var result = await handler.HandleAsync(ApprovedEvent());
    True(result.Allowed); True(result.StateMutated);
    var thread = await store.GetByIdeaVersionAsync("IDEA-1", 7);
    NotNull(thread); Eq("UNASSIGNED_CANDIDATE", thread!.Candidate.State); Eq(1, provider.CallCount);
}

async Task ServerReadinessFactsAreAuthoritative()
{
    var store = new PortfolioTransactionalStoreWave9();
    var provider = new FakeFactsProvider(new PortfolioEligibilityFactsWave9(2, false));
    var handler = new PortfolioEligibilityEventHandlerWave9(new PortfolioServiceWave9(store), provider);
    var message = ApprovedEvent(new Dictionary<string,string>
    {
        ["hardReadinessGaps"] = "0",
        ["policyExcluded"] = "false"
    });
    var result = await handler.HandleAsync(message);
    False(result.Allowed); Eq("P1_PORTFOLIO_HARD_READINESS_GAPS", result.Code); Eq(0, store.Threads.Count);
}

async Task ServerPolicyExclusionBlocks()
{
    var store = new PortfolioTransactionalStoreWave9();
    var provider = new FakeFactsProvider(new PortfolioEligibilityFactsWave9(0, true));
    var handler = new PortfolioEligibilityEventHandlerWave9(new PortfolioServiceWave9(store), provider);
    var result = await handler.HandleAsync(ApprovedEvent());
    False(result.Allowed); Eq("P1_PORTFOLIO_POLICY_EXCLUDED", result.Code); Eq(0, store.Threads.Count);
}

async Task UnrelatedEventRejected()
{
    var store = new PortfolioTransactionalStoreWave9();
    var provider = new FakeFactsProvider(new PortfolioEligibilityFactsWave9(0, false));
    var handler = new PortfolioEligibilityEventHandlerWave9(new PortfolioServiceWave9(store), provider);
    var result = await handler.HandleAsync(ApprovedEvent() with { EventName = "ManualPortfolioEligibilityRequested.v1" });
    False(result.Allowed); Eq("P1_PORTFOLIO_EVENT_NOT_ELIGIBILITY_TRIGGER", result.Code); Eq(0, provider.CallCount);
}

async Task MissingFactsFailsClosed()
{
    var store = new PortfolioTransactionalStoreWave9();
    var provider = new FakeFactsProvider(null);
    var handler = new PortfolioEligibilityEventHandlerWave9(new PortfolioServiceWave9(store), provider);
    var result = await handler.HandleAsync(ApprovedEvent());
    False(result.Allowed); Eq("P1_PORTFOLIO_ELIGIBILITY_FACTS_REQUIRED", result.Code); Eq(0, store.Threads.Count);
}

OutboxEnvelope ApprovedEvent(IReadOnlyDictionary<string,string>? extras = null)
{
    var payload = new Dictionary<string,string>(StringComparer.Ordinal)
    {
        ["decisionEvidenceId"] = "G04DEC-1",
        ["ideaId"] = "IDEA-1",
        ["ideaRevision"] = "7",
        ["ideaState"] = "APPROVED",
        ["outcome"] = "APPROVE"
    };
    if (extras is not null)
        foreach (var item in extras) payload[item.Key] = item.Value;
    return new OutboxEnvelope(
        "MSG-G04-1", "IdeaApprovedForPortfolio.v1", "IDEA-1", 7,
        "CORR-G04-1", DateTimeOffset.UtcNow, payload);
}

void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
void NotNull(object? value) { if (value is null) throw new InvalidOperationException("Expected non-null."); }
void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}

sealed class FakeFactsProvider(PortfolioEligibilityFactsWave9? facts) : IPortfolioEligibilityFactsProviderWave9
{
    public int CallCount { get; private set; }

    public ValueTask<PortfolioEligibilityFactsWave9?> GetFactsAsync(
        string ideaId,
        long approvedIdeaVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return ValueTask.FromResult(facts);
    }
}
