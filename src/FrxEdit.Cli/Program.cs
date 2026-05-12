using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var app = new FrxEditApp(Console.Out, Console.Error);
return app.Run(args);

internal sealed class FrxEditApp(TextWriter stdout, TextWriter stderr)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
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
        var layout = FrxBinary.Read(project.FrxPath).Inspect();
        var document = new LayoutDocument(project.FormName, Path.GetFileName(project.FrxPath), layout.Controls);
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
        var layout = frx.Inspect();
        PatchValidator.Validate(patch, layout.Controls);

        frx.Apply(patch);

        var outFrxPath = Path.ChangeExtension(outFrmPath, ".frx");
        Directory.CreateDirectory(Path.GetDirectoryName(outFrmPath)!);
        File.WriteAllBytes(outFrxPath, frx.Bytes);

        var updatedFrm = VbaRenamer.Apply(project.FrmText, patch.Renames);
        updatedFrm = UserFormProject.ReplaceOleObjectBlob(updatedFrm, Path.GetFileName(outFrxPath));
        File.WriteAllText(outFrmPath, updatedFrm, project.Encoding);

        stdout.WriteLine($"Wrote {outFrmPath}");
        stdout.WriteLine($"Wrote {outFrxPath}");
        return 0;
    }

    private int Validate(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var project = UserFormProject.Load(Path.GetFullPath(parsed.Positionals[0]));
        var frx = FrxBinary.Read(project.FrxPath);
        var layout = frx.Inspect();
        stdout.WriteLine($"OK: {Path.GetFileName(project.FrmPath)} references {Path.GetFileName(project.FrxPath)}");
        stdout.WriteLine($"OK: FRX prefix {frx.PrefixLength} bytes, OLE compound starts at 0x{frx.OleOffset:X}");
        stdout.WriteLine($"OK: {layout.Controls.Count} controls detected");
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
}

internal sealed class UserFormProject
{
    private static readonly Regex OleObjectBlobRegex = new(
        "OleObjectBlob\\s*=\\s*\"(?<frx>[^\"]+)\":(?<offset>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private static readonly Regex FormNameRegex = new(
        "Begin\\s+\\{[^}]+\\}\\s+(?<name>\\w+)",
        RegexOptions.Compiled);

    public required string FrmPath { get; init; }
    public required string FrxPath { get; init; }
    public required string FrmText { get; init; }
    public required Encoding Encoding { get; init; }
    public required string FormName { get; init; }

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
        };
    }

    public static string ReplaceOleObjectBlob(string frmText, string frxFileName) =>
        OleObjectBlobRegex.Replace(frmText, match =>
            match.Value.Replace(match.Groups["frx"].Value, frxFileName, StringComparison.Ordinal));

    private static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        return Encoding.Default;
    }
}

internal sealed class FrxBinary
{
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

    public LayoutInspection Inspect()
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

        return new LayoutInspection(controls.Values.OrderBy(c => c.NameOffset).ToList());

        void AddControlMatch(Match match, bool requireKnownType)
        {
            var name = match.Value;
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
            if (placement is null || IsKnownNonControlIdentifier(name) || requireKnownType && InferType(name) == "Unknown")
            {
                return;
            }

            var type = InferType(name);
            if (!requireKnownType && type != "Unknown")
            {
                return;
            }

            if (!requireKnownType && !HasKnownControlHeader(nameOffset))
            {
                return;
            }

            controls[name] = new ControlInfo(
                name,
                type,
                null,
                placement?.Left,
                placement?.Top,
                placement?.Width,
                placement?.Height,
                null,
                nameOffset,
                placement?.LeftOffset,
                placement?.TopOffset,
                placement?.WidthOffset,
                placement?.HeightOffset);
        }
    }

    public void Apply(PatchDocument patch)
    {
        var controls = Inspect().Controls.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
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

        var oldBytes = Encoding.ASCII.GetBytes(control.Name);
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
        WriteOptionalInt(control.LeftOffset, patch.Left, control.Name, "left");
        WriteOptionalInt(control.TopOffset, patch.Top, control.Name, "top");
        WriteOptionalInt(control.WidthOffset, patch.Width, control.Name, "width");
        WriteOptionalInt(control.HeightOffset, patch.Height, control.Name, "height");
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

        for (var skip = 0; skip <= 6 && afterName + skip + 8 <= Bytes.Length; skip++)
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

        int? widthOffset = null;
        int? heightOffset = null;
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
                widthOffset = candidateWidthOffset;
                heightOffset = candidateHeightOffset;
                break;
            }
        }

        return new Placement(
            ReadInt32(leftOffset.Value),
            ReadInt32(topOffset.Value),
            widthOffset is null ? null : ReadInt32(widthOffset.Value),
            heightOffset is null ? null : ReadInt32(heightOffset.Value),
            leftOffset.Value,
            topOffset.Value,
            widthOffset,
            heightOffset);
    }

    private static bool IsPlausiblePosition(int value) => value is >= 0 and <= 40_000;

    private int ReadInt32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(offset, 4));

    private static string InferType(string name)
    {
        foreach (var (prefix, type) in TypePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return "Unknown";
    }

    private static bool IsKnownNonControlIdentifier(string name) =>
        name.Equals("Tahoma", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Forms", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Object", StringComparison.OrdinalIgnoreCase);

    private bool HasKnownControlHeader(int nameOffset)
    {
        if (nameOffset < 2)
        {
            return false;
        }

        var marker = Bytes[nameOffset - 2];
        return Bytes[nameOffset - 1] == 0x00 &&
               marker is 0x0C or 0x10 or 0x11 or 0x15 or 0x17 or 0x1A or 0x1B;
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

internal sealed record LayoutDocument(string FormName, string FrxFile, IReadOnlyList<ControlInfo> Controls);

internal sealed record LayoutInspection(IReadOnlyList<ControlInfo> Controls);

internal sealed record ControlInfo(
    string Name,
    string Type,
    string? Caption,
    int? Left,
    int? Top,
    int? Width,
    int? Height,
    string? Parent,
    int NameOffset,
    int? LeftOffset,
    int? TopOffset,
    int? WidthOffset,
    int? HeightOffset);

internal sealed record Placement(
    int Left,
    int Top,
    int? Width,
    int? Height,
    int LeftOffset,
    int TopOffset,
    int? WidthOffset,
    int? HeightOffset);

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
