using System.Text.Json;
using EIMS.HrImport;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: EIMS.P4.HrImportCli validate <incoming.csv> | plan <incoming.csv> <current.csv>");
    return 2;
}

var command = args[0].Trim().ToLowerInvariant();
var incomingPath = args[1];
if (!File.Exists(incomingPath))
{
    Console.Error.WriteLine("P4_INPUT_FILE_NOT_FOUND");
    return 2;
}

var service = new HrOrgImportService();
var options = new JsonSerializerOptions { WriteIndented = true };

if (command == "validate")
{
    var csv = await File.ReadAllTextAsync(incomingPath);
    var result = service.ValidateCanonicalCsv(csv);
    var summary = new
    {
        operation = "validate",
        schemaVersion = result.Metadata.SchemaVersion,
        importMode = result.Metadata.ImportMode,
        sourceSystem = result.Metadata.SourceSystem,
        fileSha256 = result.Metadata.FileSha256,
        isValid = result.IsValid,
        recordCount = result.Records.Count,
        errorCount = result.ErrorCount,
        warningCount = result.WarningCount,
        issues = result.Issues.Select(x => new
        {
            rowNumber = x.RowNumber,
            code = x.Code,
            severity = x.Severity.ToString(),
            field = x.Field
        })
    };
    Console.WriteLine(JsonSerializer.Serialize(summary, options));
    return result.IsValid ? 0 : 1;
}

if (command == "plan")
{
    if (args.Length < 3 || !File.Exists(args[2]))
    {
        Console.Error.WriteLine("P4_CURRENT_DIRECTORY_FILE_NOT_FOUND");
        return 2;
    }

    var incomingCsv = await File.ReadAllTextAsync(incomingPath);
    var currentCsv = await File.ReadAllTextAsync(args[2]);
    var plan = service.BuildPlan(incomingCsv, currentCsv);
    var summary = new
    {
        operation = "plan",
        schemaVersion = plan.Metadata.SchemaVersion,
        fileSha256 = plan.Metadata.FileSha256,
        canActivate = plan.CanActivate,
        createCount = plan.CreateCount,
        updateCount = plan.UpdateCount,
        statusChangeCount = plan.StatusChangeCount,
        noChangeCount = plan.NoChangeCount,
        missingNoChangeCount = plan.MissingNoChangeCount,
        blockedCount = plan.BlockedCount,
        issues = plan.Issues.Select(x => new
        {
            rowNumber = x.RowNumber,
            code = x.Code,
            severity = x.Severity.ToString(),
            field = x.Field
        }),
        actions = plan.Items
            .GroupBy(x => x.Action)
            .ToDictionary(x => x.Key.ToString(), x => x.Count())
    };
    Console.WriteLine(JsonSerializer.Serialize(summary, options));
    return plan.CanActivate ? 0 : 1;
}

Console.Error.WriteLine("P4_UNKNOWN_OPERATION");
return 2;
