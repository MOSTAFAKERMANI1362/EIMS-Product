using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.EvaluatorSchemaRecovery.ContractTests <registry-json>");
    return 2;
}

using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = doc.RootElement;
var roles = root.GetProperty("roleSchemas").EnumerateArray().ToArray();
var tests = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => tests.Add((id, name, pass));
JsonElement Role(string role) => roles.Single(x => S(x,"role") == role);

Add("ESR-CT-01", "registry identity and schema are exact",
    S(root,"schema") == "EIMS-EVALUATOR-ASSESSMENT-SCHEMA-REGISTRY-1.0"
    && S(root,"artifactIdentity") == "EVALUATOR_ASSESSMENT_SCHEMA_REGISTRY_v1.0");
Add("ESR-CT-02", "recovery classification explicitly avoids original-server claim",
    S(root,"recoveryStatus") == "RECOVERED_FROM_FROZEN_V6_360_NOT_ORIGINAL_SERVER_SCHEMA"
    && !B(root.GetProperty("provenance"),"historicalOriginalServerAssessmentSchemaAvailable")
    && B(root.GetProperty("provenance"),"mustNotBeRepresentedAsOriginalServerSchema"));
Add("ESR-CT-03", "frozen v6.360 identity and SHA are preserved",
    S(root.GetProperty("sourceProduct"),"file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html"
    && S(root.GetProperty("sourceProduct"),"sha256") == "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a"
    && !B(root.GetProperty("sourceProduct"),"modifiedByThisRecovery"));
Add("ESR-CT-04", "recovery forbids invented questions and silent constraint strengthening",
    B(root.GetProperty("provenance"),"noNewQuestionsInvented")
    && B(root.GetProperty("provenance"),"noConstraintStrengtheningBeyondFrozenEvidence"));

var server = root.GetProperty("serverBinding");
Add("ESR-CT-05", "schema is server-bound to evaluator mission",
    B(server,"schemaVersionMustBeBoundToEvaluationAssignment")
    && B(server,"schemaSelectedByServerFromEvaluationRole"));
Add("ESR-CT-06", "client cannot substitute schema version or role schema",
    !B(server,"clientMaySelectArbitrarySchemaVersion")
    && !B(server,"clientMaySubstituteDifferentRoleSchema")
    && S(server,"missingRoleSchemaAction") == "FAIL_CLOSED");
Add("ESR-CT-07", "identity authority role and scope are server-derived",
    B(server,"evaluatorPersonIdServerDerived")
    && B(server,"authorityAssignmentIdServerDerived")
    && B(server,"evaluationRoleServerDerivedFromMission")
    && B(server,"evaluationScopeServerDerivedFromMissionAndAuthority"));

var workflow = root.GetProperty("workflowSemantics");
Add("ESR-CT-08", "workflow completion and assessment outcome are separate",
    B(workflow,"successfulValidatedSubmissionCompletesMission")
    && B(workflow,"workflowCompletionIndependentOfPositiveOutcome")
    && B(workflow,"assessmentOutcomePreservedSeparately"));
Add("ESR-CT-09", "negative professional outcome may still complete workflow mission",
    B(workflow,"negativeOutcomeMayStillBeCompletedEvaluation")
    && B(workflow,"historicalCompletedStatusAndOutcomeAreSeparate"));

var expectedRoles = new[] { "FINANCIAL_ASSESSOR", "HSE_ASSESSOR", "IDEA_EVALUATOR", "IT_ASSESSOR", "TECHNICAL_ASSESSOR", "UNIT_OWNER_REVIEWER" };
Add("ESR-CT-10", "registry contains exactly the six frozen G04 evaluator roles",
    roles.Length == 6 && roles.Select(x => S(x,"role")).OrderBy(x => x).SequenceEqual(expectedRoles));
Add("ESR-CT-11", "role schema IDs are unique and versioned",
    roles.All(x => !string.IsNullOrWhiteSpace(S(x,"schemaId")) && S(x,"schemaVersion") == "1.0" && S(x,"sourceBaseline") == "v6.360")
    && roles.Select(x => S(x,"schemaId")).Distinct(StringComparer.Ordinal).Count() == 6);

var generic = root.GetProperty("sharedGenericSpecialistContract");
var genericRoles = Strings(generic,"appliesToRoles");
Add("ESR-CT-12", "generic contract applies exactly to Unit Owner HSE Financial and IT",
    genericRoles.SetEquals(new[] { "UNIT_OWNER_REVIEWER","HSE_ASSESSOR","FINANCIAL_ASSESSOR","IT_ASSESSOR" }));
var perQuestion = generic.GetProperty("perQuestion");
Add("ESR-CT-13", "generic per-question answer and note are required",
    B(perQuestion,"answerRequired") && B(perQuestion,"noteRequired") && I(perQuestion,"noteMinLength") == 3);
var genericAnswers = OptionValues(perQuestion,"answerOptions");
Add("ESR-CT-14", "generic answer enum is exact final-v6.360 contract",
    genericAnswers.SequenceEqual(new[] { "CONFIRMED","CONDITIONAL","NOT_CONFIRMED","MORE_EVIDENCE" }));
Add("ESR-CT-15", "generic answer labels are exact",
    OptionMap(perQuestion,"answerOptions")["CONFIRMED"] == "تأیید شد"
    && OptionMap(perQuestion,"answerOptions")["CONDITIONAL"] == "مشروط / نیازمند اقدام"
    && OptionMap(perQuestion,"answerOptions")["NOT_CONFIRMED"] == "تأیید نشد"
    && OptionMap(perQuestion,"answerOptions")["MORE_EVIDENCE"] == "اطلاعات یا شاهد کافی نیست");
var overall = generic.GetProperty("overall");
Add("ESR-CT-16", "generic overall outcome enum is exact",
    OptionValues(overall,"outcomeOptions").SequenceEqual(new[] { "PASS","CONDITIONAL_PASS","RETURN","FAIL" }));
Add("ESR-CT-17", "generic score evidence and comment constraints are exact",
    B(overall,"scoreRequired") && I(overall,"scoreMin") == 1 && I(overall,"scoreMax") == 5
    && B(overall,"evidenceRefRequired") && I(overall,"evidenceRefMinLength") == 3
    && B(overall,"commentRequired") && I(overall,"commentMinLength") == 20);
var evidence = generic.GetProperty("evidencePolicy");
Add("ESR-CT-18", "existing EIMS evidence references are accepted without universal new upload",
    B(evidence,"existingEimsReferenceAccepted")
    && B(evidence,"existingNeedIdeaPassportReportAttachmentReferenceAccepted")
    && !B(evidence,"newUploadUniversallyRequired"));

var idea = Role("IDEA_EVALUATOR");
var ideaCriteria = idea.GetProperty("criteria").EnumerateArray().ToArray();
Add("ESR-CT-19", "structured Idea evaluator has exact six criterion keys",
    ideaCriteria.Select(x => S(x,"key")).SequenceEqual(new[] { "quality","cost","technical","productionRisk","timeReturn","strategy" }));
Add("ESR-CT-20", "structured Idea weights are exact and total 100",
    ideaCriteria.Select(x => I(x,"weightPercent")).SequenceEqual(new[] {20,20,15,15,10,20})
    && ideaCriteria.Sum(x => I(x,"weightPercent")) == 100);
Add("ESR-CT-21", "all structured Idea scores require 1..5 and criterion notes stay optional",
    ideaCriteria.All(x => B(x,"scoreRequired") && I(x,"scoreMin") == 1 && I(x,"scoreMax") == 5 && !B(x,"noteRequired")));
var weighted = idea.GetProperty("weightedScore");
Add("ESR-CT-22", "weighted score is derived decision support only",
    B(weighted,"derived") && !B(weighted,"clientAuthoritative") && B(weighted,"decisionSupportOnly"));
var recommendation = idea.GetProperty("recommendation");
Add("ESR-CT-23", "structured evaluator recommendation is required with exact options",
    B(recommendation,"required")
    && OptionValues(recommendation,"options").SequenceEqual(new[] { "GO","REVISE","NO_GO","PILOT","TECH_REVIEW" }));
Add("ESR-CT-24", "structured evaluator final note requires minimum 10",
    B(idea.GetProperty("evaluatorNote"),"required") && I(idea.GetProperty("evaluatorNote"),"minLength") == 10);

var technical = Role("TECHNICAL_ASSESSOR");
var techCriteria = technical.GetProperty("criteria").EnumerateArray().ToArray();
Add("ESR-CT-25", "technical schema has exact eight criterion keys",
    techCriteria.Select(x => S(x,"key")).SequenceEqual(new[] { "maturity","integration","infrastructure","maintainability","testability","vendor","skills","standards" }));
Add("ESR-CT-26", "technical scores require 1..5 while criterion notes are not universally required",
    techCriteria.All(x => B(x,"scoreRequired") && I(x,"scoreMin") == 1 && I(x,"scoreMax") == 5 && !B(x,"noteRequired")));
Add("ESR-CT-27", "technical decision enum is exact",
    OptionValues(technical.GetProperty("decision"),"options").SequenceEqual(new[] { "PASS","CONDITIONAL_PASS","FAIL","MORE_EVIDENCE" }));
Add("ESR-CT-28", "technical pilot recommendation enum is exact",
    OptionValues(technical.GetProperty("pilotRecommendation"),"options").SequenceEqual(new[] { "YES","NO","CONDITIONAL" }));
Add("ESR-CT-29", "technical evidence and summary are required and conditional conditions are exact",
    B(technical.GetProperty("evidenceRef"),"required") && I(technical.GetProperty("evidenceRef"),"minLength") == 1
    && B(technical.GetProperty("summary"),"required") && I(technical.GetProperty("summary"),"minLength") == 1
    && S(technical.GetProperty("conditions").GetProperty("conditionalRequiredWhen"),"field") == "decision"
    && S(technical.GetProperty("conditions").GetProperty("conditionalRequiredWhen"),"equals") == "CONDITIONAL_PASS");
var excluded = technical.GetProperty("prototypeAuthorityFieldsExcluded").EnumerateArray().ToArray();
Add("ESR-CT-30", "technical prototype assessor and unit fields are excluded from production authority input",
    excluded.Select(x => S(x,"prototypeField")).OrderBy(x => x).SequenceEqual(new[] { "taAssessor612","taUnit612" })
    && excluded.All(x => !B(x,"productionInputAllowed")));
var techSplit = technical.GetProperty("workflowOutcomeSeparation");
Add("ESR-CT-31", "technical mission may complete with all four professional outcomes",
    Strings(techSplit,"assignmentMayCompleteWithDecisionValues").SetEquals(new[] { "PASS","CONDITIONAL_PASS","FAIL","MORE_EVIDENCE" })
    && B(techSplit,"positiveG04ReadinessOutcomeIsSeparateFromWorkflowCompletion"));

var unit = Role("UNIT_OWNER_REVIEWER");
Add("ESR-CT-32", "Unit Owner question keys and texts are exact",
    QuestionsMatch(unit, new[] {
        ("needFit","آیا راهکار مستقیماً شکاف نیاز و وضعیت مطلوب واحد را پوشش می‌دهد؟"),
        ("operationFit","آیا محدودیت‌های بهره‌برداری، توقف، فضا و دسترسی در نظر گرفته شده‌اند؟"),
        ("resources","آیا منابع انسانی، زمان، آموزش و مسئول بهره‌برداری قابل تأمین‌اند؟"),
        ("ownership","آیا واحد مسئولیت مشارکت در پایلوت، پذیرش و بهره‌برداری را می‌پذیرد؟")
    }));
var hse = Role("HSE_ASSESSOR");
Add("ESR-CT-33", "HSE question keys and texts are exact",
    QuestionsMatch(hse, new[] {
        ("hazards","آیا خطرات ایمنی، بهداشت و محیط‌زیست راهکار شناسایی شده‌اند؟"),
        ("legal","آیا الزامات قانونی، استانداردها و مجوزهای لازم مشخص و رعایت شده‌اند؟"),
        ("controls","آیا کنترل‌های پیشگیرانه، اضطراری و معیار پذیرش HSE کافی‌اند؟"),
        ("residualRisk","آیا ریسک باقیمانده پس از کنترل‌ها قابل قبول و مستند است؟")
    }));
var financial = Role("FINANCIAL_ASSESSOR");
Add("ESR-CT-34", "Financial question keys and texts are exact",
    QuestionsMatch(financial, new[] {
        ("costBasis","آیا CAPEX/OPEX و مبنای برآورد هزینه مستند و قابل اتکاست؟"),
        ("lifeCycle","آیا هزینه چرخه عمر، نگهداشت، مجوز، آموزش و توقف لحاظ شده است؟"),
        ("benefitBasis","آیا منفعت مالی یا غیرمالی و روش جلوگیری از دوباره‌شماری روشن است؟"),
        ("funding","آیا منبع بودجه، دوره بازگشت و فرض‌های مالی مشخص شده‌اند؟")
    }));
var it = Role("IT_ASSESSOR");
Add("ESR-CT-35", "IT question keys and texts are exact",
    QuestionsMatch(it, new[] {
        ("architecture","آیا معماری مقصد، مالک سامانه و نحوه پشتیبانی روشن است؟"),
        ("security","آیا امنیت، سطح محرمانگی، دسترسی و نگهداری داده بررسی شده است؟"),
        ("integration","آیا اتصال به Oracle، SAP آینده یا سامانه تخصصی و مالک Interface مشخص است؟"),
        ("continuity","آیا مجوز، پشتیبان‌گیری، تداوم خدمت، خروج از فروشنده و انتقال دانش دیده شده است؟")
    }));
Add("ESR-CT-36", "all four generic specialist roles use the shared contract",
    new[] {unit,hse,financial,it}.All(x => B(x,"usesSharedGenericSpecialistContract") && S(x,"kind") == "GENERIC_SPECIALIST_CHECKLIST"));

var nonEffects = Strings(root,"nonEffects");
Add("ESR-CT-37", "registry does not modify frozen baseline or promote evaluator runtime",
    nonEffects.Contains("Does not modify frozen v6.360")
    && nonEffects.Contains("Does not promote evaluation-assignments.complete into P1 runtime"));
Add("ESR-CT-38", "registry does not grant authority or trust prototype identity fields",
    nonEffects.Contains("Does not grant evaluator roles or scopes")
    && nonEffects.Contains("Does not trust prototype assessor/unit text fields as production authority"));
Add("ESR-CT-39", "registry makes no P5 or live environment readiness claim",
    nonEffects.Contains("Does not bind P5")
    && nonEffects.Contains("Does not claim live Oracle, Windows Domain or Network Pilot readiness"));

foreach (var t in tests)
    Console.WriteLine($"{(t.Pass ? "PASS" : "FAIL")} {t.Id} {t.Name}");
var passed = tests.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

static string S(JsonElement e, string p) =>
    e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
static int I(JsonElement e, string p) => e.GetProperty(p).GetInt32();
static HashSet<string> Strings(JsonElement e, string p) =>
    e.GetProperty(p).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
static string[] OptionValues(JsonElement e, string p) =>
    e.GetProperty(p).EnumerateArray().Select(x => S(x,"value")).ToArray();
static Dictionary<string,string> OptionMap(JsonElement e, string p) =>
    e.GetProperty(p).EnumerateArray().ToDictionary(x => S(x,"value"), x => S(x,"label"), StringComparer.Ordinal);
static bool QuestionsMatch(JsonElement role, IEnumerable<(string Key,string Text)> expected)
{
    var actual = role.GetProperty("questions").EnumerateArray().Select(x => (S(x,"key"),S(x,"textFa"))).ToArray();
    return actual.SequenceEqual(expected);
}
