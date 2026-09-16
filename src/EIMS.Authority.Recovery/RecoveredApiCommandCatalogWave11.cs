namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave11 : ICommandPolicyCatalog
{
    public const int Wave11RecoveredMutationCommandCount = 29;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave11()
    {
        var historical = new RecoveredApiCommandCatalogWave10();
        var promoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "benefits.accept",
            "benefits.measure",
            "benefits.verify",
            "benefits.attribution",
            "benefits.realize"
        };

        var entries = historical.All.Where(x => !promoted.Contains(x.CommandName)).Concat(new[]
        {
            Owner("benefits.accept", "BenefitObligationAccepted.v1", "OBLIGATION_PENDING_ACCEPTANCE"),
            Owner("benefits.set-baseline", "BenefitBaselineDefined.v1", "BASELINE_REQUIRED"),
            Owner("benefits.approve-measurement-plan", "BenefitMeasurementPlanApproved.v1", "PLAN_REQUIRED"),
            Measurement("benefits.measure", "BenefitMeasured.v1", "MEASUREMENT_PENDING"),
            Verifier("benefits.verify", "BenefitVerified.v1", "MEASURED"),
            Verifier("benefits.attribution", "BenefitAttributionValidated.v1", "VERIFIED"),
            Owner("benefits.realize", "BenefitRealized.v1", "VALIDATED"),
            Owner("benefits.close", "BenefitClosed.v1", "REALIZED")
        }).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy Owner(string command, string eventName, params string[] states) =>
        Static(command, new[] { "BENEFIT_OWNER" }, eventName, states);

    private static CommandPolicy Verifier(string command, string eventName, params string[] states) =>
        Static(command, new[] { "BENEFIT_VERIFIER" }, eventName, states);

    private static CommandPolicy Measurement(string command, string eventName, params string[] states) =>
        Static(command, new[] { "BENEFIT_OWNER", "AUTHORIZED_DATA_PROVIDER" }, eventName, states);

    private static CommandPolicy Static(
        string command,
        IReadOnlyCollection<string> roles,
        string eventName,
        params string[] states) =>
        new(
            command,
            roles,
            Array.AsReadOnly(states),
            "P1-BENEFIT-ACR-P0-008-1.0",
            eventName,
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("STATIC", StaticEventName: eventName));
}
