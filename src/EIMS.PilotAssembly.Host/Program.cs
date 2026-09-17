using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;
using EIMS.PilotAssembly.Host;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseIISIntegration();

// Production activation is controlled by OP-04 PILOT evidence plus real physical composition.
// The default composition is unavailable, so the Host remains fail-closed in LAB/dev and until
// durable P2 + authoritative P3 + a real candidate gateway are supplied by an environment adapter.
builder.Services.AddEimsRuntimeActivation(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();

bool RuntimeGatewayBound() =>
    app.Services.GetRequiredService<ICommandGateway>() is not FailClosedCommandGateway;

PilotBindingSnapshot Snapshot()
{
    string? Get(string key) => app.Configuration[key];
    bool Flag(string key) => bool.TryParse(Get(key), out var v) && v;
    var p4Path = Get("Pilot:P4:PackagePath");
    if (!string.IsNullOrWhiteSpace(p4Path) && !Path.IsPathRooted(p4Path))
        p4Path = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, p4Path));

    // A config flag alone can never promote P1 to READY. The active DI gateway must also be a real bound gateway.
    var p1RuntimeBound = Flag("Pilot:P1Authority:RuntimeBound") && RuntimeGatewayBound();

    return new PilotBindingSnapshot(
        p1RuntimeBound,
        Flag("Pilot:P1Authority:ContractTestsPassed"),
        Get("Pilot:P1Authority:PackagePath"),
        Get("Pilot:Oracle:Version"),
        Get("Pilot:Oracle:Provider"),
        Get("Pilot:Oracle:ConnectionMode"),
        Get("Pilot:Oracle:ServiceAccount"),
        Get("Pilot:Oracle:SchemaOwner"),
        Flag("Pilot:Oracle:LiveConnectionValidated"),
        Get("Pilot:WindowsIdentity:HostingMode") ?? "",
        Flag("Pilot:WindowsIdentity:RequireAuthenticatedUser"),
        Flag("Pilot:WindowsIdentity:TrustClientIdentityHeaders"),
        Flag("Pilot:WindowsIdentity:LiveValidated"),
        Flag("Pilot:Infrastructure:WindowsServerVmProvisioned"),
        Flag("Pilot:Transport:TlsCertificateConfigured"),
        Flag("Pilot:Transport:TlsHandshakeValidated"),
        p4Path,
        Get("Pilot:P4:ExpectedSha256") ?? PilotBaseline.P4ExpectedSha256,
        Flag("Pilot:P4:RealExportReconciled"),
        Flag("Pilot:Evidence:ConcurrencyIdempotencyValidated"),
        Flag("Pilot:Evidence:AuditOutboxAtomicityValidated"),
        Flag("Pilot:Evidence:BackupRestoreValidated"),
        Flag("Pilot:Evidence:MonitoringValidated"));
}

app.MapGet("/health", () =>
{
    var snapshot = Snapshot();
    var activation = app.Services.GetRequiredService<HostRuntimeActivationStatus>();
    return Results.Ok(new
    {
        status = "Healthy",
        assembly = PilotBaseline.Version,
        productBaseline = PilotBaseline.ProductBaseline,
        productSha256 = PilotBaseline.ProductSha256,
        serverAuthorityBoundary = true,
        p1P5BindingAdapterAvailable = true,
        p1P5BindingContract = P1P5BindingContract.Version,
        recoveredMutationCommandCount = P1P5BindingContract.RecoveredMutationCommandCount,
        runtimeGatewayBound = RuntimeGatewayBound(),
        domainCommandAuthorityBound = snapshot.P1AuthorityRuntimeBound,
        activationState = activation.Decision.Activate
            ? "RUNTIME_COMPOSITION_ACTIVE"
            : "FAIL_CLOSED_UNTIL_OP04_AND_PHYSICAL_COMPOSITION_READY",
        op04EvidenceState = activation.EvidenceState,
        op04PilotReady = activation.Op04PilotReady,
        activationCode = activation.Decision.Code,
        activationBlockers = activation.Decision.Blockers,
        compositionEvidenceRef = activation.CompositionEvidenceRef
    });
});

app.MapGet("/api/pilot/readiness", () =>
{
    var gates = PilotReadinessEvaluator.Evaluate(Snapshot());
    var activation = app.Services.GetRequiredService<HostRuntimeActivationStatus>();
    return Results.Ok(new
    {
        ready = PilotReadinessEvaluator.IsNetworkPilotReady(gates) && activation.Decision.Activate,
        legacyDiagnosticGates = gates,
        authoritativeActivation = new
        {
            activation.Op04EvidenceLoaded,
            activation.Op04PilotReady,
            activation.EvidenceState,
            activation.Decision.Code,
            activation.Decision.Blockers
        }
    });
});

app.MapGet("/api/session/me", (HttpContext ctx) =>
{
    var networkName = IdentitySourcePolicy.GetAuthenticatedNetworkName(ctx.User);
    return networkName is null
        ? Results.Json(new { code = "P5_WINDOWS_IDENTITY_REQUIRED", message = "Authenticated Windows identity is required." }, statusCode: 401)
        : Results.Ok(new { networkIdentity = networkName, identitySource = "WINDOWS_PRINCIPAL", clientIdentityHeadersTrusted = false });
});

app.MapPost("/api/authority/check", (HttpContext ctx) =>
{
    var networkName = IdentitySourcePolicy.GetAuthenticatedNetworkName(ctx.User);
    if (networkName is null)
        return Results.Json(new { code = "P5_WINDOWS_IDENTITY_REQUIRED" }, statusCode: 401);

    var activation = app.Services.GetRequiredService<HostRuntimeActivationStatus>();
    if (!activation.Decision.Activate || !RuntimeGatewayBound())
    {
        return Results.Json(new
        {
            code = "P5_AUTHORITY_RUNTIME_NOT_BOUND",
            allowed = false,
            bindingAdapterAvailable = true,
            bindingContract = P1P5BindingContract.Version,
            activationCode = activation.Decision.Code,
            blockers = activation.Decision.Blockers,
            message = "P1-P5 adapter is compiled and contract-tested, but OP-04 PILOT evidence and physical P2/P3 production composition are not all activated."
        }, statusCode: 503);
    }

    return Results.Json(new
    {
        code = "P5_AUTHORITY_CHECK_ENDPOINT_NOT_ACTIVATED",
        allowed = false,
        message = "Runtime composition is active, but this diagnostic endpoint has no standalone authority query contract. Use an assignment-bound command path."
    }, statusCode: 503);
});

app.MapPost("/api/commands/{**command}", async (string command, HttpContext ctx, ICommandGateway gateway, CancellationToken ct) =>
{
    var networkName = IdentitySourcePolicy.GetAuthenticatedNetworkName(ctx.User);
    if (networkName is null)
        return Results.Json(new { code = "P5_WINDOWS_IDENTITY_REQUIRED", stateMutated = false }, statusCode: 401);

    string rawBody;
    using (var reader = new StreamReader(ctx.Request.Body)) rawBody = await reader.ReadToEndAsync(ct);
    var correlationId = ctx.Request.Headers["X-EIMS-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId)) correlationId = Guid.NewGuid().ToString("D");
    long? expectedVersion = null;
    var ifMatch = ctx.Request.Headers.IfMatch.FirstOrDefault()?.Trim('"');
    if (long.TryParse(ifMatch, out var parsed)) expectedVersion = parsed;
    var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();

    var result = await gateway.ExecuteAsync(new CommandAttempt(command, networkName, correlationId, expectedVersion, idempotencyKey, rawBody), ct);
    return Results.Json(new { result.Code, result.Message, result.StateMutated, correlationId }, statusCode: result.HttpStatus);
});

app.Run();
