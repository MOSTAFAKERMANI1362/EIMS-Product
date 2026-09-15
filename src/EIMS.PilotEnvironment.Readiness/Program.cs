using System.Text.Json;
using EIMS.PilotEnvironment.Readiness;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.PilotEnvironment.Readiness <pilot-environment-evidence.json>");
    return 2;
}

try
{
    var json = await File.ReadAllTextAsync(args[0]);
    var report = EnvironmentEvidenceEvaluator.Evaluate(json);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return report.PilotActivationReady ? 0 : 3;
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"INVALID_EVIDENCE_JSON: {ex.Message}");
    return 2;
}
