using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;
using EIMS.PilotEnvironment.Readiness;

namespace EIMS.PilotAssembly.Activation;

public sealed record IdentityRuntimeBindingEvidence(
    string HostingMode,
    string? DirectoryProvider,
    bool LiveDomainIdentityValidated,
    bool PersonIdMappingValidated,
    bool ServerRoleScopeValidated,
    bool ClientIdentityHeadersTrusted,
    string? EvidenceReference)
{
    public bool IsPhysicalBindingReady =>
        string.Equals(HostingMode, "IIS_WINDOWS_AUTH", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(DirectoryProvider)
        && LiveDomainIdentityValidated
        && PersonIdMappingValidated
        && ServerRoleScopeValidated
        && !ClientIdentityHeadersTrusted
        && !string.IsNullOrWhiteSpace(EvidenceReference);

    public static IdentityRuntimeBindingEvidence Unbound() =>
        new(string.Empty, null, false, false, false, false, null);
}

public sealed record RuntimeCompositionCandidate(
    ICommandGateway Gateway,
    PersistenceContractDescriptor Persistence,
    IdentityRuntimeBindingEvidence Identity,
    EnvironmentEvidenceReport Environment,
    string BindingContractVersion,
    bool P1ContractTestsPassed,
    string? EvidenceReference);

public sealed record RuntimeActivationDecision(
    bool Activated,
    string Code,
    string Detail,
    ICommandGateway Gateway);

public sealed record RuntimeLabCompositionAssessment(
    bool WiringAccepted,
    string Code,
    string Detail);

public interface IRuntimeCompositionCandidateProvider
{
    RuntimeCompositionCandidate? GetCandidate();
}

public sealed class NoRuntimeCompositionCandidateProvider : IRuntimeCompositionCandidateProvider
{
    public RuntimeCompositionCandidate? GetCandidate() => null;
}

public static class RuntimeCompositionActivator
{
    public static RuntimeActivationDecision SelectProduction(RuntimeCompositionCandidate? candidate)
    {
        if (candidate is null)
            return Blocked("P5_RUNTIME_COMPOSITION_NOT_SUPPLIED", "No production runtime composition candidate is registered.");

        if (candidate.Gateway is FailClosedCommandGateway)
            return Blocked("P5_RUNTIME_GATEWAY_FAIL_CLOSED_CANDIDATE", "A fail-closed gateway cannot be promoted as a production runtime candidate.");

        if (!string.Equals(candidate.BindingContractVersion, P1P5BindingContract.Version, StringComparison.Ordinal))
            return Blocked("P5_RUNTIME_BINDING_CONTRACT_MISMATCH", "Runtime candidate binding contract does not match the compiled P1-P5 contract.");

        if (!candidate.P1ContractTestsPassed)
            return Blocked("P5_RUNTIME_P1_CONTRACT_EVIDENCE_REQUIRED", "P1 contract-test evidence is required before production activation.");

        if (!candidate.Persistence.IsPhysicalOracleReady)
            return Blocked("P5_RUNTIME_P2_PHYSICAL_BINDING_REQUIRED", "Durable physical Oracle P2 binding evidence is incomplete.");

        if (!candidate.Identity.IsPhysicalBindingReady)
            return Blocked("P5_RUNTIME_P3_PHYSICAL_BINDING_REQUIRED", "Authoritative live P3 Windows identity/directory binding evidence is incomplete or unsafe.");

        if (!candidate.Environment.SchemaSupported
            || candidate.Environment.SensitiveKeysDetected
            || !string.Equals(candidate.Environment.EvidenceClass, EnvironmentEvidenceEvaluator.PilotEvidenceClass, StringComparison.Ordinal)
            || !candidate.Environment.PilotActivationReady)
            return Blocked("P5_RUNTIME_OP04_PILOT_EVIDENCE_REQUIRED", "Production activation requires a clean OP-04 PILOT evidence report with every gate passed.");

        if (string.IsNullOrWhiteSpace(candidate.EvidenceReference))
            return Blocked("P5_RUNTIME_COMPOSITION_EVIDENCE_REF_REQUIRED", "A non-secret runtime composition evidence reference is required.");

        return new RuntimeActivationDecision(
            true,
            "P5_RUNTIME_COMPOSITION_ACTIVE",
            "Production runtime composition passed P1/P2/P3/OP-04 activation policy.",
            candidate.Gateway);
    }

    public static RuntimeLabCompositionAssessment AssessLab(RuntimeCompositionCandidate? candidate)
    {
        if (candidate is null)
            return new(false, "P5_LAB_COMPOSITION_NOT_SUPPLIED", "No lab composition candidate is supplied.");

        if (candidate.Gateway is FailClosedCommandGateway)
            return new(false, "P5_LAB_GATEWAY_FAIL_CLOSED", "Lab wiring requires a non-fail-closed gateway candidate.");

        if (!string.Equals(candidate.BindingContractVersion, P1P5BindingContract.Version, StringComparison.Ordinal))
            return new(false, "P5_LAB_BINDING_CONTRACT_MISMATCH", "Lab candidate binding contract is stale or incompatible.");

        if (!candidate.Persistence.IsLogicalContractReady)
            return new(false, "P5_LAB_P2_LOGICAL_CONTRACT_REQUIRED", "Lab wiring still requires the recovered logical P2 contract.");

        if (!candidate.Environment.SchemaSupported
            || candidate.Environment.SensitiveKeysDetected
            || !string.Equals(candidate.Environment.EvidenceClass, EnvironmentEvidenceEvaluator.LabEvidenceClass, StringComparison.Ordinal))
            return new(false, "P5_LAB_EVIDENCE_REQUIRED", "Lab wiring requires clean LAB_EVIDENCE; it is never production activation evidence.");

        return new(
            true,
            "P5_LAB_COMPOSITION_WIRING_ACCEPTED_NON_PRODUCTION",
            "Lab wiring is structurally valid but is explicitly not eligible for production or Network Pilot activation.");
    }

    private static RuntimeActivationDecision Blocked(string code, string detail) =>
        new(false, code, detail, new FailClosedCommandGateway());
}
