using EIMS.HrImport;

var service = new HrOrgImportService();
var tests = new List<(string Name, Func<Task> Run)>
{
    ("P4-CT-01 valid Persian Unicode snapshot passes", ValidPersian),
    ("P4-CT-02 quoted comma field parses correctly", QuotedComma),
    ("P4-CT-03 UTF-8 BOM header is accepted", BomHeader),
    ("P4-CT-04 unclosed quote is rejected", MalformedQuote),
    ("P4-CT-05 canonical header mismatch is rejected", HeaderMismatch),
    ("P4-CT-06 unsupported employment status is rejected", InvalidStatus),
    ("P4-CT-07 non-ISO effective date is rejected", InvalidDate),
    ("P4-CT-08 required field missing is rejected", RequiredMissing),
    ("P4-CT-09 network account must be DOMAIN\\user form", InvalidNetworkAccount),
    ("P4-CT-10 duplicate PersonId is rejected", DuplicatePerson),
    ("P4-CT-11 duplicate EmployeeNumber is rejected", DuplicateEmployee),
    ("P4-CT-12 duplicate NetworkAccount is case-insensitive", DuplicateNetworkAccount),
    ("P4-CT-13 manager self-reference is rejected", ManagerSelfReference),
    ("P4-CT-14 manager must resolve in full snapshot", ManagerMissing),
    ("P4-CT-15 manager graph cycle is rejected", ManagerCycle),
    ("P4-CT-16 OrgUnitCode cannot map to conflicting names", OrgConflict),
    ("P4-CT-17 new person creates approval-gated candidate", CreatePlan),
    ("P4-CT-18 directory attribute change creates update candidate", UpdatePlan),
    ("P4-CT-19 explicit ACTIVE to INACTIVE creates status change", DeactivationPlan),
    ("P4-CT-20 explicit INACTIVE to ACTIVE creates reactivation", ReactivationPlan),
    ("P4-CT-21 absence from full snapshot never auto-deactivates", AbsenceNeverDeactivates),
    ("P4-CT-22 NetworkAccount rebind to another PersonId is blocked", NetworkIdentityCollision),
    ("P4-CT-23 EmployeeNumber rebind to another PersonId is blocked", EmployeeIdentityCollision),
    ("P4-CT-24 unchanged record needs no approval", UnchangedPlan),
    ("P4-CT-25 P4 person contract contains no Role or Scope authority fields", NoRoleScopeFields),
    ("P4-CT-26 file hash is stable and content-sensitive", HashStable)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

string Header() => string.Join(',', P4CanonicalContract.Columns) + "\n";
string Row(
    string person,
    string employee,
    string account,
    string name,
    string orgCode = "RND",
    string orgName = "Research and Development",
    string manager = "",
    string status = "ACTIVE",
    string date = "2026-09-14") =>
    $"{person},{employee},{account},{name},{orgCode},{orgName},{manager},{status},{date}\n";

Task ValidPersian()
{
    var csv = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "کاربر آزمایشی", orgName: "تحقیق و توسعه");
    var result = service.ValidateCanonicalCsv(csv, "B-1");
    True(result.IsValid); Eq(1, result.Records.Count); Eq("کاربر آزمایشی", result.Records.Single().DisplayName);
    return Task.CompletedTask;
}

Task QuotedComma()
{
    var csv = Header() + "P-001,E-001,DOMAIN\\user1,\"Synthetic, User\",RND,\"Research, Development\",,ACTIVE,2026-09-14\n";
    var result = service.ValidateCanonicalCsv(csv);
    True(result.IsValid); Eq("Synthetic, User", result.Records.Single().DisplayName); Eq("Research, Development", result.Records.Single().OrgUnitName);
    return Task.CompletedTask;
}

Task BomHeader()
{
    var csv = "\uFEFF" + Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1");
    True(service.ValidateCanonicalCsv(csv).IsValid);
    return Task.CompletedTask;
}

Task MalformedQuote()
{
    var csv = Header() + "P-001,E-001,DOMAIN\\user1,\"User,RND,RND,,ACTIVE,2026-09-14";
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_CSV_MALFORMED");
    return Task.CompletedTask;
}

Task HeaderMismatch()
{
    var csv = "PersonId,EmployeeNumber\nP-001,E-001\n";
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_HEADER_MISMATCH");
    return Task.CompletedTask;
}

Task InvalidStatus()
{
    var csv = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", status: "ON_LEAVE");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_INVALID_EMPLOYMENT_STATUS");
    return Task.CompletedTask;
}

Task InvalidDate()
{
    var csv = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", date: "1405/06/23");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_INVALID_EFFECTIVE_DATE");
    return Task.CompletedTask;
}

Task RequiredMissing()
{
    var csv = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "", orgName: "RND");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_REQUIRED_FIELD_MISSING");
    return Task.CompletedTask;
}

Task InvalidNetworkAccount()
{
    var csv = Header() + Row("P-001", "E-001", "user1@example.com", "User 1");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_INVALID_NETWORK_ACCOUNT");
    return Task.CompletedTask;
}

Task DuplicatePerson()
{
    var csv = Header()
        + Row("P-001", "E-001", "DOMAIN\\user1", "User 1")
        + Row("P-001", "E-002", "DOMAIN\\user2", "User 2");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_DUPLICATE_PERSON_ID");
    return Task.CompletedTask;
}

Task DuplicateEmployee()
{
    var csv = Header()
        + Row("P-001", "E-001", "DOMAIN\\user1", "User 1")
        + Row("P-002", "E-001", "DOMAIN\\user2", "User 2");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_DUPLICATE_EMPLOYEE_NUMBER");
    return Task.CompletedTask;
}

Task DuplicateNetworkAccount()
{
    var csv = Header()
        + Row("P-001", "E-001", "DOMAIN\\User1", "User 1")
        + Row("P-002", "E-002", "domain\\user1", "User 2");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_DUPLICATE_NETWORK_ACCOUNT");
    return Task.CompletedTask;
}

Task ManagerSelfReference()
{
    var csv = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", manager: "P-001");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_MANAGER_SELF_REFERENCE");
    return Task.CompletedTask;
}

Task ManagerMissing()
{
    var csv = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", manager: "P-999");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_MANAGER_NOT_IN_FULL_SNAPSHOT");
    return Task.CompletedTask;
}

Task ManagerCycle()
{
    var csv = Header()
        + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", manager: "P-002")
        + Row("P-002", "E-002", "DOMAIN\\user2", "User 2", manager: "P-001");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_MANAGER_GRAPH_CYCLE");
    return Task.CompletedTask;
}

Task OrgConflict()
{
    var csv = Header()
        + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", orgCode: "RND", orgName: "R&D")
        + Row("P-002", "E-002", "DOMAIN\\user2", "User 2", orgCode: "rnd", orgName: "Research");
    var result = service.ValidateCanonicalCsv(csv);
    False(result.IsValid); Has(result, "P4_ORG_UNIT_NAME_CONFLICT");
    return Task.CompletedTask;
}

Task CreatePlan()
{
    var current = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1");
    var incoming = current + Row("P-002", "E-002", "DOMAIN\\user2", "User 2");
    var plan = service.BuildPlan(incoming, current);
    True(plan.CanActivate); Eq(1, plan.CreateCount);
    var item = plan.Items.Single(x => x.PersonId == "P-002");
    Eq(ReconciliationAction.Create, item.Action); True(item.RequiresApproval);
    return Task.CompletedTask;
}

Task UpdatePlan()
{
    var current = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", orgCode: "RND", orgName: "R&D");
    var incoming = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User One", orgCode: "ENG", orgName: "Engineering");
    var plan = service.BuildPlan(incoming, current);
    True(plan.CanActivate); Eq(1, plan.UpdateCount); True(plan.Items.Single().RequiresApproval);
    return Task.CompletedTask;
}

Task DeactivationPlan()
{
    var current = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", status: "ACTIVE");
    var incoming = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", status: "INACTIVE");
    var plan = service.BuildPlan(incoming, current);
    True(plan.CanActivate); Eq(1, plan.StatusChangeCount); Eq("EXPLICIT_DEACTIVATION", plan.Items.Single().ReasonCode);
    return Task.CompletedTask;
}

Task ReactivationPlan()
{
    var current = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", status: "INACTIVE");
    var incoming = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1", status: "ACTIVE");
    var plan = service.BuildPlan(incoming, current);
    True(plan.CanActivate); Eq(1, plan.StatusChangeCount); Eq("EXPLICIT_REACTIVATION", plan.Items.Single().ReasonCode);
    return Task.CompletedTask;
}

Task AbsenceNeverDeactivates()
{
    var current = Header()
        + Row("P-001", "E-001", "DOMAIN\\user1", "User 1")
        + Row("P-002", "E-002", "DOMAIN\\user2", "User 2");
    var incoming = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1");
    var plan = service.BuildPlan(incoming, current);
    True(plan.CanActivate); Eq(1, plan.MissingNoChangeCount);
    var item = plan.Items.Single(x => x.PersonId == "P-002");
    Eq(ReconciliationAction.MissingFromSnapshotNoChange, item.Action);
    Eq("ABSENCE_IS_NOT_DEACTIVATION", item.ReasonCode);
    True(plan.Issues.Any(x => x.Code == "P4_MISSING_FROM_SNAPSHOT_NO_CHANGE" && x.Severity == ImportIssueSeverity.Warning));
    return Task.CompletedTask;
}

Task NetworkIdentityCollision()
{
    var current = Header() + Row("P-001", "E-001", "DOMAIN\\shared", "User 1");
    var incoming = Header() + Row("P-002", "E-002", "DOMAIN\\shared", "User 2");
    var plan = service.BuildPlan(incoming, current);
    False(plan.CanActivate); Eq(1, plan.BlockedCount); True(plan.Issues.Any(x => x.Code == "P4_NETWORK_ACCOUNT_IDENTITY_COLLISION"));
    return Task.CompletedTask;
}

Task EmployeeIdentityCollision()
{
    var current = Header() + Row("P-001", "E-SHARED", "DOMAIN\\user1", "User 1");
    var incoming = Header() + Row("P-002", "E-SHARED", "DOMAIN\\user2", "User 2");
    var plan = service.BuildPlan(incoming, current);
    False(plan.CanActivate); Eq(1, plan.BlockedCount); True(plan.Issues.Any(x => x.Code == "P4_EMPLOYEE_NUMBER_IDENTITY_COLLISION"));
    return Task.CompletedTask;
}

Task UnchangedPlan()
{
    var current = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1");
    var plan = service.BuildPlan(current, current);
    True(plan.CanActivate); Eq(1, plan.NoChangeCount); False(plan.Items.Single().RequiresApproval);
    return Task.CompletedTask;
}

Task NoRoleScopeFields()
{
    var names = typeof(HrOrgRecord).GetProperties().Select(x => x.Name).ToArray();
    False(names.Any(x => x.Contains("Role", StringComparison.OrdinalIgnoreCase)));
    False(names.Any(x => x.Contains("Scope", StringComparison.OrdinalIgnoreCase)));
    return Task.CompletedTask;
}

Task HashStable()
{
    var a = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 1");
    var b = Header() + Row("P-001", "E-001", "DOMAIN\\user1", "User 2");
    Eq(CanonicalCsv.Sha256(a), CanonicalCsv.Sha256(a));
    False(string.Equals(CanonicalCsv.Sha256(a), CanonicalCsv.Sha256(b), StringComparison.Ordinal));
    Eq(64, CanonicalCsv.Sha256(a).Length);
    return Task.CompletedTask;
}

static void Has(HrImportValidationResult result, string code)
{
    if (!result.Issues.Any(x => x.Code == code))
        throw new InvalidOperationException($"Expected issue '{code}'.");
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}
