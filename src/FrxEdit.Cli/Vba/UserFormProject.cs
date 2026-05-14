internal sealed class UserFormProject
{
    private static readonly Regex OleObjectBlobRegex = new(
        "OleObjectBlob\\s*=\\s*\"(?<frx>[^\"]+)\":(?<offset>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private static readonly Regex FormNameRegex = new(
        "Begin\\s+\\{[^}]+\\}\\s+(?<name>\\w+)",
        RegexOptions.Compiled);
    private static readonly Regex FormPropertyRegex = new(
        "^\\s*(?<name>[A-Za-z][A-Za-z0-9_]*)\\s*=\\s*(?<value>.*?)\\s*(?:'.*)?$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CodeControlNameRegex = new(
        "(CommandButton|TextBox|CheckBox|Frame|Label|ComboBox|SpinButton|OptionButton|Image|ToggleButton|ScrollBar|TabStrip|MultiPage|ListBox)\\d+|CustButton\\d+",
        RegexOptions.Compiled);
    private static readonly Regex EventProcedureNameRegex = new(
        "^\\s*Private\\s+Sub\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)_[A-Za-z][A-Za-z0-9_]*\\b",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex MemberControlNameRegex = new(
        "\\b(?:Me|UserForm\\d*)\\.(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b|\\bControls\\(\"(?<quoted>[A-Za-z_][A-Za-z0-9_]*)\"\\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public required string FrmPath { get; init; }
    public required string FrxPath { get; init; }
    public required string FrmText { get; init; }
    public required Encoding Encoding { get; init; }
    public required string FormName { get; init; }
    public required Dictionary<string, object?> FormProperties { get; init; }
    public required HashSet<string> KnownControlNames { get; init; }
    public required Dictionary<string, string> ControlScopes { get; init; }

    public static UserFormProject Load(string frmPath)
    {
        if (!File.Exists(frmPath))
        {
            throw new CliException($"FRM file not found: {frmPath}");
        }

        var encoding = DetectEncoding(frmPath);
        var text = File.ReadAllText(frmPath, encoding);
        var oleMatch = OleObjectBlobRegex.Match(text);
        if (!oleMatch.Success)
        {
            throw new CliException("Could not find OleObjectBlob reference in FRM.");
        }

        var frxRelative = oleMatch.Groups["frx"].Value;
        var frxPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(frmPath)!, frxRelative));
        if (!File.Exists(frxPath))
        {
            throw new CliException($"Referenced FRX file not found: {frxPath}");
        }

        var formName = FormNameRegex.Match(text).Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(formName))
        {
            formName = Path.GetFileNameWithoutExtension(frmPath);
        }

        return new UserFormProject
        {
            FrmPath = frmPath,
            FrxPath = frxPath,
            FrmText = text,
            Encoding = encoding,
            FormName = formName,
            FormProperties = ParseFormProperties(text),
            KnownControlNames = ExtractKnownControlNames(text, formName),
            ControlScopes = LoadScopes(frmPath),
        };
    }

    public static string ReplaceOleObjectBlob(string frmText, string frxFileName) =>
        OleObjectBlobRegex.Replace(frmText, match =>
            match.Value.Replace(match.Groups["frx"].Value, frxFileName, StringComparison.Ordinal));

    public static void WriteScopesCopy(
        string outFrmPath,
        IReadOnlyDictionary<string, string> controlScopes,
        IReadOnlyDictionary<string, string>? renames)
    {
        if (controlScopes.Count == 0)
        {
            return;
        }

        var grouped = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, scope) in controlScopes)
        {
            var finalName = renames is not null && renames.TryGetValue(name, out var renamed) ? renamed : name;
            if (!grouped.TryGetValue(scope, out var names))
            {
                names = [];
                grouped[scope] = names;
            }

            names.Add(finalName);
        }

        foreach (var names in grouped.Values)
        {
            names.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var scopesPath = Path.ChangeExtension(outFrmPath, ".scopes.json");
        File.WriteAllText(scopesPath, JsonSerializer.Serialize(grouped, FrxEditApp.JsonOptions), Encoding.UTF8);
    }

    private static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        return Encoding.Default;
    }

    private static Dictionary<string, object?> ParseFormProperties(string frmText)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var endMatch = Regex.Match(frmText, "^End\\s*$", RegexOptions.Multiline);
        var designText = endMatch.Success ? frmText[..endMatch.Index] : frmText;
        foreach (Match match in FormPropertyRegex.Matches(designText))
        {
            var name = match.Groups["name"].Value;
            if (name.StartsWith("Attribute", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            properties[name] = ParseFrmValue(match.Groups["value"].Value);
        }

        return properties;
    }

    private static object? ParseFrmValue(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        if (value.Contains('"', StringComparison.Ordinal))
        {
            return value;
        }

        if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static HashSet<string> ExtractKnownControlNames(string frmText, string formName)
    {
        var names = CodeControlNameRegex.Matches(frmText)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in EventProcedureNameRegex.Matches(frmText))
        {
            names.Add(match.Groups["name"].Value);
        }

        foreach (Match match in MemberControlNameRegex.Matches(frmText))
        {
            var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["quoted"].Value;
            names.Add(name);
        }

        names.Add(formName);
        return names;
    }

    private static Dictionary<string, string> LoadScopes(string frmPath)
    {
        var scopesPath = Path.ChangeExtension(frmPath, ".scopes.json");
        if (!File.Exists(scopesPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var scopes = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(scopesPath))
            ?? throw new CliException($"Scope file is empty: {scopesPath}");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scope, names) in scopes)
        {
            foreach (var name in names)
            {
                result[name] = scope;
            }
        }

        return result;
    }
}
