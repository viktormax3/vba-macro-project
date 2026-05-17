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
            if (Environment.GetEnvironmentVariable("FRXEDIT_DEBUG") == "1")
            {
                stderr.WriteLine(ex);
                return 1;
            }

            stderr.WriteLine($"error: unexpected failure: {ex.Message}");
            return 1;
        }
    }

    private int Inspect(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var project = UserFormProject.Load(frmPath);
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Tolerant);
        var layout = FrxBinary.Read(project.FrxPath).Inspect(project.KnownControlNames, project.ControlScopes, parserMode);
        var rawDocument = new LayoutDocument(
            project.FormName,
            Path.GetFileName(project.FrxPath),
            project.FormProperties,
            layout.Controls,
            layout.FrxFormControl,
            layout.ParserValidation);
        var humanDocument = HumanLayoutDocument.FromRaw(rawDocument);
        WriteJson(parsed.GetOption("out"), humanDocument);

        if (parsed.GetOption("raw-out") is { } rawOut)
        {
            WriteJson(rawOut, rawDocument);
        }

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
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Tolerant);
        var layout = frx.Inspect(project.KnownControlNames, project.ControlScopes, parserMode);
        PatchValidator.Validate(patch, layout.Controls);

        frx.Apply(patch, project.KnownControlNames, project.ControlScopes, parserMode);

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
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Tolerant);
        var layout = frx.Inspect(project.KnownControlNames, project.ControlScopes, parserMode);
        stdout.WriteLine($"OK: {Path.GetFileName(project.FrmPath)} references {Path.GetFileName(project.FrxPath)}");
        stdout.WriteLine($"OK: FRX prefix {frx.PrefixLength} bytes, OLE compound starts at 0x{frx.OleOffset:X}");
        stdout.WriteLine($"OK: {layout.Controls.Count} controls detected");
        stdout.WriteLine($"OK: parser mode {parserMode.ToString().ToLowerInvariant()}");
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
        stdout.WriteLine("frxedit inspect <UserForm.frm> [--mode tolerant|strict|legacy] [--out layout.json]");
        stdout.WriteLine("frxedit inspect <UserForm.frm> --out layout.json --raw-out layout.raw.json");
        stdout.WriteLine("frxedit apply <UserForm.frm> <patch.json> --out <UserForm.patched.frm> [--mode tolerant|strict|legacy]");
        stdout.WriteLine("  apply supports safe in-place edits: renames, layout, tabIndex, colors, fontSize, and short strings that fit current StringSpan capacity.");
        stdout.WriteLine("frxedit validate <UserForm.frm> [--mode tolerant|strict|legacy]");
        stdout.WriteLine("frxedit dump-records <UserForm.frm> [--around TextBox3] [--before 4] [--after 8] [--out records.json]");
        stdout.WriteLine("frxedit dump-storage <UserForm.frm> [--out storage.json]");
        stdout.WriteLine("frxedit dump-stream-records <UserForm.frm> [--out stream-records.json]");
    }
}
