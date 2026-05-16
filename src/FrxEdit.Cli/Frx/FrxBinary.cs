internal sealed class FrxBinary
{
    private const double FrxUnitsPerPoint = 2540.0 / 72.0;
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
    private static readonly string[] StandardNamePrefixes =
    [
        "CommandButton",
        "TextBox",
        "CheckBox",
        "Frame",
        "Label",
        "ComboBox",
        "SpinButton",
        "OptionButton",
        "Image",
        "ToggleButton",
        "ScrollBar",
        "TabStrip",
        "MultiPage",
        "ListBox",
        "CustButton",
    ];

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
                ToPoints(placement?.RawWidth),
                ToPoints(placement?.RawHeight),
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
        var controlsBySiteId = new Dictionary<uint, string>();
        var formControlByOwner = new Dictionary<string, FormControlProperties>(StringComparer.OrdinalIgnoreCase);
        var pendingContainerOwners = new Queue<string>();
        var fStreams = storage.Streams
            .Where(s => s.Kind == "Stream" && s.Name == "f")
            .OrderBy(s => PathDepth(s.ParentPath))
            .ThenBy(s => s.Index)
            .ToList();

        foreach (var stream in fStreams)
        {
            if (stream.FileOffsets.Length != stream.Data.Length)
            {
                continue;
            }

            var streamOwner = ResolveStreamOwner(stream, controlsBySiteId);
            if (streamOwner is null && stream.ParentPath is not null && !IsRootStoragePath(stream.ParentPath) && pendingContainerOwners.Count > 0)
            {
                streamOwner = pendingContainerOwners.Dequeue();
            }

            if (streamOwner is not null && FormControlParser.TryRead(stream, out var formControlProperties))
            {
                formControlByOwner[streamOwner] = formControlProperties;
            }

            var pairedOStream = FindPairedObjectStream(storage.Streams, stream);
            var streamRecords = FormStreamParser.Read(stream, knownControlNames, pairedOStream);
            var streamControls = BuildControlInfos(streamRecords, streamOwner, controlScopes);

            foreach (var control in streamControls)
            {
                controls.TryAdd(control.NameOffset, control);

                if (TryGetSiteId(control, out var siteId))
                {
                    controlsBySiteId[siteId] = control.Name;
                }

                if (HasOwnStorage(control) && !TryGetSiteId(control, out _))
                {
                    pendingContainerOwners.Enqueue(control.Name);
                }
            }
        }

        var objectStreams = storage.Streams.Where(s => s.Kind == "Stream" && s.Name == "o").ToList();
        if (controls.Count == 1 && objectStreams.Count == 1)
        {
            var key = controls.Keys.Single();
            controls[key] = EnrichSingleControlFromOStream(controls[key], objectStreams[0]);
        }

        if (formControlByOwner.Count > 0)
        {
            foreach (var key in controls.Keys.ToList())
            {
                var control = controls[key];
                if (formControlByOwner.TryGetValue(control.Name, out var formControl))
                {
                    controls[key] = ApplyFormControlProperties(control, formControl);
                }
            }
        }

        return controls.Values.ToList();
    }

    private static int PathDepth(string? path) =>
        string.IsNullOrEmpty(path) ? 0 : path.Count(ch => ch == '/');

    private static bool IsRootStoragePath(string path) =>
        path.Equals("Root Entry", StringComparison.OrdinalIgnoreCase) ||
        !path.Contains('/', StringComparison.Ordinal);

    private static string? ResolveStreamOwner(StorageEntryDump stream, IReadOnlyDictionary<uint, string> controlsBySiteId)
    {
        if (string.IsNullOrEmpty(stream.ParentPath) || IsRootStoragePath(stream.ParentPath))
        {
            return null;
        }

        var slash = stream.ParentPath.LastIndexOf('/');
        var storageName = slash >= 0 ? stream.ParentPath[(slash + 1)..] : stream.ParentPath;
        if (storageName.Length > 1 && storageName[0] is 'i' or 'I' &&
            uint.TryParse(storageName[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var siteId) &&
            controlsBySiteId.TryGetValue(siteId, out var owner))
        {
            return owner;
        }

        return null;
    }

    private static bool TryGetSiteId(ControlInfo control, out uint siteId)
    {
        siteId = 0;
        if (control.Properties is null)
        {
            return false;
        }

        if (!control.Properties.TryGetValue("siteId", out var value) &&
            !control.Properties.TryGetValue("id", out value))
        {
            return false;
        }

        switch (value)
        {
            case uint u:
                siteId = u;
                return true;
            case int i when i >= 0:
                siteId = (uint)i;
                return true;
            case long l when l >= 0 && l <= uint.MaxValue:
                siteId = (uint)l;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetUInt32(out var parsed):
                siteId = parsed;
                return true;
            default:
                return false;
        }
    }

    private IReadOnlyList<ControlInfo> BuildControlInfos(
        IReadOnlyList<StructuredControlRecord> records,
        string? streamOwner,
        IReadOnlyDictionary<string, string>? controlScopes)
    {
        var controls = new List<ControlInfo>(records.Count);
        var depthParents = new Dictionary<int, string>();
        foreach (var record in records)
        {
            var depth = GetSiteDepth(record.SiteProperties);
            var parent = depth == 0
                ? streamOwner
                : depthParents.TryGetValue(depth - 1, out var nestedParent)
                    ? nestedParent
                    : streamOwner;
            var control = BuildControlInfo(record, parent, controlScopes);
            controls.Add(control);
            depthParents[depth] = control.Name;

            foreach (var deeperDepth in depthParents.Keys.Where(key => key > depth).ToList())
            {
                depthParents.Remove(deeperDepth);
            }
        }

        return controls;
    }

    private ControlInfo BuildControlInfo(
        StructuredControlRecord record,
        string? siteParent,
        IReadOnlyDictionary<string, string>? controlScopes)
    {
        var fileOffsets = record.Stream.FileOffsets;
        var recordStartOffset = record.RecordStartOffset < fileOffsets.Length ? fileOffsets[record.RecordStartOffset] : 0;
        var markerOffset = record.Marker.Offset < fileOffsets.Length ? fileOffsets[record.Marker.Offset] : 0;
        var nameOffset = record.NameOffset < fileOffsets.Length ? fileOffsets[record.NameOffset] : 0;
        var properties = new Dictionary<string, object?>(record.SiteProperties, StringComparer.OrdinalIgnoreCase)
        {
            ["streamName"] = record.Stream.Name,
            ["streamIndex"] = record.Stream.Index,
            ["streamLocalRecordStartOffset"] = record.RecordStartOffset,
            ["streamLocalRecordEndOffset"] = record.RecordEndOffset,
            ["streamLocalNameOffset"] = record.NameOffset,
            ["nameLength"] = record.NameLength,
            ["recordMarkerHex"] = ReadHex(markerOffset, 4),
            ["recordMarkerOffset"] = markerOffset,
            ["recordStartOffset"] = recordStartOffset,
            ["streamLocalRecordMarkerOffset"] = record.Marker.Offset,
            ["tabIndex"] = record.Marker.TabIndex,
            ["recordTypeCode"] = $"0x{record.Marker.TypeCode:X2}",
        };
        properties.TryAdd("parser", "structuredStorageFStream");
        AddFStreamTextProperties(properties, record);

        return new ControlInfo(
            record.Name,
            record.Type,
            record.Placement.Left,
            record.Placement.Top,
            record.Placement.RawWidth,
            record.Placement.RawHeight,
            ToPoints(record.Placement.Left),
            ToPoints(record.Placement.Top),
            ToPoints(record.Placement.RawWidth),
            ToPoints(record.Placement.RawHeight),
            properties,
            siteParent ?? (controlScopes?.TryGetValue(record.Name, out var scope) == true ? scope : null),
            record.RawName.Equals(record.Name, StringComparison.OrdinalIgnoreCase) ? null : record.RawName,
            null,
            null,
            null,
            nameOffset,
            record.Placement.LeftOffset,
            record.Placement.TopOffset,
            record.Placement.WidthOffset,
            record.Placement.HeightOffset);
    }

    private static int GetSiteDepth(Dictionary<string, object?> properties)
    {
        if (properties.TryGetValue("siteDepth", out var value))
        {
            return value switch
            {
                byte b => b,
                int i => i,
                JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var i) => i,
                _ => 0,
            };
        }

        return 0;
    }

    private static bool HasOwnStorage(ControlInfo control)
    {
        if (control.Properties?.TryGetValue("streamed", out var streamed) == true && streamed is bool streamedValue)
        {
            return !streamedValue;
        }

        return control.Type.Equals("Frame", StringComparison.OrdinalIgnoreCase) ||
            control.Type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase) ||
            control.Type.Equals("Page", StringComparison.OrdinalIgnoreCase);
    }

    private static StorageEntryDump? FindPairedObjectStream(
        IReadOnlyList<StorageEntryDump> streams,
        StorageEntryDump fStream)
    {
        if (!string.IsNullOrEmpty(fStream.ParentPath))
        {
            var sameStorageObjectStream = streams.FirstOrDefault(s =>
                s.Kind == "Stream" &&
                s.Name == "o" &&
                string.Equals(s.ParentPath, fStream.ParentPath, StringComparison.Ordinal));
            if (sameStorageObjectStream is not null)
            {
                return sameStorageObjectStream;
            }
        }

        foreach (var stream in streams.Where(s => s.Index > fStream.Index).OrderBy(s => s.Index))
        {
            if (stream.Name == "f")
            {
                return null;
            }

            if (stream.Kind == "Stream" && stream.Name == "o")
            {
                return stream;
            }
        }

        return null;
    }

    private ControlInfo EnrichSingleControlFromOStream(ControlInfo control, StorageEntryDump oStream)
    {
        if (oStream.FileOffsets.Length != oStream.Data.Length)
        {
            return control;
        }

        var properties = control.Properties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(control.Properties, StringComparer.OrdinalIgnoreCase);
        properties["objectStreamIndex"] = oStream.Index;

        var oProperties = ObjectStreamParser.Read(oStream, control.Type);
        foreach (var (name, value) in oProperties.Properties)
        {
            if (name.Equals("parser", StringComparison.OrdinalIgnoreCase) && properties.ContainsKey("siteParser"))
            {
                properties["objectParser"] = value;
                continue;
            }

            properties[name] = value;
        }

        return control with
        {
            RawWidth = oProperties.Width ?? control.RawWidth,
            RawHeight = oProperties.Height ?? control.RawHeight,
            WidthPt = ToPoints(oProperties.Width ?? control.RawWidth),
            HeightPt = ToPoints(oProperties.Height ?? control.RawHeight),
            WidthOffset = oProperties.WidthOffset ?? control.WidthOffset,
            HeightOffset = oProperties.HeightOffset ?? control.HeightOffset,
            Properties = properties,
        };
    }

    private static void AddFStreamTextProperties(
        Dictionary<string, object?> properties,
        StructuredControlRecord record)
    {
        var data = record.Stream.Data;
        var fileOffsets = record.Stream.FileOffsets;
        var nameOffset = record.NameOffset;
        var rawNameLength = record.NameLength;
        var placement = record.Placement;
        var recordEndOffset = record.RecordEndOffset;
        var leftLocalOffset = FindLocalOffset(fileOffsets, placement.LeftOffset);
        if (leftLocalOffset is null)
        {
            return;
        }

        var tagStart = SkipZeroBytes(data, nameOffset + rawNameLength);
        if (tagStart < leftLocalOffset.Value && tagStart < recordEndOffset)
        {
            var tag = ReadIdentifierLikeText(data, tagStart, Math.Min(leftLocalOffset.Value, recordEndOffset) - tagStart);
            if (!string.IsNullOrWhiteSpace(tag) && !properties.ContainsKey("tag"))
            {
                properties["tag"] = tag;
                properties["tagOffset"] = fileOffsets[tagStart];
            }
        }

        var afterTop = leftLocalOffset.Value + 8;
        var controlTipEnd = Math.Min(recordEndOffset, afterTop + 80);
        var controlTip = afterTop < controlTipEnd
            ? FindNextIdentifierLikeText(data, afterTop, controlTipEnd)
            : null;
        if (controlTip is not null)
        {
            properties.TryAdd("controlTipTextRaw", controlTip.Value.Text);
            properties.TryAdd("controlTipText", TrimLikelyPropertySuffix(controlTip.Value.Text));
            properties.TryAdd("controlTipTextOffset", fileOffsets[controlTip.Value.Offset]);
        }
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

    private static ControlInfo ApplyFormControlProperties(ControlInfo control, FormControlProperties formControl)
    {
        var properties = control.Properties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(control.Properties, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in formControl.Properties)
        {
            properties.TryAdd(name, value);
        }

        var width = formControl.DisplayedWidth ?? control.RawWidth;
        var height = formControl.DisplayedHeight ?? control.RawHeight;
        return control with
        {
            RawWidth = width,
            RawHeight = height,
            WidthPt = ToPoints(width),
            HeightPt = ToPoints(height),
            WidthOffset = formControl.DisplayedWidthOffset ?? control.WidthOffset,
            HeightOffset = formControl.DisplayedHeightOffset ?? control.HeightOffset,
            Properties = WithProperty(properties, "sizeSource", "formControlDisplayedSize")
        };
    }

    private IReadOnlyList<ControlInfo> AddContainerDimensions(IReadOnlyList<ControlInfo> controls)
    {
        var result = controls.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            var control = result[i];
            if (!control.Type.Equals("Frame", StringComparison.OrdinalIgnoreCase) ||
                control.RawWidth is not null && control.RawHeight is not null)
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
                Properties = WithProperty(control.Properties, "sizeSource", "legacyContainerDimensionFallback")
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

        foreach (var (patchName, requested) in patch.Properties ?? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase))
        {
            var sourceName = reverseNameMap.TryGetValue(patchName, out var oldName) ? oldName : patchName;
            if (!controls.TryGetValue(sourceName, out var control))
            {
                throw new CliException($"Properties target '{patchName}' was not found.");
            }

            WriteProperties(control, requested);
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
            throw new CliException("Patch fields 'width' and 'height' are reserved. Use 'widthPt'/'heightPt' for human-friendly edits or 'rawWidth'/'rawHeight' for low-level experiments.");
        }

        WriteOptionalInt(control.LeftOffset, ResolvePatchUnit(patch.Left, patch.LeftPt, "left", "leftPt"), control.Name, "left");
        WriteOptionalInt(control.TopOffset, ResolvePatchUnit(patch.Top, patch.TopPt, "top", "topPt"), control.Name, "top");
        WriteOptionalInt(control.WidthOffset, ResolvePatchUnit(patch.RawWidth, patch.WidthPt, "rawWidth", "widthPt"), control.Name, "width");
        WriteOptionalInt(control.HeightOffset, ResolvePatchUnit(patch.RawHeight, patch.HeightPt, "rawHeight", "heightPt"), control.Name, "height");
    }

    private static int? ResolvePatchUnit(int? rawValue, double? pointValue, string rawName, string pointName)
    {
        if (rawValue is not null && pointValue is not null)
        {
            throw new CliException($"Patch cannot specify both '{rawName}' and '{pointName}' for the same dimension.");
        }

        if (rawValue is not null)
        {
            return rawValue;
        }

        return FromPoints(pointValue);
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

    private void WriteProperties(ControlInfo control, Dictionary<string, JsonElement> properties)
    {
        foreach (var (property, value) in properties)
        {
            switch (property.ToLowerInvariant())
            {
                case "caption":
                    WriteStringProperty(control, property, value, preservePrintableSuffix: false);
                    break;
                case "tag":
                case "controltiptext":
                case "fontname":
                    WriteStringProperty(control, property, value, preservePrintableSuffix: false);
                    break;
                case "backcolor":
                case "forecolor":
                    WriteColorProperty(control, property, value);
                    break;
                case "fontsize":
                    WriteFontSizeProperty(control, value);
                    break;
                case "tabindex":
                    WriteTabIndex(control, value);
                    break;
                default:
                    throw new CliException($"Property '{property}' is not writable yet.");
            }
        }
    }

    private void WriteStringProperty(ControlInfo control, string property, JsonElement value, bool preservePrintableSuffix)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CliException($"Property '{property}' for '{control.Name}' must be a string.");
        }

        var text = value.GetString() ?? string.Empty;
        var properties = control.Properties ?? throw new CliException($"Control '{control.Name}' has no property metadata.");
        var offset = GetRequiredIntProperty(properties, $"{property}Offset", control.Name);
        var current = GetStringProperty(properties, property) ?? string.Empty;
        var raw = GetStringProperty(properties, $"{property}Raw") ?? current;
        var suffix = preservePrintableSuffix ? GetPrintableFlagSuffix(raw, current.Length) ?? string.Empty : string.Empty;
        var capacity = Encoding.Latin1.GetByteCount(raw);
        var bytes = Encoding.Latin1.GetBytes(text + suffix);
        if (bytes.Length > capacity)
        {
            throw new CliException($"Property '{property}' for '{control.Name}' is longer than the current in-place capacity ({capacity} bytes).");
        }

        bytes.CopyTo(Bytes.AsSpan(offset, bytes.Length));
        Bytes.AsSpan(offset + bytes.Length, capacity - bytes.Length).Clear();
    }

    private void WriteColorProperty(ControlInfo control, string property, JsonElement value)
    {
        var properties = control.Properties ?? throw new CliException($"Control '{control.Name}' has no property metadata.");
        var offset = GetRequiredIntProperty(properties, $"{property}Offset", control.Name);
        var color = ParseColor(value, property, control.Name);
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset, 4), color);
    }

    private void WriteFontSizeProperty(ControlInfo control, JsonElement value)
    {
        var properties = control.Properties ?? throw new CliException($"Control '{control.Name}' has no property metadata.");
        var offset = GetRequiredIntProperty(properties, "fontSizeOffset", control.Name);
        var size = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            _ => throw new CliException($"Property 'fontSize' for '{control.Name}' must be numeric.")
        };
        if (size is <= 0 or > 72)
        {
            throw new CliException($"Property 'fontSize' for '{control.Name}' must be between 0 and 72.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset, 4), (int)Math.Round(size * 20));
    }

    private void WriteTabIndex(ControlInfo control, JsonElement value)
    {
        var properties = control.Properties ?? throw new CliException($"Control '{control.Name}' has no property metadata.");
        var markerOffset = GetRequiredIntProperty(properties, "recordMarkerOffset", control.Name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var tabIndex) || tabIndex is < 0 or > 255)
        {
            throw new CliException($"Property 'tabIndex' for '{control.Name}' must be an integer from 0 to 255.");
        }

        Bytes[markerOffset] = (byte)tabIndex;
    }

    private static int GetRequiredIntProperty(Dictionary<string, object?> properties, string property, string controlName)
    {
        if (properties.TryGetValue(property, out var value) && value is int offset)
        {
            return offset;
        }

        throw new CliException($"Control '{controlName}' does not expose writable metadata '{property}'.");
    }

    private static string? GetStringProperty(Dictionary<string, object?> properties, string property) =>
        properties.TryGetValue(property, out var value) ? value as string : null;

    private static uint ParseColor(JsonElement value, string property, string controlName)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CliException($"Property '{property}' for '{controlName}' must be a VBA color string like &H8000000D&.");
        }

        var text = value.GetString() ?? string.Empty;
        text = text.Trim();
        if (text.StartsWith("&H", StringComparison.OrdinalIgnoreCase) && text.EndsWith('&'))
        {
            text = text[2..^1];
        }

        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var color))
        {
            throw new CliException($"Property '{property}' for '{controlName}' has invalid color value '{value.GetString()}'.");
        }

        return color;
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

    private static int? FromPoints(double? value) =>
        value is null ? null : (int)Math.Round(value.Value * FrxUnitsPerPoint, MidpointRounding.AwayFromZero);

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

    private static string NormalizeStructuredName(string binaryName, IReadOnlySet<string>? knownControlNames)
    {
        var known = CanonicalizeName(binaryName, knownControlNames);
        if (!known.Equals(binaryName, StringComparison.OrdinalIgnoreCase))
        {
            return known;
        }

        foreach (var prefix in StandardNamePrefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (!binaryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digitOffset = prefix.Length;
            var offset = digitOffset;
            while (offset < binaryName.Length && char.IsDigit(binaryName[offset]))
            {
                offset++;
            }

            if (offset == digitOffset || offset == binaryName.Length)
            {
                continue;
            }

            var candidate = binaryName[..offset];
            if (knownControlNames?.Contains(candidate) == true)
            {
                return candidate;
            }

            var suffix = binaryName[offset..];
            if (suffix.Length <= 3 && suffix.All(char.IsLower))
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

    private static int SkipZeroBytes(byte[] data, int offset)
    {
        while (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }

        return offset;
    }

    private static int? FindLocalOffset(int[] fileOffsets, int fileOffset)
    {
        for (var i = 0; i < fileOffsets.Length; i++)
        {
            if (fileOffsets[i] == fileOffset)
            {
                return i;
            }
        }

        return null;
    }

    private static string? ReadIdentifierLikeText(byte[] data, int offset, int maxLength)
    {
        if (offset >= data.Length || !IsPrintableAscii(data[offset]))
        {
            return null;
        }

        var end = offset;
        var limit = Math.Min(data.Length, offset + maxLength);
        while (end < limit && IsPrintableAscii(data[end]) && data[end] != 0)
        {
            end++;
        }

        return end - offset < 3 ? null : Encoding.Latin1.GetString(data, offset, end - offset);
    }

    private static TextRun? FindNextIdentifierLikeText(byte[] data, int start, int end)
    {
        foreach (var run in FindTextRuns(data, 3).Where(run => run.Offset >= start && run.Offset < end))
        {
            return run;
        }

        return null;
    }

    private static IReadOnlyList<TextRun> FindTextRuns(byte[] data, int minLength)
    {
        var runs = new List<TextRun>();
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
            if (length >= minLength)
            {
                runs.Add(new TextRun(start, Encoding.Latin1.GetString(data, start, length)));
            }
        }

        return runs;
    }

    private static string TrimLikelyPropertySuffix(string value)
    {
        value = TrimTrailingBinaryChars(value);

        if (value.Length > 6 &&
            value[^2] is >= 'A' and <= 'Z' &&
            char.IsLetter(value[^1]) &&
            char.IsLower(value[^3]))
        {
            return value[..^2];
        }

        return value;
    }

    private static string? GetPrintableFlagSuffix(string rawValue, int valueLength)
    {
        if (valueLength >= rawValue.Length)
        {
            return null;
        }

        var suffix = TrimTrailingBinaryChars(rawValue[valueLength..]);
        if (suffix.Length == 0)
        {
            return null;
        }

        return suffix.All(ch => ch is 'P' or 'C') ? suffix : null;
    }

    private static string TrimTrailingBinaryChars(string value)
    {
        var end = value.Length;
        while (end > 0 && (value[end - 1] == '\0' || char.IsControl(value[end - 1])))
        {
            end--;
        }

        return end == value.Length ? value : value[..end];
    }

    private static PairCandidate? FindPlausiblePair(byte[] data, int start, int maxDistance)
    {
        var end = Math.Min(data.Length - 8, start + maxDistance);
        for (var offset = start; offset <= end; offset++)
        {
            var first = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            var second = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (IsPlausiblePosition(first) && IsPlausiblePosition(second))
            {
                return new PairCandidate(offset, first, second);
            }
        }

        return null;
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
