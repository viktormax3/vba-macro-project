internal sealed class FrxEditApp(TextWriter stdout, TextWriter stderr)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string ApplyVbaFile(string updatedFrm, string? patchPath, string frmPath, Encoding encoding)
    {
        string? vbaFile = null;
        if (patchPath is not null)
        {
            var p = Path.ChangeExtension(patchPath, ".vba");
            if (File.Exists(p)) vbaFile = p;
        }
        if (vbaFile is null)
        {
            var f = Path.ChangeExtension(frmPath, ".frm.vba");
            if (File.Exists(f)) vbaFile = f;
        }

        if (vbaFile is not null)
        {
            var (def, _) = UserFormProject.SplitFrmText(updatedFrm);
            return def + File.ReadAllText(vbaFile, encoding);
        }
        return updatedFrm;
    }

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
                "build" => Build(args[1..]),
                "validate" => Validate(args[1..]),
                "create" => Create(args[1..]),
                "watch" => Watch(args[1..]),
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
            if (outPath is not null)
            {
                ExtractImages(patchDocument, outPath, project.FormName);
            }
            
            if (outPath is not null)
            {
                var vbaOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, project.FormName + ".vba");
                File.WriteAllText(vbaOut, project.VbaCode.TrimStart('\r', '\n'), project.Encoding);
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

    private int Build(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 2);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var patchPath = parsed.Positionals.Count > 1 
            ? Path.GetFullPath(parsed.Positionals[1])
            : parsed.GetOption("patch") is { } p ? Path.GetFullPath(p) : null;
            
        var outFrmPath = Path.GetFullPath(parsed.RequireOption("out"));
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Strict);
        var streamModeStr = parsed.GetOption("stream-mode");
        var streamMode = streamModeStr is not null 
            ? ParseRebuildStreamMode(streamModeStr) 
            : RebuildStreamMode.FormAndObjectPatch;

        var patch = patchPath is not null
            ? JsonSerializer.Deserialize<PatchDocument>(File.ReadAllText(patchPath), JsonOptions)
                ?? throw new CliException("Patch file is empty.")
            : null;
        if (patch is not null && streamMode is not (RebuildStreamMode.ObjectStreamPatchProperties or RebuildStreamMode.FormAndObjectPatch))
        {
            throw new CliException("Option '--patch' requires '--stream-mode object-patch' or '--stream-mode full-patch'.");
        }

        var project = UserFormProject.Load(frmPath);
        patch?.Normalize(project.FormName);
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
            : RebuildPatchApplier.ApplyObjectPropertyPatch(sourceLayout, patch, allowFormSitePatch: streamMode == RebuildStreamMode.FormAndObjectPatch, formName: project.FormName, patchDir: parsed.GetOption("patch") is not null ? Path.GetDirectoryName(Path.GetFullPath(parsed.GetOption("patch")!)) : null);
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
        updatedFrm = ApplyVbaFile(updatedFrm, patchPath, frmPath, project.Encoding);
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
        File.WriteAllText(outFrmPath, generated.FrmText, Encoding.GetEncoding(1252));

        if (parsed.GetOption("patch") is { } patchPath)
        {
            var patch = JsonSerializer.Deserialize<PatchDocument>(File.ReadAllText(Path.GetFullPath(patchPath)), JsonOptions)
                ?? throw new CliException("Patch file is empty.");
            var project = UserFormProject.Load(outFrmPath);
            var source = FrxBinary.Read(project.FrxPath);
            var sourceLayout = source.Inspect(project.KnownControlNames, project.ControlScopes, ParserMode.Strict, project.FormProperties);
            PatchValidator.Validate(patch, sourceLayout.Controls, formName: project.FormName);
            RebuildPatchApplier.ValidateObjectPatch(patch, allowFormSitePatch: true, formName: project.FormName);
            var targetLayout = RebuildPatchApplier.ApplyObjectPropertyPatch(sourceLayout, patch, allowFormSitePatch: true, formName: project.FormName, patchDir: Path.GetDirectoryName(Path.GetFullPath(patchPath)));
            VbaCodeGenerator.Validate(patch.Code, targetLayout.Controls);
            var rebuiltBytes = FrxRebuilder.RebuildContainer(source, targetLayout, RebuildStreamMode.FormAndObjectPatch);
            File.WriteAllBytes(outFrxPath, rebuiltBytes);

            var updatedFrm = VbaRenamer.Apply(project.FrmText, patch.Renames);
            updatedFrm = UserFormProject.ReplaceOleObjectBlob(updatedFrm, Path.GetFileName(outFrxPath));
            updatedFrm = ApplyVbaFile(updatedFrm, patchPath, outFrmPath, project.Encoding);
            updatedFrm = VbaCodeGenerator.Apply(updatedFrm, patch.Code);
            updatedFrm = UserFormProject.SynchronizeFormProperties(updatedFrm, targetLayout.FrxFormControl);
            File.WriteAllText(outFrmPath, updatedFrm, project.Encoding);
        }

        var validationProject = UserFormProject.Load(outFrmPath);
        var validationFrx = FrxBinary.Read(validationProject.FrxPath);
        validationFrx.Inspect(validationProject.KnownControlNames, validationProject.ControlScopes, ParserMode.Strict, validationProject.FormProperties);

        stdout.WriteLine($"Wrote {outFrmPath}");
        stdout.WriteLine($"Wrote {outFrxPath}");
        return 0;
    }

    private int Watch(string[] args)
    {
        var parsed = CommandLine.Parse(args, minPositionals: 1, maxPositionals: 2);
        var frmPath = Path.GetFullPath(parsed.Positionals[0]);
        var patchPath = parsed.Positionals.Count > 1
            ? Path.GetFullPath(parsed.Positionals[1])
            : Path.ChangeExtension(frmPath, ".json");
        var outFrmPath = parsed.GetOption("out") != null
            ? Path.GetFullPath(parsed.GetOption("out")!)
            : frmPath; // default to in-place replacement
        
        var parserMode = parsed.GetParserModeOption("mode", ParserMode.Tolerant);

        string patchDir = Path.GetDirectoryName(patchPath)!;
        string vbaPath = Path.ChangeExtension(patchPath, ".vba");
        if (!File.Exists(vbaPath))
        {
            vbaPath = Path.ChangeExtension(frmPath, ".frm.vba");
        }

        stdout.WriteLine($"Watching for changes...");
        stdout.WriteLine($"  Base FRM: {frmPath}");
        stdout.WriteLine($"  Patch JSON: {patchPath}");
        stdout.WriteLine($"  VBA File: {vbaPath}");
        stdout.WriteLine($"  Output FRM: {outFrmPath}");
        stdout.WriteLine($"Press Ctrl+C to exit.");

        System.Threading.Timer? debounceTimer = null;
        var syncRoot = new object();

        void TriggerRebuild(object? state)
        {
            lock (syncRoot)
            {
                try
                {
                    stdout.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Change detected. Rebuilding...");
                    var streamMode = RebuildStreamMode.FormAndObjectPatch;
                    
                    var patch = JsonSerializer.Deserialize<PatchDocument>(File.ReadAllText(patchPath), JsonOptions)
                        ?? throw new CliException("Patch file is empty.");

                    var project = UserFormProject.Load(frmPath);
                    patch.Normalize(project.FormName);
                    var source = FrxBinary.Read(project.FrxPath);
                    var sourceLayout = source.Inspect(project.KnownControlNames, project.ControlScopes, parserMode, project.FormProperties);
                    PatchValidator.Validate(patch, sourceLayout.Controls, formName: project.FormName);

                    RebuildPatchApplier.ValidateObjectPatch(patch, allowFormSitePatch: true, formName: project.FormName);
                    var targetLayout = RebuildPatchApplier.ApplyObjectPropertyPatch(sourceLayout, patch, allowFormSitePatch: true, formName: project.FormName, patchDir: patchDir);
                    VbaCodeGenerator.Validate(patch.Code, targetLayout.Controls);

                    var rebuiltBytes = FrxRebuilder.RebuildContainer(source, targetLayout, streamMode);

                    var outFrxPath = Path.ChangeExtension(outFrmPath, ".frx");
                    Directory.CreateDirectory(Path.GetDirectoryName(outFrmPath)!);

                    var tmpFrxPath = outFrxPath + ".tmp";
                    var tmpFrmPath = outFrmPath + ".tmp";

                    File.WriteAllBytes(tmpFrxPath, rebuiltBytes);

                    var updatedFrm = VbaRenamer.Apply(project.FrmText, patch?.Renames);
                    updatedFrm = UserFormProject.ReplaceOleObjectBlob(updatedFrm, Path.GetFileName(outFrxPath));
                    updatedFrm = ApplyVbaFile(updatedFrm, patchPath, frmPath, project.Encoding);
                    updatedFrm = VbaCodeGenerator.Apply(updatedFrm, patch?.Code);
                    updatedFrm = UserFormProject.SynchronizeFormProperties(updatedFrm, targetLayout.FrxFormControl);
                    File.WriteAllText(tmpFrmPath, updatedFrm, project.Encoding);
                    
                    if (File.Exists(outFrxPath)) File.Replace(tmpFrxPath, outFrxPath, outFrxPath + ".bak"); else File.Move(tmpFrxPath, outFrxPath);
                    if (File.Exists(outFrmPath)) File.Replace(tmpFrmPath, outFrmPath, outFrmPath + ".bak"); else File.Move(tmpFrmPath, outFrmPath);

                    var removedScopeNames = targetLayout.RemovedControls?.Select(control => control.Name).ToList() ?? patch?.Remove;
                    UserFormProject.WriteScopesCopy(outFrmPath, project.ControlScopes, patch?.Renames, removedScopeNames);

                    if (parsed.HasOption("wysiwyg"))
                    {
                        var wysiwygPatch = FrxEdit.Cli.MsForms.Model.PatchDocumentGenerator.FromRaw(targetLayout, project.FormName);
                        var serialized = JsonSerializer.Serialize(wysiwygPatch, JsonOptions);
                        if (File.Exists(patchPath)) File.Copy(patchPath, patchPath + ".bak", overwrite: true);
                        File.WriteAllText(patchPath, serialized);
                        stdout.WriteLine($"[{DateTime.Now:HH:mm:ss}] WYSIWYG synchronized {Path.GetFileName(patchPath)}.");
                    }

                    stdout.WriteLine($"[{DateTime.Now:HH:mm:ss}] Successfully rebuilt {Path.GetFileName(outFrmPath)}");
                }
                catch (Exception ex)
                {
                    stderr.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error during rebuild: {ex.Message}");
                    if (ex is not CliException && ex is not System.Text.Json.JsonException && ex is not IOException)
                    {
                        stderr.WriteLine(ex.StackTrace);
                    }
                }
            }
        }

        void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Trigger if the changed file is our JSON, our VBA file, or any image file in the directory
            var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            var isImage = ext is ".bin" or ".jpg" or ".jpeg" or ".bmp" or ".png" or ".gif" or ".ico" or ".wmf";
            
            if (string.Equals(e.FullPath, patchPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.FullPath, vbaPath, StringComparison.OrdinalIgnoreCase) ||
                isImage)
            {
                lock (syncRoot)
                {
                    debounceTimer?.Dispose();
                    debounceTimer = new System.Threading.Timer(TriggerRebuild, null, 250, Timeout.Infinite);
                }
            }
        }

        using var watcher = new FileSystemWatcher(patchDir);
        watcher.IncludeSubdirectories = false;
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Renamed += (s, e) => OnFileChanged(s, new FileSystemEventArgs(WatcherChangeTypes.Renamed, e.FullPath, e.Name!));
        watcher.EnableRaisingEvents = true;

        var projectInfo = UserFormProject.Load(frmPath);
        string assetsDir = Path.Combine(patchDir, projectInfo.FormName);
        Directory.CreateDirectory(assetsDir);
        
        using var assetsWatcher = new FileSystemWatcher(assetsDir);
        assetsWatcher.IncludeSubdirectories = false;
        assetsWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
        assetsWatcher.Changed += OnFileChanged;
        assetsWatcher.Created += OnFileChanged;
        assetsWatcher.Renamed += (s, e) => OnFileChanged(s, new FileSystemEventArgs(WatcherChangeTypes.Renamed, e.FullPath, e.Name!));
        assetsWatcher.EnableRaisingEvents = true;

        if (frmPath != outFrmPath)
        {
            // First time rebuild to ensure outFrmPath is populated
            TriggerRebuild(null);
        }

        // Wait indefinitely until Ctrl+C is pressed
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        tcs.Task.Wait();

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

    private void ExtractImages(PatchDocument patch, string outPath, string formName)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? "";
        var assetsDirName = formName;
        var fullAssetsDir = Path.Combine(outDir, assetsDirName);
        
        bool directoryCreated = false;
        
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
                        var bytes = Convert.FromBase64String(s["base64:".Length..]);
                        var ext = ".bin";
                        if (bytes.Length > 24 &&
                            bytes[0] == 0x04 && bytes[1] == 0x52 && bytes[2] == 0xE3 && bytes[3] == 0x0B &&
                            bytes[4] == 0x91 && bytes[5] == 0x8F && bytes[6] == 0xCE && bytes[7] == 0x11 &&
                            bytes[8] == 0x9D && bytes[9] == 0xE3 && bytes[10] == 0x00 && bytes[11] == 0xAA &&
                            bytes[12] == 0x00 && bytes[13] == 0x4B && bytes[14] == 0xB8 && bytes[15] == 0x51 &&
                            bytes[16] == 0x6C && bytes[17] == 0x74 && bytes[18] == 0x00 && bytes[19] == 0x00)
                        {
                            bytes = bytes[24..];
                        }
                        
                        if (bytes.Length >= 4)
                        {
                            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00) ext = ".ico";
                            else if (bytes[0] == 0x42 && bytes[1] == 0x4D) ext = ".bmp";
                            else if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ext = ".jpg";
                            else if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ext = ".png";
                            else if (bytes[0] == 0xD7 && bytes[1] == 0xCD && bytes[2] == 0xC6 && bytes[3] == 0x9A) ext = ".wmf";
                            else if (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0x09 && bytes[3] == 0x00) ext = ".wmf"; // Some WMF/EMF headers
                        }
                        
                        if (!directoryCreated)
                        {
                            Directory.CreateDirectory(fullAssetsDir);
                            directoryCreated = true;
                        }
                        
                        var fileName = $"{prefix}_{key}{ext}";
                        var filePath = Path.Combine(fullAssetsDir, fileName);
                        File.WriteAllBytes(filePath, bytes);
                        
                        // Update the JSON to point to the file in the subfolder
                        props[key] = JsonSerializer.SerializeToElement($"file:{assetsDirName}/{fileName}");
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
        stdout.WriteLine("=================================================");
        stdout.WriteLine(" FrxEdit CLI - MS Forms Reverse Engineering Tool ");
        stdout.WriteLine("=================================================\n");

        stdout.WriteLine("--- CORE COMMANDS ---");
        
        stdout.WriteLine("frxedit inspect <UserForm.frm> [--mode tolerant|strict|legacy] [--out layout.json]");
        stdout.WriteLine("frxedit inspect <UserForm.frm> --as-patch --out layout.patch.json");
        stdout.WriteLine("frxedit inspect <UserForm.frm> --as-template --out layout.template.json");
        stdout.WriteLine("  --as-patch        Exports properties only for in-place modifications (default: extracts images to subfolder).");
        stdout.WriteLine("  --as-template     Exports both properties and structural layout to clone the form from scratch.");
        stdout.WriteLine("  --extract-images  Extracts base64 picture/mouseIcon to separate binary files and uses file:// references.\n");

        stdout.WriteLine("frxedit watch <UserForm.frm> [<patch.json>] [--out <UserForm.patched.frm>]");
        stdout.WriteLine("  Automatically rebuilds the UserForm when the JSON, VBA, or image assets change.\n");

        stdout.WriteLine("frxedit build <UserForm.frm> [<patch.json>] --out <UserForm.rebuilt.frm> [--mode strict] [--stream-mode full-patch]");
        stdout.WriteLine("  Regenerates the OLE/CFB container. Merges patch structural changes, code and properties seamlessly.\n");

        stdout.WriteLine("frxedit create <UserFormNew.frm> --name UserFormNew [--caption Demo] [--widthPt 340] [--heightPt 240] [--patch form.patch.json]\n");

        stdout.WriteLine("--- DIAGNOSTIC & DEBUG COMMANDS ---");
        stdout.WriteLine("frxedit inspect <UserForm.frm> --out layout.json --raw-out layout.raw.json");
        stdout.WriteLine("  --raw-out         Dumps raw property identifiers and streams for deep diagnostic analysis.\n");

        stdout.WriteLine("frxedit validate <UserForm.frm> [--mode tolerant|strict|legacy]");
        stdout.WriteLine("frxedit dump-records <UserForm.frm> [--around TextBox3] [--before 4] [--after 8] [--out records.json]");
        stdout.WriteLine("frxedit dump-storage <UserForm.frm> [--out storage.json]");
        stdout.WriteLine("frxedit dump-stream-records <UserForm.frm> [--out stream-records.json]");
    }
}

