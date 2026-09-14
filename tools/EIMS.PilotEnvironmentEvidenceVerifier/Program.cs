using System.Text.Json;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("FAIL ENV-00 evidence file is required.");
    return 2;
}

using var document = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var root = document.RootElement;
var pass = new List<string>();
var fail = new List<string>();

void Check(string id, bool ok, string message) =>
    (ok ? pass : fail).Add($"{(ok ? "PASS" : "FAIL")} {id} {message}");

string S(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? string.Empty : string.Empty;
bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);

var prohibited = new[]
{
    "password", "passwd", "pwd", "connectionstring", "connection_string",
    "token", "privatekey", "private_key", "credential", "secret"
};

var prohibitedHits = new List<string>();
void Walk(JsonElement element, string path)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            var normalized = property.Name.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (prohibited.Any(x => normalized.Contains(x.Replace("_", string.Empty), StringComparison.Ordinal)))
                prohibitedHits.Add(string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}");
            Walk(property.Value, string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}");
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            Walk(item, $"{path}[{index}]");
            index++;
        }
    }
}
Walk(root, string.Empty);

Check("ENV-01", S(root, "evidenceSchema") == "EIMS-PILOT-ENV-EVIDENCE-1.0", "evidence schema identity");
Check("ENV-02", new[] { "LAB_EVIDENCE", "PILOT_ENVIRONMENT_EVIDENCE" }.Contains(S(root, "evidenceClass"), StringComparer.Ordinal), "evidence class is controlled");
Check("ENV-03", DateTimeOffset.TryParse(S(root, "collectedAtUtc"), out _), "collection timestamp is parseable");
Check("ENV-04", prohibitedHits.Count == 0, prohibitedHits.Count == 0 ? "no prohibited secret-bearing fields" : $"prohibited fields: {string.Join(',', prohibitedHits)}");

var platform = root.GetProperty("platform");
Check("ENV-05", Has(platform, "osCaption") && Has(platform, "osVersion") && Has(platform, "osArchitecture"), "OS evidence present");
Check("ENV-06", Has(platform, "isWindowsServer") && Has(platform, "isAdministrator") && Has(platform, "powershellVersion"), "platform readiness evidence present");
Check("ENV-07", !Has(platform, "computerName") && !Has(platform, "userName"), "unnecessary machine/user names are omitted");

var domain = root.GetProperty("domain");
Check("ENV-08", Has(domain, "joined") && Has(domain, "currentPrincipalDomainQualified"), "Domain evidence shape present");
Check("ENV-09", !domain.GetProperty("nameIncludedByExplicitRequest").GetBoolean() ? domain.GetProperty("name").ValueKind == JsonValueKind.Null : true, "Domain name is disclosed only on explicit request");

var dotnet = root.GetProperty("dotnet");
Check("ENV-10", Has(dotnet, "available") && Has(dotnet, "activeSdk") && Has(dotnet, "sdks") && Has(dotnet, "runtimes"), ".NET evidence shape present");

var iis = root.GetProperty("iis");
Check("ENV-11", Has(iis, "webServer") && Has(iis, "windowsAuthentication") && Has(iis, "webAdministrationModuleAvailable"), "IIS evidence shape present");

var tls = root.GetProperty("tls");
Check("ENV-12", Has(tls, "queryAvailable") && Has(tls, "nonExpiredServerAuthCount") && Has(tls, "earliestExpiryUtc"), "TLS summary contains no certificate identities or key material");
Check("ENV-13", !Has(tls, "thumbprint") && !Has(tls, "subject") && !Has(tls, "privateKey"), "certificate identity/private-key fields omitted");

var oracle = root.GetProperty("oracle");
Check("ENV-14", Has(oracle, "sqlplus") && Has(oracle, "tnsping") && Has(oracle, "tcpProbe"), "Oracle non-credential evidence shape present");
var tcp = oracle.GetProperty("tcpProbe");
Check("ENV-15", Has(tcp, "requested") && Has(tcp, "port") && Has(tcp, "tcpSucceeded") && !Has(tcp, "host") && !Has(tcp, "address"), "Oracle TCP probe omits target hostname/address from output");

foreach (var line in pass) Console.WriteLine(line);
foreach (var line in fail) Console.Error.WriteLine(line);
Console.WriteLine($"RESULT {pass.Count}/{pass.Count + fail.Count} PASS");
return fail.Count == 0 ? 0 : 1;
