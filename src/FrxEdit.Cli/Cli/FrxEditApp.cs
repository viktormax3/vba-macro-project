internal sealed class FrxEditApp(TextWriter stdout, TextWriter stderr)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
                "rebuild" => Rebuild(args[1..]),
                "create" => Create(args[1..]),
                "dump-records" => DumpRecords(args[1..]),
                "dump-storage" => DumpStorage(args[1..]),
                "dump-stream-records" => DumpStreamRecords(args[1..]),
                "check-internal" => CheckInternal(args[1..]),
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
        var layout = FrxBinary.Read(project.FrxPath).Inspect(project.KnownControlNames, project.ControlScopes, parserMode, project.FormProperties);
        var rawDocument = new LayoutDocument(
            project.FormName,
            Path.GetFileName(project.FrxPath),
            project.FormProperties,
            layout.Controls,
            layout.FrxFormControl,
            layout.ParserValidation);
        if (parsed.GetOption("as-patch") is not null || parsed.GetOption("as-template") is not null)
        {
            var isTemplate = parsed.GetOption("as-template") is not null;
            var patchDocument = FrxEdit.Cli.MsForms.Model.PatchDocumentGenerator.FromRaw(layout, project.FormName, asTemplate: isTemplate);
            
            var outPath = parsed.GetOption("out");
            if (parsed.GetOption("extract-images") is not null && outPath is not null)
            {
                ExtractImages(patchDocument, outPath);
            }
            
            WriteJson(outPath, patchDocument);
        }
        else
        {
            var humanDocument = HumanLayoutDocument.FromRaw(rawDocument);
            WriteJson(parsed.GetOption("out"), humanDocument);
        }

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
        patch.Normalize();
        if (patch.Add is { Count: > 0 })
        {
            throw new CliException("The in-place apply command does not support 'add'. Use rebuild --stream-mode full-patch.");
        }

        if (patch.Remove is { Count: > 0 })
        {
            throw new CliException("The in-place apply command does not support 'remove'. Use rebuild --stream-mode full-patch.");
        }

        var project = UserFormProject.Load(frmPath);
        var frx = FrxBinary.Read(project.FrxPath);
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Tolerant);
        var layout = frx.Inspect(project.KnownControlNames, project.ControlScopes, parserMode, project.FormProperties);
        PatchValidator.Validate(patch, layout.Controls);

        frx.Apply(patch, project.KnownControlNames, project.ControlScopes, parserMode);

        var outFrxPath = Path.ChangeExtension(outFrmPath, ".frx");
        Directory.CreateDirectory(Path.GetDirectoryName(outFrmPath)!);
        File.WriteAllBytes(outFrxPath, frx.Bytes);

        var updatedFrm = VbaRenamer.Apply(project.FrmText, patch.Renames);
        updatedFrm = UserFormProject.ReplaceOleObjectBlob(updatedFrm, Path.GetFileName(outFrxPath));
        var targetLayout = frx.Inspect(project.KnownControlNames, project.ControlScopes, parserMode, project.FormProperties);
        VbaCodeGenerator.Validate(patch.Code, targetLayout.Controls);
        updatedFrm = VbaCodeGenerator.Apply(updatedFrm, patch.Code);
        updatedFrm = UserFormProject.SynchronizeFormProperties(updatedFrm, targetLayout.FrxFormControl);
        File.WriteAllText(outFrmPath, updatedFrm, project.Encoding);
        UserFormProject.WriteScopesCopy(outFrmPath, project.ControlScopes, patch.Renames, patch.Remove);

        stdout.WriteLine($"Wrote {outFrmPath}");
        stdout.WriteLine($"Wrote {outFrxPath}");
        return 0;
    }


    private int Rebuild(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var outFrmPath = Path.GetFullPath(parsed.RequireOption("out"));
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Strict);
        var streamMode = ParseRebuildStreamMode(parsed.GetOption("stream-mode"));

        var patch = parsed.GetOption("patch") is { } patchPath
            ? JsonSerializer.Deserialize<PatchDocument>(File.ReadAllText(Path.GetFullPath(patchPath)), JsonOptions)
                ?? throw new CliException("Patch file is empty.")
            : null;
        patch?.Normalize();

        if (patch is not null && streamMode is not (RebuildStreamMode.ObjectStreamPatchProperties or RebuildStreamMode.FormAndObjectPatch))
        {
            throw new CliException("Option '--patch' requires '--stream-mode object-patch' or '--stream-mode full-patch'.");
        }

        var project = UserFormProject.Load(frmPath);
        var source = FrxBinary.Read(project.FrxPath);

        // Validate the source first. This keeps the rebuilder deliberately conservative:
        // it only round-trips FRX files that the documented parser already understands.
        var sourceLayout = source.Inspect(project.KnownControlNames, project.ControlScopes, parserMode, project.FormProperties);
        if (patch is not null)
        {
            PatchValidator.Validate(patch, sourceLayout.Controls, formName: project.FormName);
            RebuildPatchApplier.ValidateObjectPatch(patch, allowFormSitePatch: streamMode == RebuildStreamMode.FormAndObjectPatch, formName: project.FormName);
        }

        var targetLayout = patch is null
            ? sourceLayout
            : RebuildPatchApplier.ApplyObjectPropertyPatch(sourceLayout, patch, allowFormSitePatch: streamMode == RebuildStreamMode.FormAndObjectPatch, formName: project.FormName);
        if (patch is not null)
        {
            VbaCodeGenerator.Validate(patch.Code, targetLayout.Controls);
        }

        var rebuiltBytes = FrxRebuilder.RebuildContainer(source, targetLayout, streamMode);

        var outFrxPath = Path.ChangeExtension(outFrmPath, ".frx");
        Directory.CreateDirectory(Path.GetDirectoryName(outFrmPath)!);
        File.WriteAllBytes(outFrxPath, rebuiltBytes);

        var updatedFrm = VbaRenamer.Apply(project.FrmText, patch?.Renames);
        updatedFrm = UserFormProject.ReplaceOleObjectBlob(updatedFrm, Path.GetFileName(outFrxPath));
        updatedFrm = VbaCodeGenerator.Apply(updatedFrm, patch?.Code);
        updatedFrm = UserFormProject.SynchronizeFormProperties(updatedFrm, targetLayout.FrxFormControl);
        File.WriteAllText(outFrmPath, updatedFrm, project.Encoding);
        var removedScopeNames = targetLayout.RemovedControls?.Select(control => control.Name).ToList() ?? patch?.Remove;
        UserFormProject.WriteScopesCopy(outFrmPath, project.ControlScopes, patch?.Renames, removedScopeNames);

        var rebuiltProject = UserFormProject.Load(outFrmPath);
        var rebuilt = FrxBinary.Read(rebuiltProject.FrxPath);
        var rebuiltLayout = rebuilt.Inspect(rebuiltProject.KnownControlNames, rebuiltProject.ControlScopes, parserMode, rebuiltProject.FormProperties);
        var comparison = RebuildComparison.From(targetLayout, rebuiltLayout) with
        {
            InputControlCount = sourceLayout.Controls.Count,
            ExpectedControlCount = targetLayout.Controls.Count
        };

        stdout.WriteLine($"Wrote {outFrmPath}");
        stdout.WriteLine($"Wrote {outFrxPath}");
        stdout.WriteLine($"OK: rebuilt CFB container and validated with parser mode {parserMode.ToString().ToLowerInvariant()}");
        stdout.WriteLine($"OK: rebuild stream mode {FormatRebuildStreamMode(streamMode)}");
        stdout.WriteLine($"OK: controls {comparison.InputControlCount} -> {comparison.ExpectedControlCount} -> {comparison.RebuiltControlCount}, semantic match: {comparison.SemanticMatch}");

        if (parsed.GetOption("report-out") is { } reportOut)
        {
            WriteJson(reportOut, comparison);
        }

        return 0;
    }

    private int Create(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var outFrmPath = Path.GetFullPath(parsed.Positionals[0]);
        var formName = parsed.GetOption("name") ?? Path.GetFileNameWithoutExtension(outFrmPath);
        ValidateVbaIdentifier(formName, "name");
        var caption = parsed.GetOption("caption") ?? formName;
        var widthPt = GetDoubleOption(parsed, "widthPt", 240);
        var heightPt = GetDoubleOption(parsed, "heightPt", 180);
        if (widthPt <= 0 || heightPt <= 0)
        {
            throw new CliException("Options '--widthPt' and '--heightPt' must be greater than zero.");
        }

        var outFrxPath = Path.ChangeExtension(outFrmPath, ".frx");
        Directory.CreateDirectory(Path.GetDirectoryName(outFrmPath)!);
        var generated = GeneratedUserFormFactory.Create(formName, caption, widthPt, heightPt, Path.GetFileName(outFrxPath));
        File.WriteAllBytes(outFrxPath, generated.FrxBytes);
        File.WriteAllText(outFrmPath, generated.FrmText, Encoding.Default);

        if (parsed.GetOption("patch") is { } patchPath)
        {
            var patch = JsonSerializer.Deserialize<PatchDocument>(File.ReadAllText(Path.GetFullPath(patchPath)), JsonOptions)
                ?? throw new CliException("Patch file is empty.");
            var project = UserFormProject.Load(outFrmPath);
            var source = FrxBinary.Read(project.FrxPath);
            var sourceLayout = source.Inspect(project.KnownControlNames, project.ControlScopes, ParserMode.Strict, project.FormProperties);
            PatchValidator.Validate(patch, sourceLayout.Controls, formName: project.FormName);
            RebuildPatchApplier.ValidateObjectPatch(patch, allowFormSitePatch: true, formName: project.FormName);
            var targetLayout = RebuildPatchApplier.ApplyObjectPropertyPatch(sourceLayout, patch, allowFormSitePatch: true, formName: project.FormName);
            VbaCodeGenerator.Validate(patch.Code, targetLayout.Controls);
            var rebuiltBytes = FrxRebuilder.RebuildContainer(source, targetLayout, RebuildStreamMode.FormAndObjectPatch);
            File.WriteAllBytes(outFrxPath, rebuiltBytes);

            var updatedFrm = VbaRenamer.Apply(project.FrmText, patch.Renames);
            updatedFrm = UserFormProject.ReplaceOleObjectBlob(updatedFrm, Path.GetFileName(outFrxPath));
            updatedFrm = VbaCodeGenerator.Apply(updatedFrm, patch.Code);
            updatedFrm = UserFormProject.SynchronizeFormProperties(updatedFrm, targetLayout.FrxFormControl);
            File.WriteAllText(outFrmPath, updatedFrm, project.Encoding);
        }

        var validationProject = UserFormProject.Load(outFrmPath);
        var validation = FrxBinary.Read(validationProject.FrxPath).Inspect(validationProject.KnownControlNames, validationProject.ControlScopes, ParserMode.Strict, validationProject.FormProperties);

        stdout.WriteLine($"Wrote {outFrmPath}");
        stdout.WriteLine($"Wrote {outFrxPath}");
        stdout.WriteLine($"OK: created UserForm '{formName}' with {validation.Controls.Count} controls");
        return 0;
    }


    private static RebuildStreamMode ParseRebuildStreamMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("container", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildStreamMode.ContainerOnly;
        }

        if (value.Equals("object-roundtrip", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("o-roundtrip", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildStreamMode.ObjectStreamRoundTrip;
        }

        if (value.Equals("object-serialize", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("o-serialize", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("object-fixed", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildStreamMode.ObjectStreamSerializeFixed;
        }

        if (value.Equals("object-normalize", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("object-variable", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("o-normalize", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildStreamMode.ObjectStreamNormalizeStrings;
        }

        if (value.Equals("object-patch", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("object-mutate", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("o-patch", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildStreamMode.ObjectStreamPatchProperties;
        }

        if (value.Equals("full-patch", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("form-patch", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("site-patch", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildStreamMode.FormAndObjectPatch;
        }

        throw new CliException("Option '--stream-mode' must be one of: container, object-roundtrip, object-serialize, object-normalize, object-patch, full-patch.");
    }

    private static string FormatRebuildStreamMode(RebuildStreamMode mode) =>
        mode switch
        {
            RebuildStreamMode.ObjectStreamRoundTrip => "object-roundtrip",
            RebuildStreamMode.ObjectStreamSerializeFixed => "object-serialize",
            RebuildStreamMode.ObjectStreamNormalizeStrings => "object-normalize",
            RebuildStreamMode.ObjectStreamPatchProperties => "object-patch",
            RebuildStreamMode.FormAndObjectPatch => "full-patch",
            _ => "container"
        };

    private static double GetDoubleOption(CommandLine parsed, string name, double defaultValue)
    {
        var value = parsed.GetOption(name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue))
        {
            throw new CliException($"Option '--{name}' must be a number.");
        }

        return parsedValue;
    }

    private static void ValidateVbaIdentifier(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]{0,30}$"))
        {
            throw new CliException($"Option '--{optionName}' must be a valid VBA identifier up to 31 characters.");
        }
    }

    private int Validate(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var project = UserFormProject.Load(Path.GetFullPath(parsed.Positionals[0]));
        var frx = FrxBinary.Read(project.FrxPath);
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Tolerant);
        var layout = frx.Inspect(project.KnownControlNames, project.ControlScopes, parserMode, project.FormProperties);
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
 
    private int CheckInternal(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 1);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var project = UserFormProject.Load(frmPath);
        var frx = FrxBinary.Read(project.FrxPath);
        var storage = CompoundStorageInspector.Inspect(frx.Bytes, frx.OleOffset);

        foreach (var stream in storage.Streams.Where(s => s.Kind.Equals("Stream", StringComparison.OrdinalIgnoreCase)))
        {
            if (stream.Name.Equals("f", StringComparison.OrdinalIgnoreCase))
            {
                var sites = StructuredMsFormsParser.Parse(stream);
                stdout.WriteLine($"Stream '{stream.Path}' has {sites.Count} total sites:");
                foreach (var s in sites)
                {
                    stdout.WriteLine($"  - Index: {s.SiteIndex}, Name: '{s.Name}', Type: '{s.ControlType}', Depth: {s.Depth}, IsInternal: {s.IsInternalSite}, Size: {s.StreamEnd - s.StreamStart}");
                }
            }
        }
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

    private void ExtractImages(PatchDocument patch, string outPath)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? "";
        
        void ProcessProperties(Dictionary<string, JsonElement>? props, string prefix)
        {
            if (props is null) return;
            foreach (var key in props.Keys.ToList())
            {
                if (props[key].ValueKind == JsonValueKind.String)
                {
                    var s = props[key].GetString();
                    if (s != null && s.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
                    {
                        var base64 = s["base64:".Length..];
                        var fileName = $"{prefix}_{key}.bin";
                        var fullPath = Path.Combine(outDir, fileName);
                        File.WriteAllBytes(fullPath, Convert.FromBase64String(base64));
                        props[key] = JsonSerializer.SerializeToElement($"file://{fileName}");
                    }
                }
            }
        }

        if (patch.Properties is not null)
        {
            foreach (var pair in patch.Properties)
            {
                ProcessProperties(pair.Value, pair.Key);
            }
        }
        if (patch.Add is not null)
        {
            foreach (var control in patch.Add)
            {
                ProcessProperties(control.Properties, control.Name ?? "unnamed");
            }
        }
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
        stdout.WriteLine("frxedit inspect <UserForm.frm> --as-patch --out layout.patch.json");
        stdout.WriteLine("frxedit inspect <UserForm.frm> --as-template --out layout.template.json [--extract-images]");
        stdout.WriteLine("  --as-patch exports properties only for in-place modifications.");
        stdout.WriteLine("  --as-template exports both properties and structural layout to clone the form from scratch.");
        stdout.WriteLine("  --extract-images extracts base64 picture/mouseIcon to separate binary files and uses file:// references.");
        stdout.WriteLine("frxedit inspect <UserForm.frm> --out layout.json --raw-out layout.raw.json");
        stdout.WriteLine("frxedit apply <UserForm.frm> <patch.json> --out <UserForm.patched.frm> [--mode tolerant|strict|legacy]");
        stdout.WriteLine("  apply supports safe in-place edits: renames, layout, tabIndex, colors, fontSize, and short strings that fit current StringSpan capacity.");
        stdout.WriteLine("frxedit rebuild <UserForm.frm> --out <UserForm.rebuilt.frm> [--mode strict] [--stream-mode container|object-roundtrip|object-serialize|object-normalize|object-patch|full-patch] [--patch patch.json] [--report-out rebuild.report.json]");
        stdout.WriteLine("  rebuild regenerates the OLE/CFB container. stream-mode object-roundtrip reconstructs o streams from parser-identified object slices; object-serialize rewrites fixed-length known fields through control serializers; object-normalize rebuilds o streams with normalized counted strings and updates ObjectStreamSize metadata in f streams; object-patch applies variable-length object-payload property patches before rebuilding; full-patch also rebuilds FormSiteData for layout, renames, add, and remove.");
        stdout.WriteLine("frxedit create <UserFormNew.frm> --name UserFormNew [--caption Demo] [--widthPt 340] [--heightPt 240] [--patch form.patch.json]");
        stdout.WriteLine("frxedit validate <UserForm.frm> [--mode tolerant|strict|legacy]");
        stdout.WriteLine("frxedit dump-records <UserForm.frm> [--around TextBox3] [--before 4] [--after 8] [--out records.json]");
        stdout.WriteLine("frxedit dump-storage <UserForm.frm> [--out storage.json]");
        stdout.WriteLine("frxedit dump-stream-records <UserForm.frm> [--out stream-records.json]");
    }
}
