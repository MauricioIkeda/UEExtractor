using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Readers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTE.StructureValidatorProbe;

internal static class FixedProgram
{
    private static readonly Regex SquareControlRegex = new(@"\[[A-Za-z][A-Za-z0-9_:\-]*/\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnySquareRegex = new(@"\[[^\]\r\n]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GenderRegex = new(
        @"(?<pre>.*?)<male=>(?<male>.*?)<male>(?<mid>.*?)<female=>(?<female>.*?)<female>(?<post>.*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly string[] GenderMarkers = ["<male=>", "<male>", "<female=>", "<female>"];

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: NTE.StructureValidatorProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
                return 2;
            }

            var gameRoot = Path.GetFullPath(args[0]);
            var outputDir = Path.GetFullPath(args[1]);
            string? aesConfig = null;
            string? aesFile = null;

            foreach (var arg in args.Skip(2))
            {
                if (arg.StartsWith("--aes-config=", StringComparison.OrdinalIgnoreCase))
                    aesConfig = Path.GetFullPath(arg["--aes-config=".Length..].Trim('"'));
                else if (arg.StartsWith("--aes-file=", StringComparison.OrdinalIgnoreCase))
                    aesFile = Path.GetFullPath(arg["--aes-file=".Length..].Trim('"'));
                else
                    throw new ArgumentException($"Unknown argument: {arg}");
            }

            Directory.CreateDirectory(outputDir);

            Console.WriteLine("NTE Structure Validator Probe 015");
            Console.WriteLine("Purpose: exercise source-relative structural validation against real ES entries");
            Console.WriteLine("Offline only; no game archive or LOCRES is modified.");
            Console.WriteLine();

            var aesKey = (string)InvokeProgram("LoadAesKey", aesConfig, aesFile)!;
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = (UnrealArchiveReader)InvokeProgram("CreateReaderWithoutLeakingAes", gameRoot)!;
            var provider = (DefaultFileProvider)InvokeProgram("GetProvider", reader)!;
            var esPath = (string)InvokeProgram("FindLocres", provider, "/Content/Localization/Game/es/Game.locres")!;
            var esFileResolved = (GameFile)InvokeProgram("Resolve", provider, esPath)!;
            var sourceDescription = (SourceDescription)InvokeProgram("DescribeSource", esFileResolved)!;
            var entries = (Dictionary<string, LocaleEntry>)InvokeProgram("ParseLocale", esFileResolved)!;

            var cases = BuildCases(entries, out var skippedCategories);
            var findingRows = new List<FindingRow>();
            var caseRows = new List<CaseResult>();

            foreach (var testCase in cases)
            {
                var findings = Validate(testCase.SourceText, testCase.TranslationText);
                foreach (var finding in findings)
                {
                    findingRows.Add(new FindingRow
                    {
                        CaseId = testCase.Id,
                        Identity = testCase.Identity,
                        Severity = finding.Severity,
                        Code = finding.Code,
                        Message = finding.Message
                    });
                }

                var errorCount = findings.Count(x => x.Severity == "error");
                var warningCount = findings.Count(x => x.Severity == "warning");
                var actual = errorCount > 0 ? "error" : warningCount > 0 ? "warning_only" : "pass";
                var matched = string.Equals(actual, testCase.ExpectedOutcome, StringComparison.Ordinal);

                caseRows.Add(new CaseResult
                {
                    Id = testCase.Id,
                    Identity = testCase.Identity,
                    Description = testCase.Description,
                    ExpectedOutcome = testCase.ExpectedOutcome,
                    ActualOutcome = actual,
                    MatchedExpectation = matched,
                    ErrorCount = errorCount,
                    WarningCount = warningCount,
                    SourcePreview = Preview(testCase.SourceText),
                    TranslationPreview = Preview(testCase.TranslationText)
                });
            }

            WriteJsonl(Path.Combine(outputDir, "validator-cases.jsonl"), caseRows);
            WriteJsonl(Path.Combine(outputDir, "validator-findings.jsonl"), findingRows);
            WriteJson(Path.Combine(outputDir, "skipped-categories.json"), new { skippedCategories });

            var summary = new ProbeSummary
            {
                EsVirtualPath = esPath,
                EsContainerName = sourceDescription.ContainerName,
                EsReadOrder = sourceDescription.ReadOrder,
                EsEntryCount = entries.Count,
                CaseCount = caseRows.Count,
                PassedExpectationCount = caseRows.Count(x => x.MatchedExpectation),
                FailedExpectationCount = caseRows.Count(x => !x.MatchedExpectation),
                PassCaseCount = caseRows.Count(x => x.ActualOutcome == "pass"),
                WarningOnlyCaseCount = caseRows.Count(x => x.ActualOutcome == "warning_only"),
                ErrorCaseCount = caseRows.Count(x => x.ActualOutcome == "error"),
                FindingCount = findingRows.Count,
                ErrorFindingCount = findingRows.Count(x => x.Severity == "error"),
                WarningFindingCount = findingRows.Count(x => x.Severity == "warning"),
                Success = caseRows.All(x => x.MatchedExpectation)
            };
            WriteJson(Path.Combine(outputDir, "summary.json"), summary);

            Console.WriteLine($"Effective ES entries: {entries.Count}");
            Console.WriteLine($"Cases: {summary.CaseCount}; expectations matched: {summary.PassedExpectationCount}/{summary.CaseCount}");
            Console.WriteLine($"Outcomes pass/warning/error: {summary.PassCaseCount}/{summary.WarningOnlyCaseCount}/{summary.ErrorCaseCount}");
            Console.WriteLine($"Findings error/warning: {summary.ErrorFindingCount}/{summary.WarningFindingCount}");
            Console.WriteLine($"Skipped source categories: {(skippedCategories.Count == 0 ? "none" : string.Join(", ", skippedCategories))}");
            Console.WriteLine($"Probe success: {summary.Success}");
            Console.WriteLine($"Reports: {outputDir}");
            return summary.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 1;
        }
    }

    private static List<ValidationCase> BuildCases(Dictionary<string, LocaleEntry> entries, out List<string> skippedCategories)
    {
        skippedCategories = [];

        var placeholder = Find(entries, x =>
            x.Text.Contains("{0}", StringComparison.Ordinal) &&
            x.Text.Length is > 8 and < 240 &&
            !HasGender(x.Text) &&
            !x.Text.Contains("<Typing", StringComparison.OrdinalIgnoreCase),
            "simple {0} placeholder case");

        var gender = Find(entries, x =>
            HasGender(x.Text) &&
            GenderRegex.IsMatch(x.Text) &&
            GenderMarkers.All(marker => Count(x.Text, marker, true) == 1) &&
            CanSafelyMutateGender(x.Text) &&
            x.Text.Length < 450,
            "male/female branch case");

        var typingTitle = Find(entries, x =>
            x.Text.Contains("<TypingTitle ", StringComparison.OrdinalIgnoreCase) &&
            HasAttribute(x.Text, "TypingTitle", "titlename") &&
            HasAttribute(x.Text, "TypingTitle", "titlecolor") &&
            HasAttribute(x.Text, "TypingTitle", "contextcolor") &&
            !x.Text.Contains("grey_title\" \"", StringComparison.Ordinal),
            "well-formed TypingTitle case");

        var typingImg = Find(entries, x =>
            x.Text.Contains("<TypingImg ", StringComparison.OrdinalIgnoreCase) &&
            HasAttribute(x.Text, "TypingImg", "id"),
            "TypingImg case");

        var simpleMarkup = Find(entries, x =>
            x.Text.Contains("<NumGreen>", StringComparison.OrdinalIgnoreCase) &&
            x.Text.Contains("</>", StringComparison.Ordinal) &&
            x.Text.Length < 240,
            "NumGreen case");

        var squareControl = Find(entries, x => SquareControlRegex.IsMatch(x.Text) && x.Text.Length < 300, "square control case");

        var humanBracket = Find(entries, x =>
            AnySquareRegex.IsMatch(x.Text) &&
            !SquareControlRegex.IsMatch(x.Text) &&
            x.Text.Length < 240 &&
            !HasGender(x.Text) &&
            !x.Text.Contains("<Typing", StringComparison.OrdinalIgnoreCase),
            "human bracket text case");

        var malformedIdentity = "ST_IntroduceMsg_maintask::MT01_03_NPC015_003";
        if (!entries.TryGetValue(malformedIdentity, out var malformed))
            malformed = Find(entries, x => x.Text.Contains("grey_title\" \"", StringComparison.Ordinal), "malformed TypingTitle source case");

        var cases = new List<ValidationCase>();
        Add(cases, "plain-valid", placeholder, "valid text change preserving source structure", "pass", "PTBR TESTE: " + placeholder.Text);
        Add(cases, "placeholder-valid", placeholder, "preserve {0} while translating text", "pass", "PTBR PLACEHOLDER: " + placeholder.Text);
        Add(cases, "placeholder-missing", placeholder, "remove a source placeholder", "error", RemoveFirst(placeholder.Text, "{0}"));
        Add(cases, "placeholder-extra", placeholder, "introduce a new placeholder", "error", placeholder.Text + " {999}");

        Add(cases, "gender-valid", gender, "translate both gender branches while preserving markers", "pass", MutateGender(gender.Text));
        Add(cases, "gender-broken", gender, "remove one required gender marker", "error", RemoveFirst(gender.Text, "<female>"));

        Add(cases, "typingtitle-localizable-valid", typingTitle, "change TypingTitle.titlename only", "pass", ReplaceAttribute(typingTitle.Text, "TypingTitle", "titlename", "PTBR TITULO"));
        Add(cases, "typingtitle-invariant-broken", typingTitle, "change invariant TypingTitle.titlecolor", "error", ReplaceAttribute(typingTitle.Text, "TypingTitle", "titlecolor", "ptbr_broken_color"));
        Add(cases, "typingtitle-attribute-missing", typingTitle, "remove invariant TypingTitle.contextcolor", "error", RemoveAttribute(typingTitle.Text, "TypingTitle", "contextcolor"));

        Add(cases, "typingimg-valid", typingImg, "translate surrounding text without touching TypingImg technical attributes", "pass", "PTBR IMG: " + typingImg.Text);
        Add(cases, "typingimg-id-broken", typingImg, "change invariant TypingImg.id", "error", ReplaceAttribute(typingImg.Text, "TypingImg", "id", "999999999"));

        Add(cases, "simple-markup-valid", simpleMarkup, "translate text while preserving NumGreen markup", "pass", "PTBR MARKUP: " + simpleMarkup.Text);
        Add(cases, "simple-markup-broken", simpleMarkup, "remove a known simple markup token", "error", RemoveFirst(simpleMarkup.Text, "<NumGreen>"));

        var squareToken = SquareControlRegex.Match(squareControl.Text).Value;
        Add(cases, "square-control-valid", squareControl, "translate text while preserving square control", "pass", "PTBR SQUARE: " + squareControl.Text);
        Add(cases, "square-control-broken", squareControl, "change a square control token", "error", ReplaceFirst(squareControl.Text, squareToken, "[ptbr_broken/]"));

        var humanMatch = AnySquareRegex.Match(humanBracket.Text);
        Add(cases, "human-bracket-localizable", humanBracket, "change ordinary bracketed human text", "pass", ReplaceFirst(humanBracket.Text, humanMatch.Value, "[PTBR TEXTO]"));

        var literalNewline = entries.Values.OrderBy(x => x.Identity, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Text.Contains("\\n", StringComparison.Ordinal) && x.Text.Length < 300);
        if (literalNewline is not null)
        {
            Add(cases, "literal-newline-valid", literalNewline, "preserve literal backslash-n", "pass", "PTBR NEWLINE: " + literalNewline.Text);
            Add(cases, "literal-newline-broken", literalNewline, "remove literal backslash-n", "error", RemoveFirst(literalNewline.Text, "\\n"));
        }
        else
        {
            skippedCategories.Add("literal_backslash_n_absent_in_effective_es_1.3");
        }

        Add(cases, "official-anomaly-preserved", malformed, "preserve an inherited malformed TypingTitle token", "warning_only", malformed.Text + " PTBR");
        Add(cases, "official-anomaly-changed", malformed, "modify an inherited malformed TypingTitle token", "error", malformed.Text.Replace("grey_title", "ptbr_broken_title", StringComparison.Ordinal));
        return cases;
    }

    private static List<ValidationFinding> Validate(string source, string translation)
    {
        var nested = typeof(Program).GetNestedType("StructureValidator", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Program.StructureValidator");
        var method = nested.GetMethod("Validate", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("Program.StructureValidator.Validate");
        return (List<ValidationFinding>)(method.Invoke(null, [source, translation])
            ?? throw new InvalidOperationException("Validator returned null."));
    }

    private static object? InvokeProgram(string methodName, params object?[] args)
    {
        var methods = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(x => x.Name == methodName)
            .ToList();
        if (methods.Count != 1)
            throw new MissingMethodException($"Expected exactly one Program.{methodName} method, found {methods.Count}.");
        return methods[0].Invoke(null, args);
    }

    private static void Add(List<ValidationCase> cases, string id, LocaleEntry entry, string description, string expected, string translation)
        => cases.Add(new ValidationCase(id, entry.Identity, description, expected, entry.Text, translation));

    private static LocaleEntry Find(Dictionary<string, LocaleEntry> entries, Func<LocaleEntry, bool> predicate, string description)
        => entries.Values.OrderBy(x => x.Identity, StringComparer.Ordinal).FirstOrDefault(predicate)
           ?? throw new InvalidOperationException($"Could not find real ES source for {description}.");

    private static bool HasGender(string text) => GenderMarkers.All(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static bool CanSafelyMutateGender(string text)
    {
        var match = GenderRegex.Match(text);
        if (!match.Success) return false;
        static bool Simple(string value) => value.Length is > 0 and < 140 && value.IndexOfAny(['<', '>', '{', '}', '[', ']', '\\']) < 0;
        return Simple(match.Groups["male"].Value) && Simple(match.Groups["female"].Value);
    }

    private static string MutateGender(string source)
    {
        var m = GenderRegex.Match(source);
        return m.Groups["pre"].Value +
               "<male=>PTBR TESTE MASCULINO<male>" +
               m.Groups["mid"].Value +
               "<female=>PTBR TESTE FEMININO<female>" +
               m.Groups["post"].Value;
    }

    private static bool HasAttribute(string source, string tagName, string attributeName)
    {
        var tag = Regex.Match(source, $@"<{Regex.Escape(tagName)}\b[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return tag.Success && Regex.IsMatch(tag.Value, $@"\b{Regex.Escape(attributeName)}\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ReplaceAttribute(string source, string tagName, string attributeName, string newValue)
    {
        var tagMatch = Regex.Match(source, $@"<{Regex.Escape(tagName)}\b[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!tagMatch.Success) throw new InvalidOperationException($"Tag {tagName} not found.");
        var attrPattern = $"(?<prefix>\\b{Regex.Escape(attributeName)}\\s*=\\s*)(?<q>[\"'])(?<value>.*?)(\\k<q>)";
        var replacedTag = Regex.Replace(tagMatch.Value, attrPattern, m => m.Groups["prefix"].Value + m.Groups["q"].Value + newValue + m.Groups["q"].Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (replacedTag == tagMatch.Value) throw new InvalidOperationException($"Attribute {tagName}.{attributeName} not found.");
        return source[..tagMatch.Index] + replacedTag + source[(tagMatch.Index + tagMatch.Length)..];
    }

    private static string RemoveAttribute(string source, string tagName, string attributeName)
    {
        var tagMatch = Regex.Match(source, $@"<{Regex.Escape(tagName)}\b[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!tagMatch.Success) throw new InvalidOperationException($"Tag {tagName} not found.");
        var attrPattern = $"\\s+\\b{Regex.Escape(attributeName)}\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+)";
        var replacedTag = Regex.Replace(tagMatch.Value, attrPattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (replacedTag == tagMatch.Value) throw new InvalidOperationException($"Attribute {tagName}.{attributeName} not found.");
        return source[..tagMatch.Index] + replacedTag + source[(tagMatch.Index + tagMatch.Length)..];
    }

    private static string RemoveFirst(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        if (index < 0) throw new InvalidOperationException($"Value not found for removal: {value}");
        return source.Remove(index, value.Length);
    }

    private static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException($"Value not found for replacement: {oldValue}");
        return source[..index] + newValue + source[(index + oldValue.Length)..];
    }

    private static int Count(string text, string value, bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var count = 0;
        var start = 0;
        while (start <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, start, comparison);
            if (index < 0) break;
            count++;
            start = index + value.Length;
        }
        return count;
    }

    private static string Preview(string text)
    {
        const int max = 400;
        var value = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return value.Length <= max ? value : value[..max] + "…";
    }

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonIndented), new UTF8Encoding(false));
    private static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var value in values) writer.WriteLine(JsonSerializer.Serialize(value, JsonCompact));
    }

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}