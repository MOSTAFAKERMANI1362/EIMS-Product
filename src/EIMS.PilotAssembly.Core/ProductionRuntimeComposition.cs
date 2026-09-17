namespace EIMS.PilotAssembly.Core;

public interface IProductionRuntimeComposition
{
    bool DurableP2Bound { get; }
    bool AuthoritativeP3Bound { get; }
    string? CompositionEvidenceRef { get; }
    ICommandGateway? CandidateGateway { get; }
}

/// <summary>
/// Safe default used by the Pilot Host until a real environment-specific composition is supplied.
/// Configuration flags cannot turn this implementation into a production gateway.
/// </summary>
public sealed class UnavailableProductionRuntimeComposition : IProductionRuntimeComposition
{
    public bool DurableP2Bound => false;
    public bool AuthoritativeP3Bound => false;
    public string? CompositionEvidenceRef => null;
    public ICommandGateway? CandidateGateway => null;
}
