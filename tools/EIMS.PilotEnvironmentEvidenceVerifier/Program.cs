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

string S(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
bool B(JsonElement e, string name, bool expected) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && (expected ? p.ValueKind == JsonValueKind.True : p.ValueKind == JsonValueKind.False);

var prohibited = new[]
{
    "password", "passwd", "pwd", "connectionstring", "connection_string",
    "token", "privatekey", "private_key", "credential", "secret"
};
var safeAssertionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "noSecretsCommitted" };
var prohibitedHits = new List<string>();

void Walk(JsonElement element, string path)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!safeAssertionKeys.Contains(property.Name))
            {
                var normalized = property.Name.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
                if (prohibited.Any(x => normalized.Contains(x.Replace("_", string.Empty), StringComparison.Ordinal)))
                    prohibitedHits.Add(string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}");
            }
            Walk(property.Value, string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}");
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        var index = 0;
        foreach (var item in element.EnumerateArray()) Walk(item, $"{path}[{index++}]");
    }
}

Walk(root, string.Empty);
var evidenceClass = S(root, "evidenceClass");
Check("ENV-01", new[] { "LAB_EVIDENCE", "PILOT_ENVIRONMENT_EVIDENCE" }.Contains(evidenceClass, StringComparer.Ordinal), "evidence class is controlled");
Check("ENV-02", prohibitedHits.Count == 0, prohibitedHits.Count == 0 ? "no prohibited secret-bearing fields" : $"prohibited fields: {string.Join(',', prohibitedHits)}");

if (Has(root, "schemaVersion"))
{
    Check("ENV-03", S(root, "schemaVersion") == "1.1", "readiness schema version is 1.1");
    Check("ENV-04", Has(root, "windows") && Has(root, "oracle") && Has(root, "p1Authority") && Has(root, "windowsIdentity") && Has(root, "hrOrg") && Has(root, "tls") && Has(root, "runtime") && Has(root, "operations") && Has(root, "security"), "all nine readiness evidence sections exist");

    var windows = root.GetProperty("windows");
    Check("ENV-05", Has(windows, "serverName") && Has(windows, "osVersion") && Has(windows, "vmProvisioned") && Has(windows, "domainJoined") && Has(windows, "domainName") && Has(windows, "iisInstalled") && Has(windows, "windowsAuthenticationInstalled") && Has(windows, "dotnetRuntimeVersion") && DateTimeOffset.TryParse(S(windows, "collectedAt"), out _), "Windows/readiness evidence shape present");

    var windowsIdentity = root.GetProperty("windowsIdentity");
    Check("ENV-06", B(windowsIdentity, "clientIdentityHeadersTrusted", false), "client identity headers remain untrusted");

    var security = root.GetProperty("security");
    Check("ENV-07", Has(security, "noSecretsCommitted") && Has(security, "noPersonalDataCommitted") && Has(security, "reviewedByRole") && Has(security, "reviewDate"), "security evidence shape present");

    Check("ENV-08", Has(root, "discovery"), "non-sensitive discovery section present");
    if (Has(root, "discovery"))
    {
        var discovery = root.GetProperty("discovery");
        var platform = discovery.GetProperty("platform");
        Check("ENV-09", Has(platform, "osCaption") && Has(platform, "osVersion") && Has(platform, "osArchitecture") && Has(platform, "isWindowsServer") && Has(platform, "isAdministrator") && Has(platform, "powershellVersion"), "platform discovery present");
        Check("ENV-10", !Has(platform, "computerName") && !Has(platform, "userName"), "machine/user names omitted from discovery");

        var domain = discovery.GetProperty("domain");
        Check("ENV-11", Has(domain, "joined") && Has(domain, "nameIncludedByExplicitRequest") && Has(domain, "currentPrincipalDomainQualified"), "Domain discovery shape present");
        if (domain.TryGetProperty("nameIncludedByExplicitRequest", out var includeName) && includeName.ValueKind == JsonValueKind.False)
            Check("ENV-12", string.IsNullOrWhiteSpace(S(windows, "domainName")), "Domain name omitted unless explicitly requested");
        else
            Check("ENV-12", true, "Domain name disclosure explicitly requested");

        var dotnet = discovery.GetProperty("dotnet");
        Check("ENV-13", Has(dotnet, "available") && Has(dotnet, "activeSdk") && Has(dotnet, "sdks") && Has(dotnet, "runtimes"), ".NET discovery shape present");

        var iis = discovery.GetProperty("iis");
        Check("ENV-14", Has(iis, "webServer") && Has(iis, "windowsAuthentication") && Has(iis, "webAdministrationModuleAvailable"), "IIS discovery shape present");

        var tls = discovery.GetProperty("tls");
        Check("ENV-15", Has(tls, "queryAvailable") && Has(tls, "nonExpiredServerAuthCount") && Has(tls, "earliestExpiryUtc") && !Has(tls, "thumbprint") && !Has(tls, "subject") && !Has(tls, "privateKey"), "TLS discovery omits certificate identity/key material");

        var oracle = discovery.GetProperty("oracle");
        Check("ENV-16", Has(oracle, "sqlplus") && Has(oracle, "tnsping") && Has(oracle, "tcpProbe"), "Oracle non-credential discovery shape present");
        var tcp = oracle.GetProperty("tcpProbe");
        Check("ENV-17", Has(tcp, "requested") && Has(tcp, "port") && Has(tcp, "tcpSucceeded") && !Has(tcp, "host") && !Has(tcp, "address"), "Oracle TCP probe omits target hostname/address");
    }
}
else
{
    Check("ENV-03", S(root, "evidenceSchema") == "EIMS-PILOT-ENV-EVIDENCE-1.0", "legacy evidence schema identity");
    Check("ENV-04", DateTimeOffset.TryParse(S(root, "collectedAtUtc"), out _), "legacy collection timestamp is parseable");

    var platform = root.GetProperty("platform");
    Check("ENV-05", Has(platform, "osCaption") && Has(platform, "osVersion") && Has(platform, "osArchitecture") && Has(platform, "isWindowsServer") && Has(platform, "isAdministrator") && Has(platform, "powershellVersion"), "legacy platform evidence present");
    Check("ENV-06", !Has(platform, "computerName") && !Has(platform, "userName"), "legacy machine/user names omitted");

    var domain = root.GetProperty("domain");
    Check("ENV-07", Has(domain, "joined") && Has(domain, "currentPrincipalDomainQualified"), "legacy Domain evidence shape present");
    Check("ENV-08", !domain.GetProperty("nameIncludedByExplicitRequest").GetBoolean() ? domain.GetProperty("name").ValueKind == JsonValueKind.Null : true, "legacy Domain name disclosed only on explicit request");

    var dotnet = root.GetProperty("dotnet");
    Check("ENV-09", Has(dotnet, "available") && Has(dotnet, "activeSdk") && Has(dotnet, "sdks") && Has(dotnet, "runtimes"), "legacy .NET evidence shape present");

    var iis = root.GetProperty("iis");
    Check("ENV-10", Has(iis, "webServer") && Has(iis, "windowsAuthentication") && Has(iis, "webAdministrationModuleAvailable"), "legacy IIS evidence shape present");

    var tls = root.GetProperty("tls");
    Check("ENV-11", Has(tls, "queryAvailable") && Has(tls, "nonExpiredServerAuthCount") && Has(tls, "earliestExpiryUtc") && !Has(tls, "thumbprint") && !Has(tls, "subject") && !Has(tls, "privateKey"), "legacy TLS summary omits certificate identities/key material");

    var oracle = root.GetProperty("oracle");
    Check("ENV-12", Has(oracle, "sqlplus") && Has(oracle, "tnsping") && Has(oracle, "tcpProbe"), "legacy Oracle evidence shape present");
    var tcp = oracle.GetProperty("tcpProbe");
    Check("ENV-13", Has(tcp, "requested") && Has(tcp, "port") && Has(tcp, "tcpSucceeded") && !Has(tcp, "host") && !Has(tcp, "address"), "legacy Oracle TCP probe omits target hostname/address");
}

foreach (var line in pass) Console.WriteLine(line);
foreach (var line in fail) Console.Error.WriteLine(line);
Console.WriteLine($"RESULT {pass.Count}/{pass.Count + fail.Count} PASS");
return fail.Count == 0 ? 0 : 1;
