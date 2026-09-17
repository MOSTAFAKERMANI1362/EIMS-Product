namespace EIMS.PilotAssembly.Core;

public sealed record RuntimeActivationPrerequisites(
    bool PilotEvidenceReady,
    bool DurableP2Bound,
    bool AuthoritativeP3Bound,
    bool CandidateGatewayAvailable,
    bool CandidateGatewayIsFailClosed);

public sealed record RuntimeActivationDecision(
    bool Activate,
    string Code,
    IReadOnlyCollection<string> Blockers);

public static class RuntimeActivationGate
{
    public static RuntimeActivationDecision Evaluate(RuntimeActivationPrerequisites p)
    {
        var blockers = new List<string>();
        if (!p.PilotEvidenceReady) blockers.Add("OP04_PILOT_EVIDENCE_NOT_READY");
        if (!p.DurableP2Bound) blockers.Add("DURABLE_P2_NOT_BOUND");
        if (!p.AuthoritativeP3Bound) blockers.Add("AUTHORITATIVE_P3_NOT_BOUND");
        if (!p.CandidateGatewayAvailable) blockers.Add("PRODUCTION_GATEWAY_NOT_AVAILABLE");
        if (p.CandidateGatewayIsFailClosed) blockers.Add("CANDIDATE_GATEWAY_IS_FAIL_CLOSED");

        return blockers.Count == 0
            ? new RuntimeActivationDecision(true, "RUNTIME_ACTIVATION_ALLOWED", Array.Empty<string>())
            : new RuntimeActivationDecision(false, "RUNTIME_ACTIVATION_BLOCKED", blockers.AsReadOnly());
    }

    public static ICommandGateway SelectGateway(
        RuntimeActivationDecision decision,
        ICommandGateway? candidate,
        ICommandGateway failClosed)
    {
        if (!decision.Activate || candidate is null || candidate is FailClosedCommandGateway)
            return failClosed;
        return candidate;
    }
}
