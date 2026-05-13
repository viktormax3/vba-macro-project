using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var app = new FrxEditApp(Console.Out, Console.Error);
return app.Run(args);

internal sealed class FrxEditApp(TextWriter stdout, TextWriter stderr)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "inspect" => Inspect(args[1..]),
                "apply" => Apply(args[1..]),
                "validate" => Validate(args[1..]),
                "dump-records" => DumpRecords(args[1..]),
                "dump-storage" => DumpStorage(args[1..]),
                "dump-stream-records" => DumpStreamRecords(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (CliException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: unexpected failure: {ex.Message}");
            return 1;
        }
    }

    private int Inspect(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var project = UserFormProject.Load(frmPath);
        var layout = FrxBinary.Read(project.FrxPath).Inspect(project.KnownControlNames, project.ControlScopes);
        var document = new LayoutDocument(project.FormName, Path.GetFileName(project.FrxPath), project.FormProperties, layout.Controls);
        WriteJson(parsed.GetOption("out"), document);
        return 0;
    }

    private int Apply(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 2, maxPositionals: 2);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var patchPath = Path.GetFullPath(parsed.Positionals[1]);
        var outFrmPath = Path.GetFullPath(parsed.RequireOption("out"));

        var patch = JsonSerializer.Deserialize<PatchDocument>(File.ReadAllText(patchPath), JsonOptions)
            ?? throw new CliException("Patch file is empty.");
        if (patch.Add is { Count: > 0 })
        {
            throw new CliException("The 'add' section is reserved for a future version. This build supports renames and layout only.");
        }

        var project = UserFormProject.Load(frmPath);
        var frx = FrxBinary.Read(project.FrxPath);
        var layout = frx.Inspect(project.KnownControlNames, project.ControlScopes);
        PatchValidator.Validate(patch, layout.Controls);

        frx.Apply(patch, project.KnownControlNames, project.ControlScopes);

        var outFrxPath = Path.ChangeExtension(outFrmPath, ".frx");
        Directory.CreateDirectory(Path.GetDirectoryName(outFrmPath)!);
        File.WriteAllBytes(outFrxPath, frx.Bytes);

        var updatedFrm = VbaRenamer.Apply(project.FrmText, patch.Renames);
        updatedFrm = UserFormProject.ReplaceOleObjectBlob(updatedFrm, Path.GetFileName(outFrxPath));
        File.WriteAllText(outFrmPath, updatedFrm, project.Encoding);
        UserFormProject.WriteScopesCopy(outFrmPath, project.ControlScopes, patch.Renames);

        stdout.WriteLine($"Wrote {outFrmPath}");
        stdout.WriteLine($"Wrote {outFrxPath}");
        return 0;
    }

    private int Validate(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var project = UserFormProject.Load(Path.GetFullPath(parsed.Positionals[0]));
        var frx = FrxBinary.Read(project.FrxPath);
        var layout = frx.Inspect(project.KnownControlNames, project.ControlScopes);
        stdout.WriteLine($"OK: {Path.GetFileName(project.FrmPath)} references {Path.GetFileName(project.FrxPath)}");
        stdout.WriteLine($"OK: FRX prefix {frx.PrefixLength} bytes, OLE compound starts at 0x{frx.OleOffset:X}");
        stdout.WriteLine($"OK: {layout.Controls.Count} controls detected");
        return 0;
    }

    private int DumpRecords(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var project = UserFormProject.Load(frmPath);
        var frx = FrxBinary.Read(project.FrxPath);
        var around = parsed.GetOption("around");
        var before = parsed.GetIntOption("before", 4);
        var after = parsed.GetIntOption("after", 8);
        var records = frx.DumpRecords(around, before, after, project.KnownControlNames, project.ControlScopes);
        WriteJson(parsed.GetOption("out"), records);
        return 0;
    }

    private int DumpStorage(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var project = UserFormProject.Load(frmPath);
        var frx = FrxBinary.Read(project.FrxPath);
        WriteJson(parsed.GetOption("out"), CompoundStorageInspector.Inspect(frx.Bytes, frx.OleOffset));
        return 0;
    }

    private int DumpStreamRecords(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var project = UserFormProject.Load(frmPath);
        var frx = FrxBinary.Read(project.FrxPath);
        var storage = CompoundStorageInspector.Inspect(frx.Bytes, frx.OleOffset);
        WriteJson(parsed.GetOption("out"), StreamRecordInspector.Inspect(storage.Streams));
        return 0;
    }

    private void WriteJson(string? outPath, object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (string.IsNullOrWhiteSpace(outPath))
        {
            stdout.WriteLine(json);
            return;
        }

        File.WriteAllText(Path.GetFullPath(outPath), json, Encoding.UTF8);
        stdout.WriteLine($"Wrote {Path.GetFullPath(outPath)}");
    }

    private int Fail(string message)
    {
        stderr.WriteLine($"error: {message}");
        PrintHelp();
        return 2;
    }

    private void PrintHelp()
    {
        stdout.WriteLine("frxedit inspect <UserForm.frm> [--out layout.json]");
        stdout.WriteLine("frxedit apply <UserForm.frm> <patch.json> --out <UserForm.patched.frm>");
        stdout.WriteLine("frxedit validate <UserForm.frm>");
        stdout.WriteLine("frxedit dump-records <UserForm.frm> [--around TextBox3] [--before 4] [--after 8] [--out records.json]");
        stdout.WriteLine("frxedit dump-storage <UserForm.frm> [--out storage.json]");
        stdout.WriteLine("frxedit dump-stream-records <UserForm.frm> [--out stream-records.json]");
    }
}

internal sealed class CommandLine
{
    public required List<string> Positionals { get; init; }
    public required Dictionary<string, string> Options { get; init; }

    public static CommandLine Parse(string[] args, int minPositionals, int maxPositionals)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var key = arg[2..];
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new CliException("Invalid empty option.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliException($"Option '--{key}' requires a value.");
            }

            options[key] = args[++i];
        }

        if (positionals.Count < minPositionals || positionals.Count > maxPositionals)
        {
            throw new CliException($"Expected {minPositionals} positional argument(s), got {positionals.Count}.");
        }

        return new CommandLine { Positionals = positionals, Options = options };
    }

    public string? GetOption(string name) => Options.TryGetValue(name, out var value) ? value : null;

    public string RequireOption(string name) =>
        GetOption(name) ?? throw new CliException($"Missing required option '--{name}'.");

    public int GetIntOption(string name, int defaultValue)
    {
        var value = GetOption(name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new CliException($"Option '--{name}' must be a non-negative integer.");
        }

        return parsed;
    }
}

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
        "(CommandButton|TextBox|CheckBox|Frame|Label|ComboBox|SpinButton|OptionButton|Image)\\d+|CustButton\\d+",
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

internal sealed class FrxBinary
{
    private const double FrxUnitsPerPoint = 35.25;
    private const int RecordBlockGapThreshold = 512;
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly Regex ControlNameRegex = new(
        "(CommandButton|TextBox|CheckBox|Frame|Label|ComboBox|SpinButton|OptionButton|Image)\\d+|CustButton\\d+",
        RegexOptions.Compiled);
    private static readonly Regex IdentifierRegex = new(
        "[A-Za-z_][A-Za-z0-9_]{2,31}",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> TypePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CommandButton"] = "CommandButton",
        ["TextBox"] = "TextBox",
        ["CheckBox"] = "CheckBox",
        ["Frame"] = "Frame",
        ["Label"] = "Label",
        ["ComboBox"] = "ComboBox",
        ["SpinButton"] = "SpinButton",
        ["OptionButton"] = "OptionButton",
        ["Image"] = "Image",
        ["CustButton"] = "Custom",
    };

    public required byte[] Bytes { get; init; }
    public required int OleOffset { get; init; }
    public int PrefixLength => OleOffset;

    public static FrxBinary Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var oleOffset = FindBytes(bytes, OleSignature);
        if (oleOffset < 0)
        {
            throw new CliException("FRX does not contain an OLE compound signature.");
        }

        return new FrxBinary { Bytes = bytes, OleOffset = oleOffset };
    }

    public LayoutInspection Inspect(
        IReadOnlySet<string>? knownControlNames = null,
        IReadOnlyDictionary<string, string>? controlScopes = null)
    {
        var structured = InspectStructuredStorage(knownControlNames, controlScopes);
        if (structured.Count > 0)
        {
            var orderedStructured = structured.OrderBy(c => c.NameOffset).ToList();
            var annotatedStructured = AnnotateRecordOrder(orderedStructured);
            return new LayoutInspection(AddContainerDimensions(annotatedStructured));
        }

        return InspectByNameScan(knownControlNames, controlScopes);
    }

    private LayoutInspection InspectByNameScan(
        IReadOnlySet<string>? knownControlNames = null,
        IReadOnlyDictionary<string, string>? controlScopes = null)
    {
        var ascii = Encoding.Latin1.GetString(Bytes);
        var controls = new Dictionary<string, ControlInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ControlNameRegex.Matches(ascii))
        {
            AddControlMatch(match, requireKnownType: true);
        }

        foreach (Match match in IdentifierRegex.Matches(ascii))
        {
            AddControlMatch(match, requireKnownType: false);
        }

        var ordered = controls.Values.OrderBy(c => c.NameOffset).ToList();
        var annotated = AnnotateRecordOrder(ordered);
        return new LayoutInspection(AddContainerDimensions(annotated));

        void AddControlMatch(Match match, bool requireKnownType)
        {
            var binaryName = match.Value;
            var name = CanonicalizeName(binaryName, knownControlNames);
            if (controls.ContainsKey(name))
            {
                return;
            }

            var nameOffset = match.Index;
            if (!requireKnownType && controls.Values.Any(c => c.NameOffset == nameOffset))
            {
                return;
            }

            var placement = TryReadPlacement(nameOffset, name.Length);
            if (placement is null || IsKnownNonControlIdentifier(name) || requireKnownType && InferType(name, nameOffset) == "Unknown")
            {
                return;
            }

            var type = InferType(name, nameOffset);
            if (!requireKnownType && !HasKnownControlHeader(nameOffset))
            {
                return;
            }

            controls[name] = new ControlInfo(
                name,
                type,
                placement?.Left,
                placement?.Top,
                placement?.RawWidth,
                placement?.RawHeight,
                ToPoints(placement?.Left),
                ToPoints(placement?.Top),
                null,
                null,
                ReadKnownProperties(type, nameOffset),
                controlScopes?.TryGetValue(name, out var scope) == true ? scope : null,
                binaryName.Equals(name, StringComparison.OrdinalIgnoreCase) ? null : binaryName,
                null,
                null,
                null,
                nameOffset,
                placement?.LeftOffset,
                placement?.TopOffset,
                placement?.WidthOffset,
                placement?.HeightOffset);
        }
    }

    private IReadOnlyList<ControlInfo> InspectStructuredStorage(
        IReadOnlySet<string>? knownControlNames,
        IReadOnlyDictionary<string, string>? controlScopes)
    {
        CompoundStorageDump storage;
        try
        {
            storage = CompoundStorageInspector.Inspect(Bytes, OleOffset);
        }
        catch (CliException)
        {
            return [];
        }

        var controls = new Dictionary<int, ControlInfo>();
        foreach (var stream in storage.Streams.Where(s => s.Kind == "Stream" && s.Name == "f"))
        {
            if (stream.FileOffsets.Length != stream.Data.Length)
            {
                continue;
            }

            foreach (var control in ReadStructuredControlsFromFStream(stream, knownControlNames, controlScopes))
            {
                controls.TryAdd(control.NameOffset, control);
            }
        }

        return controls.Values.ToList();
    }

    private IReadOnlyList<ControlInfo> ReadStructuredControlsFromFStream(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames,
        IReadOnlyDictionary<string, string>? controlScopes)
    {
        var controls = new List<ControlInfo>();
        var data = stream.Data;
        for (var textOffset = 4; textOffset < data.Length; textOffset++)
        {
            if (!IsPrintableAscii(data[textOffset]))
            {
                continue;
            }

            if (textOffset > 0 && IsIdentifierPart(data[textOffset - 1]))
            {
                continue;
            }

            var marker = FindStructuredMarkerBefore(data, textOffset);
            if (marker is null || !TryGetMsFormsType(marker.TypeCode, out var type))
            {
                continue;
            }

            var rawName = ReadNullTerminatedAscii(data, textOffset, maxLength: 64);
            if (rawName is null || IsKnownNonControlIdentifier(rawName))
            {
                continue;
            }

            var name = CanonicalizeName(rawName, knownControlNames);
            var placement = TryReadStreamPlacement(data, stream.FileOffsets, textOffset + rawName.Length);
            if (placement is null)
            {
                continue;
            }

            var nameOffset = stream.FileOffsets[textOffset];
            var markerOffset = stream.FileOffsets[marker.Offset];
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["streamName"] = stream.Name,
                ["streamIndex"] = stream.Index,
                ["streamLocalNameOffset"] = textOffset,
                ["recordMarkerHex"] = ReadHex(markerOffset, 4),
                ["recordMarkerOffset"] = markerOffset,
                ["streamLocalRecordMarkerOffset"] = marker.Offset,
                ["tabIndex"] = marker.TabIndex,
                ["recordTypeCode"] = $"0x{marker.TypeCode:X2}",
                ["parser"] = "structuredStorageFStream"
            };

            controls.Add(new ControlInfo(
                name,
                type,
                placement.Left,
                placement.Top,
                placement.RawWidth,
                placement.RawHeight,
                ToPoints(placement.Left),
                ToPoints(placement.Top),
                null,
                null,
                properties,
                controlScopes?.TryGetValue(name, out var scope) == true ? scope : null,
                rawName.Equals(name, StringComparison.OrdinalIgnoreCase) ? null : rawName,
                null,
                null,
                null,
                nameOffset,
                placement.LeftOffset,
                placement.TopOffset,
                placement.WidthOffset,
                placement.HeightOffset));
        }

        return controls;
    }

    private static ControlTypeMarker? FindStructuredMarkerBefore(byte[] data, int textOffset)
    {
        var candidates = new List<(ControlTypeMarker Marker, int Gap)>();
        var start = Math.Max(0, textOffset - 16);
        for (var offset = textOffset - 4; offset >= start; offset--)
        {
            var tabIndex = data[offset];
            var typeCode = data[offset + 2];
            if (data[offset + 1] == 0 &&
                data[offset + 3] == 0 &&
                tabIndex <= 0x7F &&
                TryGetMsFormsType(typeCode, out _))
            {
                candidates.Add((new ControlTypeMarker(offset, tabIndex, typeCode), textOffset - offset - 4));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Gap % 4 == 0 ? 0 : 1)
            .ThenBy(candidate => candidate.Gap)
            .ThenBy(candidate => candidate.Marker.TabIndex)
            .Select(candidate => candidate.Marker)
            .FirstOrDefault();
    }

    private Placement? TryReadStreamPlacement(byte[] data, int[] fileOffsets, int afterNameOffset)
    {
        var searchStart = Math.Min(data.Length, afterNameOffset);
        while (searchStart < data.Length && data[searchStart] == 0)
        {
            searchStart++;
        }

        var searchEnd = Math.Min(data.Length - 8, searchStart + 56);
        for (var offset = searchStart; offset <= searchEnd; offset += 2)
        {
            var left = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            var top = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (!IsPlausiblePosition(left) || !IsPlausiblePosition(top))
            {
                continue;
            }

            return new Placement(
                left,
                top,
                null,
                null,
                fileOffsets[offset],
                fileOffsets[offset + 4],
                null,
                null);
        }

        return null;
    }

    private static IReadOnlyList<ControlInfo> AnnotateRecordOrder(IReadOnlyList<ControlInfo> controls)
    {
        var annotated = new List<ControlInfo>(controls.Count);
        var block = 0;
        ControlInfo? previous = null;

        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            int? delta = previous is null ? null : control.NameOffset - previous.NameOffset;
            if (delta is > RecordBlockGapThreshold)
            {
                block++;
            }

            annotated.Add(control with
            {
                RecordIndex = index,
                RecordDelta = delta,
                RecordBlock = block,
            });

            previous = control;
        }

        return annotated;
    }

    private IReadOnlyList<ControlInfo> AddContainerDimensions(IReadOnlyList<ControlInfo> controls)
    {
        var result = controls.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            var control = result[i];
            if (!control.Type.Equals("Frame", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var firstChild = controls
                .Where(candidate => candidate.Parent?.Equals(control.Name, StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(candidate => candidate.NameOffset)
                .FirstOrDefault();
            if (firstChild is null)
            {
                continue;
            }

            var dimensions = TryReadContainerDimensionsBefore(firstChild.NameOffset);
            if (dimensions is null)
            {
                continue;
            }

            result[i] = control with
            {
                RawWidth = dimensions.Width,
                RawHeight = dimensions.Height,
                WidthPt = ToPoints(dimensions.Width),
                HeightPt = ToPoints(dimensions.Height),
                WidthOffset = dimensions.WidthOffset,
                HeightOffset = dimensions.HeightOffset,
                Properties = WithProperty(control.Properties, "sizeSource", "containerPropertyBag")
            };
        }

        return result;
    }

    public IReadOnlyList<RecordDump> DumpRecords(
        string? around,
        int before,
        int after,
        IReadOnlySet<string>? knownControlNames = null,
        IReadOnlyDictionary<string, string>? controlScopes = null)
    {
        var controls = Inspect(knownControlNames, controlScopes).Controls.ToList();
        var start = 0;
        var end = controls.Count - 1;

        if (!string.IsNullOrWhiteSpace(around))
        {
            var index = controls.FindIndex(c => c.Name.Equals(around, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new CliException($"Control '{around}' was not found.");
            }

            start = Math.Max(0, index - before);
            end = Math.Min(controls.Count - 1, index + after);
        }

        var result = new List<RecordDump>();
        for (var i = start; i <= end; i++)
        {
            var control = controls[i];
            var previous = i > 0 ? controls[i - 1] : null;
            result.Add(new RecordDump(
                i,
                control.Name,
                control.Type,
                control.RecordBlock,
                control.NameOffset,
                control.RecordDelta,
                control.Left,
                control.Top,
                control.LeftPt,
                control.TopPt,
                control.RawWidth,
                control.RawHeight,
                control.Properties,
                ReadHex(Math.Max(0, control.NameOffset - 24), 24),
                ReadHex(control.NameOffset, Math.Min(control.Name.Length + 16, Bytes.Length - control.NameOffset))));
        }

        return result;
    }

    public void Apply(
        PatchDocument patch,
        IReadOnlySet<string>? knownControlNames = null,
        IReadOnlyDictionary<string, string>? controlScopes = null)
    {
        var controls = Inspect(knownControlNames, controlScopes).Controls.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var nameMap = patch.Renames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reverseNameMap = nameMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (patchName, requested) in patch.Layout ?? new Dictionary<string, LayoutPatch>(StringComparer.OrdinalIgnoreCase))
        {
            var sourceName = reverseNameMap.TryGetValue(patchName, out var oldName) ? oldName : patchName;
            if (!controls.TryGetValue(sourceName, out var control))
            {
                throw new CliException($"Layout target '{patchName}' was not found.");
            }

            WriteLayout(control, requested);
        }

        foreach (var (oldName, newName) in nameMap)
        {
            RenameInFrx(controls[oldName], newName);
        }
    }

    private void RenameInFrx(ControlInfo control, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || !Regex.IsMatch(newName, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new CliException($"Invalid control name '{newName}'.");
        }

        var oldBytes = Encoding.ASCII.GetBytes(control.BinaryName ?? control.Name);
        var newBytes = Encoding.ASCII.GetBytes(newName);
        if (newBytes.Length > oldBytes.Length)
        {
            throw new CliException($"FRX rename '{control.Name}' -> '{newName}' is longer than the original. Use a name up to {oldBytes.Length} bytes for this binary-safe v1.");
        }

        if (!Bytes.AsSpan(control.NameOffset, oldBytes.Length).SequenceEqual(oldBytes))
        {
            throw new CliException($"FRX name offset for '{control.Name}' no longer matches the inspected bytes.");
        }

        newBytes.CopyTo(Bytes.AsSpan(control.NameOffset, newBytes.Length));
        Bytes.AsSpan(control.NameOffset + newBytes.Length, oldBytes.Length - newBytes.Length).Clear();
    }

    private void WriteLayout(ControlInfo control, LayoutPatch patch)
    {
        if (patch.Width is not null || patch.Height is not null)
        {
            throw new CliException("Patch fields 'width' and 'height' are not writable yet because FRX size encoding is not fully mapped. Use 'rawWidth'/'rawHeight' only for low-level experiments.");
        }

        WriteOptionalInt(control.LeftOffset, patch.Left, control.Name, "left");
        WriteOptionalInt(control.TopOffset, patch.Top, control.Name, "top");
        WriteOptionalInt(control.WidthOffset, patch.RawWidth, control.Name, "rawWidth");
        WriteOptionalInt(control.HeightOffset, patch.RawHeight, control.Name, "rawHeight");
    }

    private void WriteOptionalInt(int? offset, int? value, string name, string property)
    {
        if (value is null)
        {
            return;
        }

        if (offset is null)
        {
            throw new CliException($"Control '{name}' does not expose a writable '{property}' offset.");
        }

        if (value < 0 || value > 100_000)
        {
            throw new CliException($"Control '{name}' has invalid '{property}' value {value}.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset.Value, 4), value.Value);
    }

    private Placement? TryReadPlacement(int nameOffset, int nameLength)
    {
        var afterName = nameOffset + nameLength;
        int? leftOffset = null;
        int? topOffset = null;

        for (var skip = 0; skip <= 16 && afterName + skip + 8 <= Bytes.Length; skip++)
        {
            var left = ReadInt32(afterName + skip);
            var top = ReadInt32(afterName + skip + 4);
            if (IsPlausiblePosition(left) && IsPlausiblePosition(top))
            {
                leftOffset = afterName + skip;
                topOffset = afterName + skip + 4;
                break;
            }
        }

        if (leftOffset is null || topOffset is null)
        {
            return null;
        }

        int? rawWidthOffset = null;
        int? rawHeightOffset = null;
        for (var back = 8; back <= 28; back += 4)
        {
            var candidateWidthOffset = nameOffset - back;
            var candidateHeightOffset = nameOffset - back + 4;
            if (candidateWidthOffset < 0)
            {
                continue;
            }

            var width = ReadInt32(candidateWidthOffset);
            var height = ReadInt32(candidateHeightOffset);
            if (width is > 0 and <= 20_000 && height is > 0 and <= 20_000)
            {
                rawWidthOffset = candidateWidthOffset;
                rawHeightOffset = candidateHeightOffset;
                break;
            }
        }

        return new Placement(
            ReadInt32(leftOffset.Value),
            ReadInt32(topOffset.Value),
            rawWidthOffset is null ? null : ReadInt32(rawWidthOffset.Value),
            rawHeightOffset is null ? null : ReadInt32(rawHeightOffset.Value),
            leftOffset.Value,
            topOffset.Value,
            rawWidthOffset,
            rawHeightOffset);
    }

    private ContainerDimensions? TryReadContainerDimensionsBefore(int firstChildNameOffset)
    {
        var marker = FindControlTypeMarker(firstChildNameOffset);
        if (marker is null)
        {
            return null;
        }

        var start = Math.Max(4, marker.Offset - 160);
        var end = Math.Max(start, marker.Offset - 24);
        for (var offset = end; offset >= start; offset--)
        {
            if (offset + 16 > Bytes.Length || ReadInt32(offset - 4) != 0x00007D00)
            {
                continue;
            }

            var width = ReadInt32(offset);
            var height = ReadInt32(offset + 4);
            if (width is <= 0 or > 40_000 || height is <= 0 or > 40_000)
            {
                continue;
            }

            if (ReadInt32(offset + 8) != 0 || ReadInt32(offset + 12) != 0)
            {
                continue;
            }

            return new ContainerDimensions(width, height, offset, offset + 4);
        }

        return null;
    }

    private static Dictionary<string, object?> WithProperty(
        Dictionary<string, object?>? properties,
        string name,
        object? value)
    {
        var result = properties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(properties, StringComparer.OrdinalIgnoreCase);
        result[name] = value;
        return result;
    }

    private Dictionary<string, object?>? ReadKnownProperties(string type, int nameOffset)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var marker = FindControlTypeMarker(nameOffset);
        if (marker is not null)
        {
            properties["recordMarkerHex"] = ReadHex(marker.Offset, 4);
            properties["recordMarkerOffset"] = marker.Offset;
            properties["tabIndex"] = marker.TabIndex;
            properties["recordTypeCode"] = $"0x{marker.TypeCode:X2}";
            if (marker.Offset + 4 < nameOffset)
            {
                properties["recordTailHex"] = ReadHex(marker.Offset + 4, nameOffset - marker.Offset - 4);
            }
        }

        var backColor = FindColorNear(nameOffset, searchBefore: 160, searchAfter: 24, likelyBgr: [0x23, 0x23, 0x23]);
        if (backColor is not null)
        {
            properties["backColor"] = backColor;
        }

        var foreColor = FindColorNear(nameOffset, searchBefore: 160, searchAfter: 24, likelyBgr: [0xCC, 0xCC, 0xCC]);
        if (foreColor is not null)
        {
            properties["foreColor"] = foreColor;
        }

        return properties.Count == 0 ? null : properties;
    }

    private string? FindColorNear(int nameOffset, int searchBefore, int searchAfter, byte[] likelyBgr)
    {
        var start = Math.Max(0, nameOffset - searchBefore);
        var end = Math.Min(Bytes.Length - likelyBgr.Length, nameOffset + searchAfter);
        for (var i = start; i <= end; i++)
        {
            if (!Bytes.AsSpan(i, likelyBgr.Length).SequenceEqual(likelyBgr))
            {
                continue;
            }

            return $"&H80{likelyBgr[2]:X2}{likelyBgr[1]:X2}{likelyBgr[0]:X2}&";
        }

        return null;
    }

    private static double? ToPoints(int? value) =>
        value is null ? null : Math.Round(value.Value / FrxUnitsPerPoint, 2);

    private static bool IsPlausiblePosition(int value) => value is >= 0 and <= 40_000;

    private int ReadInt32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(offset, 4));

    private string ReadHex(int offset, int count) =>
        Convert.ToHexString(Bytes.AsSpan(offset, count));

    private string InferType(string name, int nameOffset)
    {
        foreach (var (prefix, type) in TypePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return InferTypeFromRecordMarker(nameOffset);
    }

    private string InferTypeFromRecordMarker(int nameOffset)
    {
        var marker = FindControlTypeMarker(nameOffset);
        return marker is not null && TryGetMsFormsType(marker.TypeCode, out var type) ? type : "Unknown";
    }

    private static string CanonicalizeName(string binaryName, IReadOnlySet<string>? knownControlNames)
    {
        if (knownControlNames is null || knownControlNames.Contains(binaryName))
        {
            return binaryName;
        }

        for (var length = binaryName.Length - 1; length >= 3; length--)
        {
            var candidate = binaryName[..length];
            if (knownControlNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return binaryName;
    }

    private static bool IsKnownNonControlIdentifier(string name) =>
        name.Equals("Tahoma", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Forms", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Object", StringComparison.OrdinalIgnoreCase) ||
        TypePrefixes.Keys.Any(prefix => name.Equals(prefix, StringComparison.OrdinalIgnoreCase));

    private static string? ReadNullTerminatedAscii(byte[] data, int offset, int maxLength)
    {
        if (offset >= data.Length || !IsIdentifierStart(data[offset]))
        {
            return null;
        }

        var end = offset;
        var limit = Math.Min(data.Length, offset + maxLength);
        while (end < limit && data[end] != 0)
        {
            if (!IsIdentifierPart(data[end]))
            {
                return end - offset < 3 ? null : Encoding.Latin1.GetString(data, offset, end - offset);
            }

            end++;
        }

        if (end - offset < 3)
        {
            return null;
        }

        return Encoding.Latin1.GetString(data, offset, end - offset);
    }

    private static bool IsIdentifierStart(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ||
        value is >= (byte)'a' and <= (byte)'z' ||
        value == (byte)'_';

    private static bool IsIdentifierPart(byte value) =>
        IsIdentifierStart(value) ||
        value is >= (byte)'0' and <= (byte)'9';

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

    private bool HasKnownControlHeader(int nameOffset)
        => FindControlTypeMarker(nameOffset) is not null;

    private ControlTypeMarker? FindControlTypeMarker(int nameOffset)
    {
        for (var offset = nameOffset - 4; offset >= Math.Max(0, nameOffset - 16); offset -= 4)
        {
            var tabIndex = Bytes[offset];
            var typeCode = Bytes[offset + 2];
            if (Bytes[offset + 1] == 0x00 &&
                Bytes[offset + 3] == 0x00 &&
                TryGetMsFormsType(typeCode, out _) &&
                tabIndex <= 0x7F)
            {
                return new ControlTypeMarker(offset, tabIndex, typeCode);
            }
        }

        return null;
    }

    private static bool TryGetMsFormsType(byte typeCode, out string type)
    {
        type = typeCode switch
        {
            0x0C => "Image",
            0x0E => "Frame",
            0x10 => "SpinButton",
            0x11 => "CommandButton",
            0x12 => "TabStrip",
            0x15 => "Label",
            0x17 => "TextBox",
            0x18 => "ListBox",
            0x19 => "ComboBox",
            0x1A => "CheckBox",
            0x1B => "OptionButton",
            0x1C => "ToggleButton",
            0x2F => "ScrollBar",
            0x39 => "MultiPage",
            _ => string.Empty,
        };

        return type.Length > 0;
    }

    private static int FindBytes(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}

internal static class PatchValidator
{
    public static void Validate(PatchDocument patch, IReadOnlyList<ControlInfo> controls)
    {
        var known = controls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalNames = controls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (oldName, newName) in patch.Renames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Contains(oldName))
            {
                throw new CliException($"Rename source '{oldName}' does not exist.");
            }

            finalNames.Remove(oldName);
            if (!finalNames.Add(newName))
            {
                throw new CliException($"Rename target '{newName}' would duplicate an existing control.");
            }
        }

        foreach (var name in patch.Layout?.Keys ?? Enumerable.Empty<string>())
        {
            if (known.Contains(name))
            {
                continue;
            }

            if (patch.Renames?.Values.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            throw new CliException($"Layout target '{name}' does not exist.");
        }
    }
}

internal static class VbaRenamer
{
    public static string Apply(string source, Dictionary<string, string>? renames)
    {
        if (renames is null || renames.Count == 0)
        {
            return source;
        }

        var result = source;
        foreach (var (oldName, newName) in renames)
        {
            result = Regex.Replace(result, $@"(?<![A-Za-z0-9_]){Regex.Escape(oldName)}(?![A-Za-z0-9])", newName);
        }

        return result;
    }
}

internal sealed record LayoutDocument(
    string FormName,
    string FrxFile,
    Dictionary<string, object?> FormProperties,
    IReadOnlyList<ControlInfo> Controls);

internal sealed record LayoutInspection(IReadOnlyList<ControlInfo> Controls);

internal sealed record RecordDump(
    int Index,
    string Name,
    string Type,
    int? RecordBlock,
    int NameOffset,
    int? DeltaFromPrevious,
    int? Left,
    int? Top,
    double? LeftPt,
    double? TopPt,
    int? RawWidth,
    int? RawHeight,
    Dictionary<string, object?>? Properties,
    string HeaderHex,
    string NameAndPositionHex);

internal sealed record ControlInfo(
    string Name,
    string Type,
    int? Left,
    int? Top,
    int? RawWidth,
    int? RawHeight,
    double? LeftPt,
    double? TopPt,
    double? WidthPt,
    double? HeightPt,
    Dictionary<string, object?>? Properties,
    string? Parent,
    string? BinaryName,
    int? RecordIndex,
    int? RecordDelta,
    int? RecordBlock,
    int NameOffset,
    int? LeftOffset,
    int? TopOffset,
    int? WidthOffset,
    int? HeightOffset);

internal sealed record Placement(
    int Left,
    int Top,
    int? RawWidth,
    int? RawHeight,
    int LeftOffset,
    int TopOffset,
    int? WidthOffset,
    int? HeightOffset);

internal sealed record ContainerDimensions(int Width, int Height, int WidthOffset, int HeightOffset);

internal sealed record ControlTypeMarker(int Offset, byte TabIndex, byte TypeCode);

internal static class CompoundStorageInspector
{
    private const int HeaderSize = 512;
    private const int EndOfChain = unchecked((int)0xFFFFFFFE);
    private const int FreeSector = unchecked((int)0xFFFFFFFF);
    private const int FatSector = unchecked((int)0xFFFFFFFD);
    private const int DifatSector = unchecked((int)0xFFFFFFFC);

    public static CompoundStorageDump Inspect(byte[] bytes, int oleOffset)
    {
        if (bytes.Length < oleOffset + HeaderSize)
        {
            throw new CliException("OLE compound header is incomplete.");
        }

        var header = bytes.AsSpan(oleOffset, HeaderSize);
        var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(header[0x1E..]);
        var miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(header[0x20..]);
        var firstDirectorySector = ReadInt32(header, 0x30);
        var miniStreamCutoff = ReadInt32(header, 0x38);
        var firstMiniFatSector = ReadInt32(header, 0x3C);
        var miniFatSectorCount = ReadInt32(header, 0x40);
        var fatSectorIds = ReadDifat(header);
        var fat = ReadFat(bytes, oleOffset, sectorSize, fatSectorIds);
        var directoryBytes = ReadRegularStream(bytes, oleOffset, sectorSize, fat, firstDirectorySector);
        var entries = ReadDirectory(directoryBytes);
        var root = entries.FirstOrDefault(e => e.Type == 5);
        var rootRead = root is null
            ? new StreamRead([], [])
            : TrimToSize(ReadRegularStreamWithOffsets(bytes, oleOffset, sectorSize, fat, root.StartSector), root.Size);
        var rootStream = rootRead.Data;
        var miniFat = ReadMiniFat(bytes, oleOffset, sectorSize, fat, firstMiniFatSector, miniFatSectorCount);

        var streamIndex = 0;
        var streams = entries
            .Where(e => e.Type is 2 or 5)
            .Select(e =>
            {
                var index = streamIndex++;
                var read = e.Type == 2
                    ? ReadStreamData(bytes, oleOffset, sectorSize, miniSectorSize, fat, miniFat, rootRead, e, miniStreamCutoff)
                    : rootRead;
                var data = read.Data;
                var sample = Convert.ToHexString(data.AsSpan(0, Math.Min(32, data.Length)));
                return new StorageEntryDump(
                    index,
                    e.Name,
                    e.Type == 5 ? "Root" : "Stream",
                    e.StartSector,
                    e.Size,
                    e.Type == 2 && e.Size < (ulong)miniStreamCutoff,
                    sample,
                    data.Length <= 512 ? Convert.ToHexString(data) : null,
                    DetectResourceKind(sample),
                    ScanResourceHits(data),
                    data,
                    read.FileOffsets);
            })
            .OrderBy(e => e.Index)
            .ToList();

        return new CompoundStorageDump(sectorSize, miniSectorSize, miniStreamCutoff, fatSectorIds.Count, streams);
    }

    private static List<int> ReadDifat(ReadOnlySpan<byte> header)
    {
        var sectors = new List<int>();
        for (var offset = 0x4C; offset < 0x200; offset += 4)
        {
            var sector = ReadInt32(header, offset);
            if (sector != FreeSector && sector != EndOfChain && sector != FatSector && sector != DifatSector)
            {
                sectors.Add(sector);
            }
        }

        return sectors;
    }

    private static int[] ReadFat(byte[] bytes, int oleOffset, int sectorSize, IReadOnlyList<int> fatSectorIds)
    {
        var entries = new List<int>();
        foreach (var sectorId in fatSectorIds)
        {
            var sector = ReadSector(bytes, oleOffset, sectorSize, sectorId);
            for (var i = 0; i < sector.Length; i += 4)
            {
                entries.Add(BinaryPrimitives.ReadInt32LittleEndian(sector[i..(i + 4)]));
            }
        }

        return entries.ToArray();
    }

    private static int[] ReadMiniFat(byte[] bytes, int oleOffset, int sectorSize, int[] fat, int firstSector, int sectorCount)
    {
        if (firstSector < 0 || sectorCount <= 0)
        {
            return [];
        }

        var data = ReadRegularStream(bytes, oleOffset, sectorSize, fat, firstSector);
        var entries = new int[data.Length / 4];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4, 4));
        }

        return entries;
    }

    private static byte[] ReadRegularStream(byte[] bytes, int oleOffset, int sectorSize, int[] fat, int firstSector)
    {
        if (firstSector < 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        foreach (var sectorId in FollowChain(fat, firstSector))
        {
            output.Write(ReadSector(bytes, oleOffset, sectorSize, sectorId));
        }

        return output.ToArray();
    }

    private static StreamRead ReadRegularStreamWithOffsets(byte[] bytes, int oleOffset, int sectorSize, int[] fat, int firstSector)
    {
        if (firstSector < 0)
        {
            return new StreamRead([], []);
        }

        using var output = new MemoryStream();
        var offsets = new List<int>();
        foreach (var sectorId in FollowChain(fat, firstSector))
        {
            var sectorOffset = GetSectorOffset(bytes, oleOffset, sectorSize, sectorId);
            if (sectorOffset < 0)
            {
                continue;
            }

            output.Write(bytes.AsSpan(sectorOffset, sectorSize));
            for (var i = 0; i < sectorSize; i++)
            {
                offsets.Add(sectorOffset + i);
            }
        }

        return new StreamRead(output.ToArray(), offsets.ToArray());
    }

    private static byte[] ReadMiniStream(byte[] rootStream, int miniSectorSize, int[] miniFat, int firstMiniSector, ulong size)
    {
        if (firstMiniSector < 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        foreach (var sectorId in FollowChain(miniFat, firstMiniSector))
        {
            var offset = sectorId * miniSectorSize;
            if (offset < 0 || offset >= rootStream.Length)
            {
                break;
            }

            output.Write(rootStream.AsSpan(offset, Math.Min(miniSectorSize, rootStream.Length - offset)));
            if ((ulong)output.Length >= size)
            {
                break;
            }
        }

        var data = output.ToArray();
        return data.Length > (int)size ? data[..(int)size] : data;
    }

    private static StreamRead ReadMiniStream(StreamRead rootStream, int miniSectorSize, int[] miniFat, int firstMiniSector, ulong size)
    {
        if (firstMiniSector < 0)
        {
            return new StreamRead([], []);
        }

        using var output = new MemoryStream();
        var offsets = new List<int>();
        foreach (var sectorId in FollowChain(miniFat, firstMiniSector))
        {
            var offset = sectorId * miniSectorSize;
            if (offset < 0 || offset >= rootStream.Data.Length)
            {
                break;
            }

            var count = Math.Min(miniSectorSize, rootStream.Data.Length - offset);
            output.Write(rootStream.Data.AsSpan(offset, count));
            offsets.AddRange(rootStream.FileOffsets.Skip(offset).Take(count));
            if ((ulong)output.Length >= size)
            {
                break;
            }
        }

        return TrimToSize(new StreamRead(output.ToArray(), offsets.ToArray()), size);
    }

    private static IEnumerable<int> FollowChain(int[] fat, int firstSector)
    {
        var seen = new HashSet<int>();
        var current = firstSector;
        while (current >= 0 && current < fat.Length && seen.Add(current))
        {
            yield return current;
            current = fat[current];
            if (current == EndOfChain || current == FreeSector)
            {
                yield break;
            }
        }
    }

    private static byte[] ReadSector(byte[] bytes, int oleOffset, int sectorSize, int sectorId)
    {
        var offset = GetSectorOffset(bytes, oleOffset, sectorSize, sectorId);
        if (offset < 0)
        {
            return [];
        }

        return bytes.AsSpan(offset, sectorSize).ToArray();
    }

    private static int GetSectorOffset(byte[] bytes, int oleOffset, int sectorSize, int sectorId)
    {
        var offset = oleOffset + HeaderSize + sectorId * sectorSize;
        return sectorId < 0 || offset < 0 || offset + sectorSize > bytes.Length ? -1 : offset;
    }

    private static List<StorageDirectoryEntry> ReadDirectory(byte[] directoryBytes)
    {
        var entries = new List<StorageDirectoryEntry>();
        for (var offset = 0; offset + 128 <= directoryBytes.Length; offset += 128)
        {
            var entry = directoryBytes.AsSpan(offset, 128);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x40..]);
            var type = entry[0x42];
            if (type == 0 || nameLength < 2)
            {
                continue;
            }

            var name = Encoding.Unicode.GetString(entry[..Math.Min(nameLength - 2, 64)]).TrimEnd('\0');
            var startSector = ReadInt32(entry, 0x74);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x78..]);
            entries.Add(new StorageDirectoryEntry(name, type, startSector, size));
        }

        return entries;
    }

    private static StreamRead ReadStreamData(
        byte[] bytes,
        int oleOffset,
        int sectorSize,
        int miniSectorSize,
        int[] fat,
        int[] miniFat,
        StreamRead rootStream,
        StorageDirectoryEntry entry,
        int miniStreamCutoff)
    =>
        entry.Size < (ulong)miniStreamCutoff
            ? ReadMiniStream(rootStream, miniSectorSize, miniFat, entry.StartSector, entry.Size)
            : TrimToSize(ReadRegularStreamWithOffsets(bytes, oleOffset, sectorSize, fat, entry.StartSector), entry.Size);

    private static byte[] TrimToSize(byte[] data, ulong size)
    {
        var targetSize = (int)Math.Min((ulong)data.Length, size);
        return data.Length == targetSize ? data : data[..targetSize];
    }

    private static StreamRead TrimToSize(StreamRead read, ulong size)
    {
        var targetSize = (int)Math.Min((ulong)read.Data.Length, size);
        return read.Data.Length == targetSize
            ? read
            : new StreamRead(read.Data[..targetSize], read.FileOffsets[..targetSize]);
    }

    private static string? DetectResourceKind(string sampleHex)
    {
        if (sampleHex.StartsWith("00000100", StringComparison.OrdinalIgnoreCase)) return "ico";
        if (sampleHex.StartsWith("28000000", StringComparison.OrdinalIgnoreCase)) return "dib";
        if (sampleHex.StartsWith("424D", StringComparison.OrdinalIgnoreCase)) return "bmp";
        if (sampleHex.StartsWith("89504E47", StringComparison.OrdinalIgnoreCase)) return "png";
        if (sampleHex.Contains("4D6963726F736F667420466F726D73", StringComparison.OrdinalIgnoreCase)) return "forms";
        return null;
    }

    private static IReadOnlyList<ResourceHit> ScanResourceHits(byte[] data)
    {
        var patterns = new (string Kind, byte[] Pattern)[]
        {
            ("ico", [0x00, 0x00, 0x01, 0x00]),
            ("dib", [0x28, 0x00, 0x00, 0x00]),
            ("bmp", [0x42, 0x4D]),
            ("png", [0x89, 0x50, 0x4E, 0x47]),
        };

        var hits = new List<ResourceHit>();
        foreach (var (kind, pattern) in patterns)
        {
            for (var i = 0; i <= data.Length - pattern.Length; i++)
            {
                if (!data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                {
                    continue;
                }

                if (kind == "ico" && !LooksLikeIconDirectory(data, i))
                {
                    continue;
                }

                if (kind == "dib" && !LooksLikeDibHeader(data, i))
                {
                    continue;
                }

                var resourceLength = TryGetResourceLength(data, i, kind);
                var contextStart = Math.Max(0, i - 32);
                var contextLength = Math.Min(64, data.Length - contextStart);
                hits.Add(new ResourceHit(
                    kind,
                    i,
                    Convert.ToHexString(data.AsSpan(i, Math.Min(32, data.Length - i))),
                    Convert.ToHexString(data.AsSpan(contextStart, contextLength)),
                    resourceLength,
                    TryDetectFrxImageHeader(data, i),
                    TryDetectMsFormsPictureHeader(data, i, resourceLength)));
            }
        }

        return hits.OrderBy(h => h.Offset).Take(32).ToList();
    }

    private static int? TryGetResourceLength(byte[] data, int offset, string kind)
    {
        if (kind == "ico" && offset + 6 <= data.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
            if (count is <= 0 or > 20 || offset + 6 + count * 16 > data.Length)
            {
                return null;
            }

            var end = 0;
            for (var i = 0; i < count; i++)
            {
                var entryOffset = offset + 6 + i * 16;
                var bytesInResource = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entryOffset + 8, 4));
                var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entryOffset + 12, 4));
                if (bytesInResource > int.MaxValue || imageOffset > int.MaxValue)
                {
                    return null;
                }

                end = Math.Max(end, (int)(imageOffset + bytesInResource));
            }

            return end > 0 && offset + end <= data.Length ? end : null;
        }

        return null;
    }

    private static FrxImageHeaderGuess? TryDetectFrxImageHeader(byte[] data, int contentOffset)
    {
        var longHeader = TryReadImageHeader(data, contentOffset, 24);
        if (longHeader is not null)
        {
            return longHeader;
        }

        return TryReadImageHeader(data, contentOffset, 8);
    }

    private static FrxImageHeaderGuess? TryReadImageHeader(byte[] data, int contentOffset, int headerLength)
    {
        var payloadOffset = contentOffset - headerLength;
        var recordOffset = payloadOffset - 4;
        if (recordOffset < 0 || payloadOffset < 0)
        {
            return null;
        }

        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(recordOffset, 4));
        var contentLengthOffset = payloadOffset + (headerLength == 24 ? 20 : 4);
        if (contentLengthOffset < payloadOffset || contentLengthOffset + 4 > data.Length)
        {
            return null;
        }

        var contentLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(contentLengthOffset, 4));
        if (declaredLength <= 0 || contentLength <= 0 || declaredLength != contentLength + headerLength)
        {
            return null;
        }

        if (contentOffset + contentLength > data.Length)
        {
            return null;
        }

        return new FrxImageHeaderGuess(recordOffset, payloadOffset, headerLength, declaredLength, contentLength);
    }

    private static MsFormsPictureHeaderGuess? TryDetectMsFormsPictureHeader(byte[] data, int contentOffset, int? resourceLength)
    {
        if (resourceLength is null || contentOffset < 32)
        {
            return null;
        }

        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(contentOffset - 4, 4));
        if (declaredLength != resourceLength)
        {
            return null;
        }

        var clsidOffset = contentOffset - 24;
        var clsidHex = Convert.ToHexString(data.AsSpan(clsidOffset, 16));
        if (!clsidHex.Equals("0452E30B918FCE119DE300AA004BB851", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new MsFormsPictureHeaderGuess(contentOffset - 32, clsidOffset, declaredLength, clsidHex);
    }

    private static bool LooksLikeIconDirectory(byte[] data, int offset)
    {
        if (offset + 22 > data.Length)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
        if (count is <= 0 or > 20)
        {
            return false;
        }

        var width = data[offset + 6];
        var height = data[offset + 7];
        var reserved = data[offset + 9];
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 10, 2));
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 12, 2));
        var bytesInResource = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 14, 4));
        var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 18, 4));

        return width <= 128 &&
               height <= 128 &&
               reserved == 0 &&
               planes is 0 or 1 &&
               bitCount is 0 or 1 or 4 or 8 or 24 or 32 &&
               bytesInResource > 0 &&
               imageOffset >= 6 + count * 16 &&
               offset + imageOffset < data.Length;
    }

    private static bool LooksLikeDibHeader(byte[] data, int offset)
    {
        if (offset + 16 > data.Length)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8, 4));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 12, 2));
        return width is > 0 and <= 4096 && height is > 0 and <= 8192 && planes == 1;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..(offset + 4)]);
}

internal sealed record CompoundStorageDump(
    int SectorSize,
    int MiniSectorSize,
    int MiniStreamCutoff,
    int FatSectorCount,
    IReadOnlyList<StorageEntryDump> Streams);

internal sealed record StorageEntryDump(
    int Index,
    string Name,
    string Kind,
    int StartSector,
    ulong Size,
    bool IsMiniStream,
    string SampleHex,
    string? DataHex,
    string? ResourceKind,
    IReadOnlyList<ResourceHit> ResourceHits,
    [property: JsonIgnore] byte[] Data,
    [property: JsonIgnore] int[] FileOffsets);

internal sealed record StreamRead(byte[] Data, int[] FileOffsets);

internal static class StreamRecordInspector
{
    private static readonly HashSet<byte> KnownTypeCodes = new([0x0C, 0x0E, 0x10, 0x11, 0x12, 0x15, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x2F, 0x39]);

    public static IReadOnlyList<StreamRecordDump> Inspect(IReadOnlyList<StorageEntryDump> streams)
    {
        return streams
            .Where(s => s.Kind == "Stream" && s.Name is "f" or "o")
            .Select(s => new StreamRecordDump(
                s.Index,
                s.Name,
                s.Size,
                s.Name == "f" ? ScanStructuralRecords(s.Data) : [],
                ScanAsciiRuns(s.Data)))
            .ToList();
    }

    private static IReadOnlyList<StructuralRecordCandidate> ScanStructuralRecords(byte[] data)
    {
        var records = new List<StructuralRecordCandidate>();

        for (var offset = 0; offset <= data.Length - 4; offset++)
        {
            var tabIndex = data[offset];
            var typeCode = data[offset + 2];
            if (data[offset + 1] != 0 || data[offset + 3] != 0 || tabIndex > 127 || !KnownTypeCodes.Contains(typeCode))
            {
                continue;
            }

            var name = FindFirstAsciiRun(data, offset + 4, 80);
            var intsAfterName = name is null
                ? []
                : ReadFollowingInt32Values(data, name.Offset + name.Length, 4);

            records.Add(new StructuralRecordCandidate(
                offset,
                tabIndex,
                typeCode,
                GuessTypeName(typeCode),
                name?.Offset,
                name?.Text,
                intsAfterName,
                Convert.ToHexString(data.AsSpan(Math.Max(0, offset - 8), Math.Min(48, data.Length - Math.Max(0, offset - 8))))));
        }

        return records;
    }

    private static IReadOnlyList<AsciiRunCandidate> ScanAsciiRuns(byte[] data)
    {
        var runs = new List<AsciiRunCandidate>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (!IsPrintableAscii(data[offset]))
            {
                offset++;
                continue;
            }

            var start = offset;
            while (offset < data.Length && IsPrintableAscii(data[offset]))
            {
                offset++;
            }

            var length = offset - start;
            if (length >= 3)
            {
                runs.Add(new AsciiRunCandidate(
                    start,
                    Encoding.Latin1.GetString(data, start, length),
                    length,
                    ReadFollowingInt32Values(data, offset, 4),
                    FindPlausibleInt32Pairs(data, offset, 48),
                    Convert.ToHexString(data.AsSpan(Math.Max(0, start - 8), Math.Min(48, data.Length - Math.Max(0, start - 8))))));
            }
        }

        return runs;
    }

    private static AsciiRunCandidate? FindFirstAsciiRun(byte[] data, int start, int maxDistance)
    {
        var limit = Math.Min(data.Length, start + maxDistance);
        for (var offset = start; offset < limit; offset++)
        {
            if (!IsPrintableAscii(data[offset]))
            {
                continue;
            }

            var runStart = offset;
            while (offset < limit && IsPrintableAscii(data[offset]))
            {
                offset++;
            }

            var length = offset - runStart;
            if (length >= 3)
            {
                return new AsciiRunCandidate(
                    runStart,
                    Encoding.Latin1.GetString(data, runStart, length),
                    length,
                    ReadFollowingInt32Values(data, offset, 4),
                    FindPlausibleInt32Pairs(data, offset, 48),
                    Convert.ToHexString(data.AsSpan(Math.Max(0, runStart - 8), Math.Min(48, data.Length - Math.Max(0, runStart - 8)))));
            }
        }

        return null;
    }

    private static IReadOnlyList<int> ReadFollowingInt32Values(byte[] data, int offset, int count)
    {
        var values = new List<int>();
        while (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }

        for (var i = 0; i < count && offset + 4 <= data.Length; i++, offset += 4)
        {
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)));
        }

        return values;
    }

    private static IReadOnlyList<Int32PairCandidate> FindPlausibleInt32Pairs(byte[] data, int offset, int maxDistance)
    {
        var pairs = new List<Int32PairCandidate>();
        var limit = Math.Min(data.Length - 8, offset + maxDistance);
        for (var cursor = offset; cursor <= limit; cursor += 2)
        {
            var first = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            var second = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4, 4));
            if (IsPlausibleTwipValue(first) && IsPlausibleTwipValue(second))
            {
                pairs.Add(new Int32PairCandidate(cursor, first, second));
            }
        }

        return pairs;
    }

    private static bool IsPlausibleTwipValue(int value) => value is >= 0 and <= 100000;

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

    private static string GuessTypeName(byte typeCode) => typeCode switch
    {
        0x0C => "Image",
        0x0E => "Frame",
        0x10 => "SpinButton",
        0x11 => "CommandButton",
        0x12 => "TabStrip",
        0x15 => "Label",
        0x17 => "TextBox",
        0x18 => "ListBox",
        0x19 => "ComboBox",
        0x1A => "CheckBox",
        0x1B => "OptionButton",
        0x1C => "ToggleButton",
        0x2F => "ScrollBar",
        0x39 => "MultiPage",
        _ => $"0x{typeCode:X2}",
    };
}

internal sealed record StreamRecordDump(
    int StreamIndex,
    string StreamName,
    ulong Size,
    IReadOnlyList<StructuralRecordCandidate> StructuralRecords,
    IReadOnlyList<AsciiRunCandidate> TextRuns);

internal sealed record StructuralRecordCandidate(
    int MarkerOffset,
    byte TabIndex,
    byte TypeCode,
    string TypeGuess,
    int? FirstTextOffset,
    string? FirstText,
    IReadOnlyList<int> Int32AfterFirstText,
    string ContextHex);

internal sealed record AsciiRunCandidate(
    int Offset,
    string Text,
    int Length,
    IReadOnlyList<int> FollowingInt32,
    IReadOnlyList<Int32PairCandidate> PlausibleInt32Pairs,
    string ContextHex);

internal sealed record Int32PairCandidate(
    int Offset,
    int First,
    int Second);

internal sealed record ResourceHit(
    string Kind,
    int Offset,
    string SampleHex,
    string ContextHex,
    int? ResourceLength,
    FrxImageHeaderGuess? FrxImageHeader,
    MsFormsPictureHeaderGuess? MsFormsPictureHeader);

internal sealed record FrxImageHeaderGuess(
    int RecordOffset,
    int PayloadOffset,
    int HeaderLength,
    int DeclaredLength,
    int ContentLength);

internal sealed record MsFormsPictureHeaderGuess(
    int HeaderOffset,
    int ClsidOffset,
    int DeclaredLength,
    string ClsidHex);

internal sealed record StorageDirectoryEntry(string Name, byte Type, int StartSector, ulong Size);

internal sealed class PatchDocument
{
    public Dictionary<string, string>? Renames { get; set; }
    public Dictionary<string, LayoutPatch>? Layout { get; set; }
    public List<AddControlPatch>? Add { get; set; }
}

internal sealed class LayoutPatch
{
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RawWidth { get; set; }
    public int? RawHeight { get; set; }
}

internal sealed class AddControlPatch
{
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string? FromTemplate { get; set; }
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Caption { get; set; }
}

internal sealed class CliException(string message) : Exception(message);
