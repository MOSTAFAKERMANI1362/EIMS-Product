using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1P5-W14C-01 gateway reaches Knowledge service and creates DRAFT atomically", CreateDraftComposition),
    ("P1P5-W14C-02 server author policy defeats a role-correct unauthorized author", AuthorPolicyStillAuthoritative),
    ("P1P5-W14C-03 gateway steward validation reaches VALIDATED", ValidateComposition),
    ("P1P5-W14C-04 gateway publisher requires dossier and reaches PUBLISHED", PublishComposition)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

static async Task CreateDraftComposition()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var service = KnowledgeService(store, AuthorEvidence());
    var gateway = Gateway("P-AUTH", "ASG-AUTH", "DOMAIN_EXPERT", new KnowledgeExecutor(service));

    var result = await gateway.ExecuteAsync(new CommandAttempt(
        "knowledge.create-draft",
        "DOMAIN\\author",
        "CORR-CREATE",
        9,
        "IDEM-CREATE",
        Body("ASG-AUTH", benefitId: "BEN-1")));

    Equal(200, result.HttpStatus);
    True(result.StateMutated);
    var item = store.KnowledgeAssets.Single();
    Equal("DRAFT", item.State);
    Equal("BEN-1", item.BenefitId);
    Equal("EX-1", item.ExecutionId);
    Equal("IDEA-1", item.IdeaId);
    Equal(1, store.KnowledgeAuditLog.Count);
    Equal(1, store.KnowledgeOutbox.Count);
    Equal(1, store.KnowledgeIdempotencyRecords.Count);
}

static async Task AuthorPolicyStillAuthoritative()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var wrongEvidence = new KnowledgeAuthorAuthorityEvidenceWave13(
        "BEN-1", "P-OTHER", "ASG-OTHER", "UNIT:RND", "NEED_OWNER_OR_DOMAIN_EXPERT", true, "AUTH-EV", "1");
    var service = KnowledgeService(store, wrongEvidence);
    var gateway = Gateway("P-AUTH", "ASG-AUTH", "DOMAIN_EXPERT", new KnowledgeExecutor(service));

    var result = await gateway.ExecuteAsync(new CommandAttempt(
        "knowledge.create-draft",
        "DOMAIN\\author",
        "CORR-AUTH",
        9,
        "IDEM-AUTH",
        Body("ASG-AUTH", benefitId: "BEN-1")));

    Equal(403, result.HttpStatus);
    Equal("P1_KNOWLEDGE_AUTHOR_POLICY_REQUIRED", result.Code);
    Equal(0, store.KnowledgeAssets.Count);
}

static async Task ValidateComposition()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft());
    var service = KnowledgeService(store);
    var gateway = Gateway("P-STEW", "ASG-STEW", "KNOWLEDGE_STEWARD", new KnowledgeExecutor(service));

    var result = await gateway.ExecuteAsync(new CommandAttempt(
        "knowledge.validate",
        "DOMAIN\\steward",
        "CORR-VALIDATE",
        1,
        "IDEM-VALIDATE",
        Body("ASG-STEW", knowledgeId: "KN-1", decision: "APPROVE")));

    Equal(200, result.HttpStatus);
    Equal("VALIDATED", store.KnowledgeAssets.Single().State);
    Equal("P-STEW", store.KnowledgeAssets.Single().ValidatedByPersonId);
}

static async Task PublishComposition()
{
    var store = new KnowledgeTransactionalStoreWave13(Validated());
    var service = KnowledgeService(store);
    var gateway = Gateway("P-PUB", "ASG-PUB", "KNOWLEDGE_PUBLISHER", new KnowledgeExecutor(service));

    var missing = await gateway.ExecuteAsync(new CommandAttempt(
        "knowledge.publish",
        "DOMAIN\\publisher",
        "CORR-MISS",
        2,
        "IDEM-MISS",
        Body("ASG-PUB", knowledgeId: "KN-1", decision: "PUBLISH")));
    Equal(400, missing.HttpStatus);
    Equal("P1_KNOWLEDGE_PUBLICATION_DOSSIER_REQUIRED", missing.Code);
    Equal("VALIDATED", store.KnowledgeAssets.Single().State);

    var published = await gateway.ExecuteAsync(new CommandAttempt(
        "knowledge.publish",
        "DOMAIN\\publisher",
        "CORR-PUBLISH",
        2,
        "IDEM-PUBLISH",
        Body("ASG-PUB", knowledgeId: "KN-1", decision: "PUBLISH", publicationDossierRef: "PUB-DOS")));
    Equal(200, published.HttpStatus);
    var item = store.KnowledgeAssets.Single();
    Equal("PUBLISHED", item.State);
    Equal("PUB-DOS", item.PublicationDossierRef);
    Equal("KnowledgePublished.v1", store.KnowledgeOutbox.Single().EventName);
}

static KnowledgeServiceWave13 KnowledgeService(
    KnowledgeTransactionalStoreWave13 store,
    params KnowledgeAuthorAuthorityEvidenceWave13[] authorEvidence) =>
    new(
        store,
        new StaticKnowledgeBenefitSourceProviderWave13(Source()),
        new StaticKnowledgeAuthorPolicyProviderWave13(authorEvidence),
        new RecoveredApiCommandCatalogWave13());

static P1RecoveryCommandGateway Gateway(
    string personId,
    string assignmentId,
    string role,
    IP1KnowledgeExecutor knowledge)
{
    var identity = role switch
    {
        "DOMAIN_EXPERT" => "DOMAIN\\author",
        "KNOWLEDGE_STEWARD" => "DOMAIN\\steward",
        _ => "DOMAIN\\publisher"
    };
    var directory = new InMemoryIdentityDirectoryStore(
        new[] { new PersonDirectoryEntry(personId, identity, DirectoryPersonStatus.Active) },
        new[]
        {
            new RoleAssignmentEntry(
                assignmentId,
                personId,
                role,
                new[] { "UNIT:RND" },
                DateTimeOffset.UtcNow.AddDays(-1),
                null,
                false)
        });

    return new P1RecoveryCommandGateway(
        new WindowsIdentityRbacResolver(directory),
        new NoopKernelExecutor(),
        new NoopEvaluationExecutor(),
        new NoopVoteExecutor(),
        new NoopFinalDecisionExecutor(),
        new RecoveredApiCommandCatalogWave13(),
        knowledge: knowledge);
}

static KnowledgeBenefitSourceEvidenceWave13 Source() =>
    new(
        "BEN-1",
        9,
        "EX-1",
        "IDEA-1",
        7,
        "REALIZED",
        "VERIFY-DOS",
        "ATTR-DOS",
        "REAL-DOS",
        "UNIT:RND",
        "BEN-EV",
        "1");

static KnowledgeAuthorAuthorityEvidenceWave13 AuthorEvidence() =>
    new(
        "BEN-1",
        "P-AUTH",
        "ASG-AUTH",
        "UNIT:RND",
        "NEED_OWNER_OR_DOMAIN_EXPERT",
        true,
        "AUTH-EV",
        "1");

static KnowledgeEnvelopeWave13 Draft() =>
    new(
        "KN-1",
        "BEN-1",
        "EX-1",
        "IDEA-1",
        7,
        "DRAFT",
        1,
        "P-AUTH",
        "ASG-AUTH",
        "NEED_OWNER_OR_DOMAIN_EXPERT",
        "UNIT:RND",
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.Parse("2026-09-17T00:00:00Z"),
        DateTimeOffset.Parse("2026-09-17T00:00:00Z"),
        "CORR-BASE");

static KnowledgeEnvelopeWave13 Validated() =>
    Draft() with
    {
        State = "VALIDATED",
        Version = 2,
        ValidatedByPersonId = "P-STEW",
        ValidatedByAssignmentId = "ASG-STEW"
    };

static string Body(
    string assignmentId,
    string? knowledgeId = null,
    string? benefitId = null,
    string? decision = null,
    string? publicationDossierRef = null)
{
    var parts = new List<string>
    {
        $"\"assignmentId\":\"{assignmentId}\"",
        "\"requestedScope\":\"UNIT:RND\""
    };
    if (knowledgeId is not null) parts.Add($"\"knowledgeId\":\"{knowledgeId}\"");
    if (benefitId is not null) parts.Add($"\"benefitId\":\"{benefitId}\"");
    if (decision is not null) parts.Add($"\"decision\":\"{decision}\"");
    if (publicationDossierRef is not null) parts.Add($"\"publicationDossierRef\":\"{publicationDossierRef}\"");
    return "{" + string.Join(",", parts) + "}";
}

static AuthorityResult Success(string correlationId, long? version) =>
    new(200, "OK", true, true, false, version, correlationId, Array.Empty<string>());

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}

sealed class NoopKernelExecutor : IP1AuthorityKernelExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(AuthorityCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedVersion + 1));
}

sealed class NoopEvaluationExecutor : IP1EvaluationCompletionExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(EvaluationCompletionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedIdeaVersion + 1));
}

sealed class NoopVoteExecutor : IP1G04VoteExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(G04VoteCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedIdeaVersion));
}

sealed class NoopFinalDecisionExecutor : IP1G04FinalDecisionExecutor
{
    public ValueTask<AuthorityResult> ExecuteAsync(G04FinalDecisionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedIdeaRevision));
}
