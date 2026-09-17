namespace EIMS.PilotAssembly.Binding;

public static class P1P5BindingContract
{
    public const string Version = "P1P5-1.3.0";
    public const int RecoveredMutationCommandCount = 32;
    public const int PortfolioUserCommandCount = 6;
    public const int ExecutionUserCommandCount = 9;
    public const int BenefitUserCommandCount = 8;
    public const int KnowledgeUserCommandCount = 3;
    public const bool SystemPortfolioEligibilityExposedAsUserCommand = false;
    public const bool SystemExecutionHandoffExposedAsUserCommand = false;
    public const bool SystemBenefitObligationIntakeExposedAsUserCommand = false;
    public const bool RequiresGuardedExecutionService = true;
    public const bool RequiresAuthoritativeKnowledgeSourceEvidence = true;
    public const bool RequiresServerResolvedKnowledgeAuthorPolicy = true;
    public const bool RequiresAuthoritativeP3Directory = true;
    public const bool RequiresDurableP2StoreForPilotActivation = true;
    public const bool ClientRoleHeadersTrusted = false;
    public const bool ClientScopeGrantsTrusted = false;
    public const bool ClientOwnershipClaimsTrusted = false;
    public const bool ClientKnowledgeAuthorClaimsTrusted = false;
}
