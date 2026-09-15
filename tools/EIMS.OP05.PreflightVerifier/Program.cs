using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.OP05.PreflightVerifier <op-05-test-matrix.v1.1.json>");
    return 2;
}

var requiredIds = new HashSet<string>(StringComparer.Ordinal)
{
    "VS-IDENTITY-01","VS-AUTH-01","VS-AUTH-02","VS-ASSIGN-01","VS-CONC-01","VS-IDEMP-01","VS-TX-01","VS-TRACE-01",
    "VS-G04-01","VS-G04-02","VS-G04-03",
    "VS-PORT-01","VS-PORT-02","VS-PORT-03","VS-PORT-04","VS-PORT-05",
    "VS-EXEC-01","VS-EXEC-02","VS-EXEC-03","VS-EXEC-04",
    "VS-BEN-01","VS-BEN-02","VS-BEN-03","VS-BEN-04",
    "VS-KNOW-01","VS-KNOW-02","VS-KNOW-03","VS-AI-01"
};

try
{
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
    var root = doc.RootElement;
    var failures = new List<string>();

    string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    bool? B(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    if (S("version") != "1.1") failures.Add("version must be 1.1");
    if (S("operation") != "OP-05") failures.Add("operation must be OP-05");
    if (S("exitRule") != "ALL_REQUIRED_PASS") failures.Add("exitRule must be ALL_REQUIRED_PASS");
    if (B("allowTestOnlyShortcutForFinalPass") != false) failures.Add("test-only shortcut must be false");
    if (B("liveEnvironmentRequiredForFinalPass") != true) failures.Add("live environment must be required for final pass");

    if (!root.TryGetProperty("mandatoryTests", out var tests) || tests.ValueKind != JsonValueKind.Array)
    {
        failures.Add("mandatoryTests array missing");
    }
    else
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests.EnumerateArray())
        {
            if (!test.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            {
                failures.Add("test without id");
                continue;
            }
            var id = idProp.GetString()!;
            if (!seen.Add(id)) failures.Add($"duplicate test id: {id}");
            if (!test.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.True)
                failures.Add($"mandatory test must be required=true: {id}");
            if (!test.TryGetProperty("evidence", out var evidence) || evidence.GetString() != "LIVE")
                failures.Add($"mandatory test must require LIVE evidence: {id}");
        }

        foreach (var id in requiredIds)
            if (!seen.Contains(id)) failures.Add($"missing mandatory test: {id}");

        foreach (var id in seen)
            if (!requiredIds.Contains(id)) failures.Add($"unreviewed mandatory test id: {id}");
    }

    if (failures.Count > 0)
    {
        foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
        Console.Error.WriteLine($"RESULT FAIL {failures.Count} issue(s)");
        return 1;
    }

    Console.WriteLine($"RESULT PASS {requiredIds.Count}/{requiredIds.Count} mandatory OP-05 preflight tests locked");
    Console.WriteLine("NOTE Final OP-05 PASS still requires live OP-04 environment evidence.");
    return 0;
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"INVALID_JSON {ex.Message}");
    return 2;
}
