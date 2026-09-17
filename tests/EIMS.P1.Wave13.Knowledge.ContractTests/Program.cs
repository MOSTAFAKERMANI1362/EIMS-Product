using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

var tests = new List<(string Name, Action Run)>
{
    ("W13-01 catalog recovers three Knowledge mutations", Catalog),
    ("W13-02 create draft requires realized Benefit evidence", SourceRequired),
    ("W13-03 create draft binds exact Benefit version", SourceVersion),
    ("W13-04 Benefit dossiers are mandatory for Knowledge creation", SourceDossiers),
    ("W13-05 Benefit Owner alone is not Knowledge author authority", BenefitOwnerDenied),
    ("W13-06 server author policy is mandatory", AuthorPolicyRequired),
    ("W13-07 authorized author creates DRAFT preserving Digital Thread", CreateHappy),
    ("W13-08 one Benefit cannot create duplicate Knowledge asset", DuplicateBenefit),
    ("W13-09 validation requires Knowledge Steward", ValidateRole),
    ("W13-10 validation APPROVE advances DRAFT to VALIDATED", ValidateApprove),
    ("W13-11 validation RETURN remains correction DRAFT", ValidateReturn),
    ("W13-12 publish requires publication dossier", PublishDossier),
    ("W13-13 publish requires Knowledge Publisher", PublishRole),
    ("W13-14 validator cannot publish same asset", PublishSod),
    ("W13-15 independent publisher advances to PUBLISHED", PublishHappy),
    ("W13-16 publisher RETURN sends asset back to DRAFT", PublishReturn),
    ("W13-17 exact create retry is idempotent before source reload", ReplayBeforeSource),
    ("W13-18 changed payload with same key conflicts", ReplayConflict),
    ("W13-19 stale Knowledge version fails closed", StaleVersion),
    ("W13-20 requested scope cannot widen authority", ScopeCannotWiden),
    ("W13-21 persistence fault rolls back create atomically", CreateRollback),
    ("W13-22 persistence fault rolls back mutation atomically", MutationRollback),
    ("W13-23 full Knowledge lifecycle is DRAFT to VALIDATED to PUBLISHED", FullLifecycle),
    ("W13-24 publication emits authoritative KnowledgePublished event", PublishEvent)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

static void Catalog()
{
    var catalog = new RecoveredApiCommandCatalogWave13();
    Assert(catalog.All.Count(x => x.MutationContractRecovered) == 32, "recovered count");
    foreach (var name in new[] { "knowledge.create-draft", "knowledge.validate", "knowledge.publish" })
        Assert(catalog.TryGet(name, out var p) && p.MutationContractRecovered && p.EventContractRecovered, name);
}

static void SourceRequired()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var service = Service(store, source: Array.Empty<KnowledgeBenefitSourceEvidenceWave13>());
    var result = Exec(service, Create(), Author());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_REALIZED_BENEFIT_EVIDENCE_REQUIRED", "source required");
}

static void SourceVersion()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var result = Exec(Service(store), Create(expectedVersion: 8), Author());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_SOURCE_VERSION_CONFLICT", "version");
}

static void SourceDossiers()
{
    var bad = Source() with { VerificationDossierRef = string.Empty };
    var store = new KnowledgeTransactionalStoreWave13();
    var result = Exec(Service(store, source: new[] { bad }), Create(), Author());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_REALIZED_BENEFIT_EVIDENCE_REQUIRED", "dossiers");
}

static void BenefitOwnerDenied()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var result = Exec(Service(store), Create(), Actor("P-BEN", "A-BEN", "BENEFIT_OWNER"));
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_COMMAND_ROLE_REQUIRED", "benefit owner denied");
}

static void AuthorPolicyRequired()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var service = Service(store, author: Array.Empty<KnowledgeAuthorAuthorityEvidenceWave13>());
    var result = Exec(service, Create(), Author());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_AUTHOR_POLICY_REQUIRED", "author policy");
}

static void CreateHappy()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var result = Exec(Service(store), Create(), Author());
    Assert(result.Allowed && result.StateMutated && result.NewVersion == 1, "create");
    var item = store.KnowledgeAssets.Single();
    Assert(item.State == "DRAFT", "draft");
    Assert(item.BenefitId == "BEN-1" && item.ExecutionId == "EX-1" && item.IdeaId == "IDEA-1" && item.ApprovedIdeaVersion == 7, "thread");
    Assert(item.AuthorPersonId == "P-AUTH" && item.AuthorPolicy == "NEED_OWNER_OR_DOMAIN_EXPERT", "author");
}

static void DuplicateBenefit()
{
    var store = new KnowledgeTransactionalStoreWave13();
    Assert(Exec(Service(store), Create(idem: "I1"), Author()).Allowed, "first");
    var result = Exec(Service(store), Create(idem: "I2"), Author());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_BENEFIT_ALREADY_HAS_ASSET", "duplicate");
}

static void ValidateRole()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft());
    var result = Exec(Service(store), Validate("APPROVE"), Author());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_COMMAND_ROLE_REQUIRED", "role");
}

static void ValidateApprove()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft());
    var result = Exec(Service(store), Validate("APPROVE"), Steward());
    Assert(result.Allowed, "approve");
    var item = store.KnowledgeAssets.Single();
    Assert(item.State == "VALIDATED" && item.Version == 2 && item.ValidatedByPersonId == "P-STEW", "validated");
}

static void ValidateReturn()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft());
    var result = Exec(Service(store), Validate("RETURN"), Steward());
    Assert(result.Allowed && store.KnowledgeAssets.Single().State == "DRAFT", "return");
    Assert(store.KnowledgeOutbox.Single().EventName == "KnowledgeReturnedForCorrection.v1", "return event");
}

static void PublishDossier()
{
    var store = new KnowledgeTransactionalStoreWave13(Validated());
    var result = Exec(Service(store), Publish("PUBLISH", dossier: null), Publisher());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_PUBLICATION_DOSSIER_REQUIRED", "dossier");
}

static void PublishRole()
{
    var store = new KnowledgeTransactionalStoreWave13(Validated());
    var result = Exec(Service(store), Publish("PUBLISH"), Steward());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_COMMAND_ROLE_REQUIRED", "publisher role");
}

static void PublishSod()
{
    var store = new KnowledgeTransactionalStoreWave13(Validated(validatorPerson: "P-PUB", validatorAssignment: "A-PUB"));
    var result = Exec(Service(store), Publish("PUBLISH"), Publisher());
    Assert(!result.Allowed && result.Code == "SOD_KNOWLEDGE_VALIDATOR_NOT_PUBLISHER", "sod");
}

static void PublishHappy()
{
    var store = new KnowledgeTransactionalStoreWave13(Validated());
    var result = Exec(Service(store), Publish("PUBLISH"), Publisher());
    Assert(result.Allowed, "publish");
    var item = store.KnowledgeAssets.Single();
    Assert(item.State == "PUBLISHED" && item.Version == 3 && item.PublishedByPersonId == "P-PUB" && item.PublicationDossierRef == "PUB-DOS", "published");
}

static void PublishReturn()
{
    var store = new KnowledgeTransactionalStoreWave13(Validated());
    var result = Exec(Service(store), Publish("RETURN", dossier: null), Publisher());
    var item = store.KnowledgeAssets.Single();
    Assert(result.Allowed && item.State == "DRAFT" && item.ValidatedByPersonId is null, "return draft");
}

static void ReplayBeforeSource()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var command = Create(idem: "REPLAY");
    Assert(Exec(Service(store), command, Author()).Allowed, "first");
    var replayService = Service(
        store,
        source: Array.Empty<KnowledgeBenefitSourceEvidenceWave13>(),
        author: Array.Empty<KnowledgeAuthorAuthorityEvidenceWave13>());
    var replay = Exec(replayService, command, Author());
    Assert(replay.Allowed && replay.IdempotentReplay && !replay.StateMutated, "replay");
    Assert(store.KnowledgeAssets.Count == 1 && store.KnowledgeAuditLog.Count == 1, "no duplicate");
}

static void ReplayConflict()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft());
    var first = Validate("APPROVE", idem: "SAME");
    Assert(Exec(Service(store), first, Steward()).Allowed, "first");
    var changed = first with { Decision = "RETURN" };
    var replay = Exec(Service(store), changed, Steward());
    Assert(!replay.Allowed && replay.Code == "P1_IDEMPOTENCY_CONFLICT", "conflict");
}

static void StaleVersion()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft(version: 2));
    var result = Exec(Service(store), Validate("APPROVE", expectedVersion: 1), Steward());
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_VERSION_CONFLICT", "stale");
}

static void ScopeCannotWiden()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft());
    var actor = new AuthorityActor("P-STEW", "DOMAIN\\stew", "WINDOWS", "A-STEW", new[] { "KNOWLEDGE_STEWARD" }, new[] { "GLOBAL" });
    var result = Exec(Service(store), Validate("APPROVE") with { RequestedScope = "GLOBAL" }, actor);
    Assert(!result.Allowed && result.Code == "P1_KNOWLEDGE_SCOPE_CONTEXT_MISMATCH", "scope");
}

static void CreateRollback()
{
    var store = new KnowledgeTransactionalStoreWave13 { FaultPoint = KnowledgeWave13PersistenceFaultPoint.BeforeCommitPublish };
    try
    {
        _ = Exec(Service(store), Create(), Author());
        throw new Exception("fault not thrown");
    }
    catch (PersistenceAtomicityException) { }
    Assert(store.KnowledgeAssets.Count == 0 && store.KnowledgeAuditLog.Count == 0 && store.KnowledgeOutbox.Count == 0 && store.KnowledgeIdempotencyRecords.Count == 0, "rollback");
}

static void MutationRollback()
{
    var store = new KnowledgeTransactionalStoreWave13(Draft()) { FaultPoint = KnowledgeWave13PersistenceFaultPoint.BeforeCommitPublish };
    try
    {
        _ = Exec(Service(store), Validate("APPROVE"), Steward());
        throw new Exception("fault not thrown");
    }
    catch (PersistenceAtomicityException) { }
    Assert(store.KnowledgeAssets.Single().State == "DRAFT", "state rollback");
    Assert(store.KnowledgeAuditLog.Count == 0 && store.KnowledgeOutbox.Count == 0 && store.KnowledgeIdempotencyRecords.Count == 0, "evidence rollback");
}

static void FullLifecycle()
{
    var store = new KnowledgeTransactionalStoreWave13();
    var service = Service(store);
    Assert(Exec(service, Create(idem: "I1"), Author()).Allowed, "create");
    var id = store.KnowledgeAssets.Single().KnowledgeId;
    Assert(Exec(service, Validate("APPROVE", id, 1, "I2"), Steward()).Allowed, "validate");
    Assert(Exec(service, Publish("PUBLISH", id, 2, "I3"), Publisher()).Allowed, "publish");
    var item = store.KnowledgeAssets.Single();
    Assert(item.State == "PUBLISHED" && item.Version == 3, "terminal");
    var expected = new[] { "KnowledgeDraftCreated.v1", "KnowledgeValidated.v1", "KnowledgePublished.v1" };
    Assert(store.KnowledgeOutbox.Select(x => x.EventName).SequenceEqual(expected), "events");
}

static void PublishEvent()
{
    var store = new KnowledgeTransactionalStoreWave13(Validated());
    Assert(Exec(Service(store), Publish("PUBLISH"), Publisher()).Allowed, "publish");
    var evt = store.KnowledgeOutbox.Single();
    Assert(evt.EventName == "KnowledgePublished.v1" && evt.Payload!["state"] == "PUBLISHED", "event");
}

static KnowledgeServiceWave13 Service(
    KnowledgeTransactionalStoreWave13 store,
    KnowledgeBenefitSourceEvidenceWave13[]? source = null,
    KnowledgeAuthorAuthorityEvidenceWave13[]? author = null) =>
    new(
        store,
        new StaticKnowledgeBenefitSourceProviderWave13(source ?? new[] { Source() }),
        new StaticKnowledgeAuthorPolicyProviderWave13(author ?? new[] { AuthorEvidence() }),
        new RecoveredApiCommandCatalogWave13());

static AuthorityResult Exec(KnowledgeServiceWave13 service, KnowledgeCommandWave13 command, AuthorityActor actor) =>
    service.ExecuteAsync(command, actor).AsTask().GetAwaiter().GetResult();

static KnowledgeBenefitSourceEvidenceWave13 Source() =>
    new("BEN-1", 9, "EX-1", "IDEA-1", 7, "REALIZED", "VERIFY-DOS", "ATTR-DOS", "REAL-DOS", "UNIT:RND", "BEN-EV", "1");

static KnowledgeAuthorAuthorityEvidenceWave13 AuthorEvidence() =>
    new("BEN-1", "P-AUTH", "A-AUTH", "UNIT:RND", "NEED_OWNER_OR_DOMAIN_EXPERT", true, "AUTHOR-EV", "1");

static KnowledgeEnvelopeWave13 Draft(long version = 1) =>
    new(
        "KN-1", "BEN-1", "EX-1", "IDEA-1", 7, "DRAFT", version,
        "P-AUTH", "A-AUTH", "NEED_OWNER_OR_DOMAIN_EXPERT", "UNIT:RND",
        null, null, null, null, null,
        DateTimeOffset.Parse("2026-09-17T00:00:00Z"),
        DateTimeOffset.Parse("2026-09-17T00:00:00Z"),
        "CORR-BASE");

static KnowledgeEnvelopeWave13 Validated(string validatorPerson = "P-STEW", string validatorAssignment = "A-STEW") =>
    Draft() with
    {
        State = "VALIDATED",
        Version = 2,
        ValidatedByPersonId = validatorPerson,
        ValidatedByAssignmentId = validatorAssignment
    };

static KnowledgeCommandWave13 Create(long expectedVersion = 9, string idem = "IDEM-CREATE") =>
    new("knowledge.create-draft", null, "BEN-1", expectedVersion, idem, "CORR-CREATE", "UNIT:RND");

static KnowledgeCommandWave13 Validate(
    string decision,
    string knowledgeId = "KN-1",
    long expectedVersion = 1,
    string idem = "IDEM-VALIDATE") =>
    new("knowledge.validate", knowledgeId, null, expectedVersion, idem, "CORR-VALIDATE", "UNIT:RND", decision, "note");

static KnowledgeCommandWave13 Publish(
    string decision,
    string knowledgeId = "KN-1",
    long expectedVersion = 2,
    string idem = "IDEM-PUBLISH",
    string? dossier = "PUB-DOS") =>
    new("knowledge.publish", knowledgeId, null, expectedVersion, idem, "CORR-PUBLISH", "UNIT:RND", decision, "note", dossier);

static AuthorityActor Author() => Actor("P-AUTH", "A-AUTH", "NEED_OWNER");
static AuthorityActor Steward() => Actor("P-STEW", "A-STEW", "KNOWLEDGE_STEWARD");
static AuthorityActor Publisher() => Actor("P-PUB", "A-PUB", "KNOWLEDGE_PUBLISHER");
static AuthorityActor Actor(string person, string assignment, string role) =>
    new(person, "DOMAIN\\" + person, "WINDOWS", assignment, new[] { role }, new[] { "UNIT:RND" });

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
