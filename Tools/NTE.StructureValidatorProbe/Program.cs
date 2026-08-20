using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTE.StructureValidatorProbe;

internal static class Program
{
    private static readonly Regex BraceRegex = new(@"\{[^{}\r\n]+\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SquareControlRegex = new(@"\[[A-Za-z][A-Za-z0-9_:\-]*/\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnySquareRegex = new(@"\[[^\]\r\n]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SimpleAngleRegex = new(@"<(?:NumGreen|Blue|Orange|red|Yellow|Grey|Green|Italic|Title|lv)>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ParameterizedTagRegex = new(@"<(?:TypingTitle|TypingImg|img|hot)\b[^>\r\n]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AttributeRegex = new(
        "(?<name>[A-Za-z_][A-Za-z0-9_:\\-]*)\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GenderRegex = new(
        @"(?<pre>.*?)<male=>(?<male>.*?)<male>(?<mid>.*?)<female=>(?<female>.*?)<female>(?<post>.*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly string[] GenderMarkers = ["<male=>", "<male>", "<female=>", "<female>"];
    private static readonly string[] InvariantTypingTitleAttributes = ["titlecolor", "contextcolor", "font"];
    private static readonly HashSet<string> LocalizableTypingTitleAttributes = new(StringComparer.OrdinalIgnoreCase) { "titlename" };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                PrintUsage();
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

            ValidateInputs(gameRoot, aesConfig, aesFile);
            Directory.CreateDirectory(outputDir);

            Console.WriteLine("NTE Structure Validator Probe 015");
            Console.WriteLine("Purpose: exercise source-relative structural validation against real ES entries");
            Console.WriteLine("Offline only; no game archive or LOCRES is modified.");
            Console.WriteLine();

            var aesKey = LoadAesKey(aesConfig, aesFile);
            using var aesScope = TemporaryAesCompatibility.Install(gameRoot, aesKey);
            using var reader = CreateReaderWithoutLeakingAes(gameRoot);
            var provider = GetProvider(reader);

            var esPath = FindLocres(provider, "/Content/Localization/Game/es/Game.locres");
            var esFile = Resolve(provider, esPath);
            var sourceDescription = DescribeSource(esFile);
            var entries = ParseLocale(esFile);

            var cases = BuildCases(entries);
            var findingRows = new List<FindingRow>();
            var caseRows = new List<CaseResult>();

            foreach (var testCase in cases)
            {
                var findings = StructureValidator.Validate(testCase.SourceText, testCase.TranslationText);
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

    private static List<ValidationCase> BuildCases(Dictionary<string, LocaleEntry> entries)
    {
        var placeholder = Find(entries, x =>
            x.Text.Contains("{0}", StringComparison.Ordinal) &&
            x.Text.Length is > 8 and < 240 &&
            !HasGender(x.Text) &&
            !x.Text.Contains("<Typing", StringComparison.OrdinalIgnoreCase),
            "simple {0} placeholder case");

        var gender = Find(entries, x =>
            HasGender(x.Text) && GenderRegex.IsMatch(x.Text) && x.Text.Length < 450,
            "male/female branch case");

        var typingTitle = Find(entries, x =>
            x.Text.Contains("<TypingTitle ", StringComparison.OrdinalIgnoreCase) &&
            TryFirstParsedTag(x.Text, "TypingTitle", out var tag) &&
            tag.Attributes.ContainsKey("titlename") &&
            tag.Attributes.ContainsKey("titlecolor") &&
            tag.Attributes.ContainsKey("contextcolor"),
            "well-formed TypingTitle case");

        var typingImg = Find(entries, x =>
            x.Text.Contains("<TypingImg ", StringComparison.OrdinalIgnoreCase) &&
            TryFirstParsedTag(x.Text, "TypingImg", out var tag) &&
            tag.Attributes.ContainsKey("id"),
            "TypingImg case");

        var simpleMarkup = Find(entries, x =>
            x.Text.Contains("<NumGreen>", StringComparison.OrdinalIgnoreCase) &&
            x.Text.Contains("</>", StringComparison.Ordinal) &&
            x.Text.Length < 240,
            "NumGreen case");

        var squareControl = Find(entries, x =>
            SquareControlRegex.IsMatch(x.Text) && x.Text.Length < 300,
            "square control case");

        var humanBracket = Find(entries, x =>
            AnySquareRegex.IsMatch(x.Text) &&
            !SquareControlRegex.IsMatch(x.Text) &&
            x.Text.Length < 240 &&
            BraceRegex.Matches(x.Text).Count == 0 &&
            !HasGender(x.Text) &&
            ParameterizedTagRegex.Matches(x.Text).Count == 0,
            "human bracket text case");

        var literalNewline = Find(entries, x =>
            x.Text.Contains("\\n", StringComparison.Ordinal) && x.Text.Length < 300,
            "literal backslash-n case");

        var malformedIdentity = "ST_IntroduceMsg_maintask::MT01_03_NPC015_003";
        if (!entries.TryGetValue(malformedIdentity, out var malformed))
            malformed = Find(entries, x =>
                x.Text.Contains("<TypingTitle ", StringComparison.OrdinalIgnoreCase) &&
                !TryFirstParsedTag(x.Text, "TypingTitle", out _),
                "malformed TypingTitle source case");

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

        Add(cases, "literal-newline-valid", literalNewline, "preserve literal backslash-n", "pass", "PTBR NEWLINE: " + literalNewline.Text);
        Add(cases, "literal-newline-broken", literalNewline, "remove literal backslash-n", "error", RemoveFirst(literalNewline.Text, "\\n"));

        Add(cases, "official-anomaly-preserved", malformed, "preserve an inherited malformed TypingTitle token", "warning_only", malformed.Text + " PTBR");
        Add(cases, "official-anomaly-changed", malformed, "modify an inherited malformed TypingTitle token", "error", malformed.Text.Replace("grey_title", "ptbr_broken_title", StringComparison.Ordinal));

        return cases;
    }

    private static void Add(List<ValidationCase> cases, string id, LocaleEntry entry, string description, string expected, string translation)
        => cases.Add(new ValidationCase(id, entry.Identity, description, expected, entry.Text, translation));

    private static LocaleEntry Find(Dictionary<string, LocaleEntry> entries, Func<LocaleEntry, bool> predicate, string description)
        => entries.Values.OrderBy(x => x.Identity, StringComparer.Ordinal).FirstOrDefault(predicate)
           ?? throw new InvalidOperationException($"Could not find real ES source for {description}.");

    private static bool HasGender(string text) => GenderMarkers.All(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static string MutateGender(string source)
    {
        var m = GenderRegex.Match(source);
        if (!m.Success) throw new InvalidOperationException("Selected gender source did not match branch grammar.");
        return m.Groups["pre"].Value +
               "<male=>PTBR TESTE MASCULINO<male>" +
               m.Groups["mid"].Value +
               "<female=>PTBR TESTE FEMININO<female>" +
               m.Groups["post"].Value;
    }

    private static string ReplaceAttribute(string source, string tagName, string attributeName, string newValue)
    {
        var tagMatch = Regex.Match(source, $@"<{Regex.Escape(tagName)}\b[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!tagMatch.Success) throw new InvalidOperationException($"Tag {tagName} not found.");
        var attrPattern = $@"(?<prefix>\b{Regex.Escape(attributeName)}\s*=\s*)(?<q>[\"'])(?<value>.*?)(\k<q>)";
        var replacedTag = Regex.Replace(tagMatch.Value, attrPattern, m =>
            m.Groups["prefix"].Value + m.Groups["q"].Value + newValue + m.Groups["q"].Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        if (replacedTag == tagMatch.Value) throw new InvalidOperationException($"Attribute {tagName}.{attributeName} not found.");
        return source[..tagMatch.Index] + replacedTag + source[(tagMatch.Index + tagMatch.Length)..];
    }

    private static string RemoveAttribute(string source, string tagName, string attributeName)
    {
        var tagMatch = Regex.Match(source, $@"<{Regex.Escape(tagName)}\b[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!tagMatch.Success) throw new InvalidOperationException($"Tag {tagName} not found.");
        var attrPattern = $@"\s+\b{Regex.Escape(attributeName)}\s*=\s*(?:\"[^\"]*\"|'[^']*'|[^\s>]+)";
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

    private static bool TryFirstParsedTag(string text, string tagName, out ParsedTag tag)
    {
        foreach (Match m in Regex.Matches(text, $@"<{Regex.Escape(tagName)}\b[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (TryParseParameterizedTag(m.Value, out var parsed, out _) && parsed is not null)
            {
                tag = parsed;
                return true;
            }
        }
        tag = ParsedTag.Empty;
        return false;
    }

    private static bool TryParseParameterizedTag(string token, out ParsedTag? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (token.Length < 3 || token[0] != '<' || token[^1] != '>')
        {
            error = "incomplete token";
            return false;
        }

        var inner = token[1..^1].Trim();
        var nameMatch = Regex.Match(inner, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant);
        if (!nameMatch.Success)
        {
            error = "missing tag name";
            return false;
        }

        var name = nameMatch.Groups["name"].Value;
        var rest = inner[nameMatch.Length..];
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var consumed = new bool[rest.Length];
        foreach (Match m in AttributeRegex.Matches(rest))
        {
            var attrName = m.Groups["name"].Value;
            var value = m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["bare"].Value;
            attrs[attrName] = value;
            for (var i = m.Index; i < m.Index + m.Length && i < consumed.Length; i++) consumed[i] = true;
        }

        for (var i = 0; i < rest.Length; i++)
        {
            if (consumed[i] || char.IsWhiteSpace(rest[i]) || rest[i] == '/') continue;
            error = $"unparsed syntax near '{rest[i..Math.Min(rest.Length, i + 40)]}'";
            return false;
        }

        parsed = new ParsedTag(name, attrs, token);
        return true;
    }

    private static class StructureValidator
    {
        public static List<ValidationFinding> Validate(string source, string translation)
        {
            var findings = new List<ValidationFinding>();

            CompareMultiset(findings, "placeholder_mismatch", "brace placeholder/token", Tokens(BraceRegex, source), Tokens(BraceRegex, translation));
            CompareMultiset(findings, "square_control_mismatch", "square control", Tokens(SquareControlRegex, source), Tokens(SquareControlRegex, translation));
            CompareMultiset(findings, "simple_markup_mismatch", "known simple angle markup", Tokens(SimpleAngleRegex, source), Tokens(SimpleAngleRegex, translation));

            CompareCount(findings, "generic_close_mismatch", "</>", Count(source, "</>"), Count(translation, "</>"));
            CompareCount(findings, "literal_newline_mismatch", "literal \\n", Count(source, "\\n"), Count(translation, "\\n"));
            CompareCount(findings, "internal_marker_mismatch", "<!>", Count(source, "<!>"), Count(translation, "<!>"));

            foreach (var marker in GenderMarkers)
                CompareCount(findings, "gender_marker_mismatch", marker, Count(source, marker, true), Count(translation, marker, true));

            ValidateParameterizedTags(source, translation, findings);
            return findings;
        }

        private static void ValidateParameterizedTags(string source, string translation, List<ValidationFinding> findings)
        {
            var sourceTokens = ParameterizedTagRegex.Matches(source).Select(x => x.Value).ToList();
            var targetTokens = ParameterizedTagRegex.Matches(translation).Select(x => x.Value).ToList();

            var sourceGroups = sourceTokens.GroupBy(TagPrefix, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var targetGroups = targetTokens.GroupBy(TagPrefix, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var tagName in sourceGroups.Keys.Union(targetGroups.Keys, StringComparer.OrdinalIgnoreCase))
            {
                sourceGroups.TryGetValue(tagName, out var sourceList);
                targetGroups.TryGetValue(tagName, out var targetList);
                sourceList ??= [];
                targetList ??= [];

                if (sourceList.Count != targetList.Count)
                {
                    findings.Add(new ValidationFinding("error", "parameterized_tag_count_mismatch", $"Tag <{tagName}> count changed from {sourceList.Count} to {targetList.Count}."));
                    continue;
                }

                for (var i = 0; i < sourceList.Count; i++)
                {
                    var sourceToken = sourceList[i];
                    var targetToken = targetList[i];
                    var sourceOk = TryParseParameterizedTag(sourceToken, out var sourceTag, out var sourceError);
                    var targetOk = TryParseParameterizedTag(targetToken, out var targetTag, out var targetError);

                    if (!sourceOk)
                    {
                        if (string.Equals(sourceToken, targetToken, StringComparison.Ordinal))
                            findings.Add(new ValidationFinding("warning", "inherited_source_markup_anomaly", $"Official source contains malformed recognized <{tagName}> markup that was preserved unchanged: {sourceError}"));
                        else
                            findings.Add(new ValidationFinding("error", "source_markup_anomaly_changed", $"Malformed recognized <{tagName}> source token was changed. Source parse issue: {sourceError}"));
                        continue;
                    }

                    if (!targetOk || targetTag is null || sourceTag is null)
                    {
                        findings.Add(new ValidationFinding("error", "target_markup_parse_error", $"Translated <{tagName}> token is malformed: {targetError}"));
                        continue;
                    }

                    var sourceNames = sourceTag.Attributes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    var targetNames = targetTag.Attributes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (!sourceNames.SequenceEqual(targetNames, StringComparer.OrdinalIgnoreCase))
                    {
                        findings.Add(new ValidationFinding("error", "attribute_shape_mismatch", $"<{tagName}> attribute set changed. Source=[{string.Join(',', sourceNames)}], target=[{string.Join(',', targetNames)}]."));
                        continue;
                    }

                    foreach (var attr in sourceNames)
                    {
                        if (tagName.Equals("TypingTitle", StringComparison.OrdinalIgnoreCase) && LocalizableTypingTitleAttributes.Contains(attr))
                            continue;

                        var sourceValue = sourceTag.Attributes[attr];
                        var targetValue = targetTag.Attributes[attr];
                        if (!string.Equals(sourceValue, targetValue, StringComparison.Ordinal))
                            findings.Add(new ValidationFinding("error", "invariant_attribute_changed", $"Invariant attribute <{tagName}>.{attr} changed from '{sourceValue}' to '{targetValue}'."));
                    }
                }
            }
        }

        private static string TagPrefix(string token)
        {
            var m = Regex.Match(token, @"^<(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
            return m.Success ? m.Groups["name"].Value : "<unknown>";
        }

        private static void CompareMultiset(List<ValidationFinding> findings, string code, string label, IEnumerable<string> source, IEnumerable<string> target)
        {
            var a = source.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var b = target.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            if (a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var count) && count == x.Value)) return;
            findings.Add(new ValidationFinding("error", code, $"{label} multiset changed. Source={Format(a)} Target={Format(b)}"));
        }

        private static string Format(Dictionary<string, int> map) => string.Join(", ", map.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}×{x.Value}"));

        private static void CompareCount(List<ValidationFinding> findings, string code, string label, int sourceCount, int targetCount)
        {
            if (sourceCount == targetCount) return;
            findings.Add(new ValidationFinding("error", code, $"{label} count changed from {sourceCount} to {targetCount}."));
        }
    }

    private static IEnumerable<string> Tokens(Regex regex, string text) => regex.Matches(text).Select(x => x.Value);

    private static int Count(string text, string value, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(value)) return 0;
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

    private static Dictionary<string, LocaleEntry> ParseLocale(GameFile file)
    {
        using var ar = file.CreateReader();
        var resource = new FTextLocalizationResource(ar);
        var result = new Dictionary<string, LocaleEntry>(StringComparer.Ordinal);
        foreach (var (nsKey, values) in resource.Entries)
        {
            foreach (var (textKey, entry) in values)
            {
                var identity = Identity(nsKey.Str, textKey.Str);
                result[identity] = new LocaleEntry(identity, nsKey.Str, textKey.Str, entry.LocalizedString ?? string.Empty);
            }
        }
        return result;
    }

    private static string FindLocres(DefaultFileProvider provider, string suffix) => provider.Files.Keys
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(x => NormalizePath(x).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException($"LOCRES not found for suffix: {suffix}");

    private static GameFile Resolve(DefaultFileProvider provider, string path)
    {
        if (!provider.Files.TryGetValue(path, out var file) || file is null)
            throw new InvalidOperationException($"Provider could not resolve effective file: {path}");
        return file;
    }

    private static SourceDescription DescribeSource(GameFile file)
    {
        if (file is VfsEntry entry)
            return new SourceDescription(entry.Vfs.Name, entry.Vfs.ReadOrder);
        return new SourceDescription("<non-vfs>", 0);
    }

    private static DefaultFileProvider GetProvider(UnrealArchiveReader reader)
    {
        var field = typeof(UnrealArchiveReader).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(UnrealArchiveReader).FullName, "_provider");
        return field.GetValue(reader) as DefaultFileProvider
            ?? throw new InvalidOperationException("UnrealArchiveReader._provider is not a DefaultFileProvider.");
    }

    private static UnrealArchiveReader CreateReaderWithoutLeakingAes(string gameRoot)
    {
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(new AesRedactingTextWriter(originalOut));
            return new UnrealArchiveReader(gameRoot);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string LoadAesKey(string? aesConfig, string? aesFile)
    {
        if (aesConfig is null && aesFile is null) return string.Empty;
        string raw;
        if (aesFile is not null)
            raw = File.ReadAllText(aesFile).Trim();
        else
        {
            using var document = JsonDocument.Parse(File.ReadAllText(aesConfig!));
            if (!document.RootElement.TryGetProperty("aes_key", out var aesProperty))
                throw new InvalidDataException("AES config does not contain an 'aes_key' property.");
            raw = aesProperty.GetString()?.Trim() ?? string.Empty;
        }
        if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = "0x" + raw;
        if (raw.Length != 66 || raw.Skip(2).Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("AES key must contain exactly 64 hexadecimal digits.");
        return raw;
    }

    private static void ValidateInputs(string gameRoot, string? aesConfig, string? aesFile)
    {
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException($"Game root not found: {gameRoot}");
        if (aesConfig is not null && aesFile is not null) throw new ArgumentException("Use only one of --aes-config or --aes-file.");
        if (aesConfig is not null && !File.Exists(aesConfig)) throw new FileNotFoundException("AES config not found.", aesConfig);
        if (aesFile is not null && !File.Exists(aesFile)) throw new FileNotFoundException("AES file not found.", aesFile);
    }

    private static string Identity(string ns, string key) => string.IsNullOrEmpty(ns) ? key : $"{ns}::{key}";
    private static string NormalizePath(string path) => path.Replace('\\', '/');
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

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NTE.StructureValidatorProbe <gameRoot> <outputDir> [--aes-config=<path> | --aes-file=<path>]");
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

internal sealed record LocaleEntry(string Identity, string Namespace, string Key, string Text);
internal sealed record SourceDescription(string ContainerName, long ReadOrder);
internal sealed record ValidationCase(string Id, string Identity, string Description, string ExpectedOutcome, string SourceText, string TranslationText);
internal sealed record ValidationFinding(string Severity, string Code, string Message);
internal sealed record ParsedTag(string Name, Dictionary<string, string> Attributes, string RawToken)
{
    public static ParsedTag Empty { get; } = new(string.Empty, new Dictionary<string, string>(), string.Empty);
}

internal sealed class CaseResult
{
    public required string Id { get; init; }
    public required string Identity { get; init; }
    public required string Description { get; init; }
    public required string ExpectedOutcome { get; init; }
    public required string ActualOutcome { get; init; }
    public bool MatchedExpectation { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public required string SourcePreview { get; init; }
    public required string TranslationPreview { get; init; }
}

internal sealed class FindingRow
{
    public required string CaseId { get; init; }
    public required string Identity { get; init; }
    public required string Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

internal sealed class ProbeSummary
{
    public required string EsVirtualPath { get; init; }
    public required string EsContainerName { get; init; }
    public long EsReadOrder { get; init; }
    public int EsEntryCount { get; init; }
    public int CaseCount { get; init; }
    public int PassedExpectationCount { get; init; }
    public int FailedExpectationCount { get; init; }
    public int PassCaseCount { get; init; }
    public int WarningOnlyCaseCount { get; init; }
    public int ErrorCaseCount { get; init; }
    public int FindingCount { get; init; }
    public int ErrorFindingCount { get; init; }
    public int WarningFindingCount { get; init; }
    public bool Success { get; init; }
}

internal sealed class TemporaryAesCompatibility : IDisposable
{
    private readonly string? _path;
    private readonly bool _hadExisting;
    private readonly byte[]? _previous;

    private TemporaryAesCompatibility(string? path, bool hadExisting, byte[]? previous)
    {
        _path = path;
        _hadExisting = hadExisting;
        _previous = previous;
    }

    public static TemporaryAesCompatibility Install(string gameRoot, string aesKey)
    {
        if (string.IsNullOrEmpty(aesKey)) return new TemporaryAesCompatibility(null, false, null);
        var path = Path.Combine(gameRoot, "aes.txt");
        var hadExisting = File.Exists(path);
        var previous = hadExisting ? File.ReadAllBytes(path) : null;
        File.WriteAllText(path, aesKey, Encoding.ASCII);
        return new TemporaryAesCompatibility(path, hadExisting, previous);
    }

    public void Dispose()
    {
        if (_path is null) return;
        if (_hadExisting && _previous is not null) File.WriteAllBytes(_path, _previous);
        else if (File.Exists(_path)) File.Delete(_path);
    }
}

internal sealed class AesRedactingTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    public AesRedactingTextWriter(TextWriter inner) => _inner = inner;
    public override Encoding Encoding => _inner.Encoding;
    public override void Write(char value) => _inner.Write(value);
    public override void Write(string? value) => _inner.Write(value);
    public override void WriteLine(string? value)
    {
        if (value is not null && value.StartsWith("AES key loaded:", StringComparison.OrdinalIgnoreCase))
            _inner.WriteLine("AES key loaded [REDACTED]");
        else
            _inner.WriteLine(value);
    }
}
