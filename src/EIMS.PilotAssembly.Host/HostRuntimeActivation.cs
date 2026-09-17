using EIMS.PilotAssembly.Core;
using EIMS.PilotEnvironment.Readiness;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EIMS.PilotAssembly.Host;

public sealed record HostRuntimeActivationStatus(
    bool Op04EvidenceLoaded,
    bool Op04PilotReady,
    string EvidenceState,
    string? CompositionEvidenceRef,
    RuntimeActivationDecision Decision,
    ICommandGateway Gateway);

public static class HostRuntimeActivation
{
    public static IServiceCollection AddEimsRuntimeActivation(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        // Environment-specific production composition may register this before the activation extension.
        // The default is inserted only when no real composition has been supplied.
        services.TryAddSingleton<IProductionRuntimeComposition, UnavailableProductionRuntimeComposition>();
        services.AddSingleton(sp => Evaluate(
            configuration,
            contentRootPath,
            sp.GetRequiredService<IProductionRuntimeComposition>()));
        services.AddSingleton<ICommandGateway>(sp =>
            sp.GetRequiredService<HostRuntimeActivationStatus>().Gateway);
        return services;
    }

    public static HostRuntimeActivationStatus Evaluate(
        IConfiguration configuration,
        string contentRootPath,
        IProductionRuntimeComposition composition)
    {
        var failClosed = new FailClosedCommandGateway();
        var configuredPath = configuration["Pilot:Activation:Op04EvidencePath"];
        var evidenceLoaded = false;
        var pilotReady = false;
        var evidenceState = "OP04_EVIDENCE_NOT_CONFIGURED";

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
            if (!File.Exists(path))
            {
                evidenceState = "OP04_EVIDENCE_FILE_NOT_FOUND";
            }
            else
            {
                try
                {
                    var report = EnvironmentEvidenceEvaluator.Evaluate(File.ReadAllText(path));
                    evidenceLoaded = true;
                    pilotReady = report.PilotActivationReady;
                    evidenceState = report.PilotActivationReady
                        ? "OP04_PILOT_EVIDENCE_READY"
                        : report.EvidenceClass == EnvironmentEvidenceEvaluator.LabEvidenceClass
                            ? "OP04_LAB_EVIDENCE_NOT_ACTIVATING"
                            : "OP04_PILOT_EVIDENCE_BLOCKED";
                }
                catch
                {
                    evidenceState = "OP04_EVIDENCE_INVALID";
                }
            }
        }

        var candidate = composition.CandidateGateway;
        var decision = RuntimeActivationGate.Evaluate(new RuntimeActivationPrerequisites(
            pilotReady,
            composition.DurableP2Bound,
            composition.AuthoritativeP3Bound,
            candidate is not null,
            candidate is FailClosedCommandGateway));
        var selected = RuntimeActivationGate.SelectGateway(decision, candidate, failClosed);

        return new HostRuntimeActivationStatus(
            evidenceLoaded,
            pilotReady,
            evidenceState,
            composition.CompositionEvidenceRef,
            decision,
            selected);
    }
}
