using EIMS.P0.MachineRecovery;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: EIMS.P0.MachineRecoveryVerifier <P0_MACHINE_CONTRACT_RECOVERY_v1.0.json>");
    return 2;
}

if (!File.Exists(args[0]))
{
    Console.Error.WriteLine($"Contract file not found: {args[0]}");
    return 2;
}

try
{
    var json = File.ReadAllText(args[0]);
    var checks = P0MachineRecoveryValidator.Validate(json);
    foreach (var check in checks)
        Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Id} {check.Description}");

    var passed = checks.Count(x => x.Passed);
    Console.WriteLine($"RESULT {passed}/{checks.Count} PASS");
    return passed == checks.Count ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL P0R-PARSE {ex.GetType().Name}: {ex.Message}");
    return 1;
}
