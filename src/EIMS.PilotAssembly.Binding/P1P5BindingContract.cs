namespace EIMS.PilotAssembly.Binding;

public static class P1P5BindingContract
{
    public const string Version = "P1P5-1.0.0";
    public const int RecoveredMutationCommandCount = 6;
    public const bool RequiresAuthoritativeP3Directory = true;
    public const bool RequiresDurableP2StoreForPilotActivation = true;
    public const bool ClientRoleHeadersTrusted = false;
    public const bool ClientScopeGrantsTrusted = false;
}
