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

            var streamRecords = ReadStructuredControlRecordsFromFStream(stream, knownControlNames);
            var streamControls = streamRecords
                .Select(record => BuildControlInfo(record, controlScopes))
                .ToList();
            var pairedOStream = FindPairedObjectStream(storage.Streams, stream);
            if (streamControls.Count == 1 && pairedOStream is not null)
            {
                streamControls = [EnrichSingleControlFromOStream(streamControls[0], pairedOStream)];
            }

            foreach (var control in streamControls)
            {
                controls.TryAdd(control.NameOffset, control);
            }
        }

        var objectStreams = storage.Streams.Where(s => s.Kind == "Stream" && s.Name == "o").ToList();
        if (controls.Count == 1 && objectStreams.Count == 1)
        {
            var key = controls.Keys.Single();
            controls[key] = EnrichSingleControlFromOStream(controls[key], objectStreams[0]);
        }

        return controls.Values.ToList();
    }

    private IReadOnlyList<StructuredControlRecord> ReadStructuredControlRecordsFromFStream(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames)
    {
        var data = stream.Data;
        var candidates = new List<StructuredControlCandidate>();
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

            var name = NormalizeStructuredName(rawName, knownControlNames);
            var placement = TryReadStreamPlacement(data, stream.FileOffsets, textOffset + rawName.Length);
            if (placement is null)
            {
                continue;
            }

            candidates.Add(new StructuredControlCandidate(marker, textOffset, rawName, name, type, placement));
        }

        candidates = candidates
            .OrderBy(candidate => candidate.NameOffset)
            .ToList();

        var records = new List<StructuredControlRecord>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var endOffset = i + 1 < candidates.Count ? candidates[i + 1].Marker.Offset : data.Length;
            records.Add(new StructuredControlRecord(
                stream,
                candidate.Marker,
                candidate.Marker.Offset,
                endOffset,
                candidate.NameOffset,
                candidate.RawName.Length,
                candidate.RawName,
                candidate.Name,
                candidate.Type,
                candidate.Placement,
                ObjectStream: null));
        }

        return records;
    }

    private ControlInfo BuildControlInfo(
        StructuredControlRecord record,
        IReadOnlyDictionary<string, string>? controlScopes)
    {
        var markerOffset = record.Stream.FileOffsets[record.RecordStartOffset];
        var nameOffset = record.Stream.FileOffsets[record.NameOffset];
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["streamName"] = record.Stream.Name,
            ["streamIndex"] = record.Stream.Index,
            ["streamLocalRecordStartOffset"] = record.RecordStartOffset,
            ["streamLocalRecordEndOffset"] = record.RecordEndOffset,
            ["streamLocalNameOffset"] = record.NameOffset,
            ["nameLength"] = record.NameLength,
            ["recordMarkerHex"] = ReadHex(markerOffset, 4),
            ["recordMarkerOffset"] = markerOffset,
            ["streamLocalRecordMarkerOffset"] = record.Marker.Offset,
            ["tabIndex"] = record.Marker.TabIndex,
            ["recordTypeCode"] = $"0x{record.Marker.TypeCode:X2}",
            ["parser"] = "structuredStorageFStream"
        };
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
            null,
            null,
            properties,
            controlScopes?.TryGetValue(record.Name, out var scope) == true ? scope : null,
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

    private static StorageEntryDump? FindPairedObjectStream(
        IReadOnlyList<StorageEntryDump> streams,
        StorageEntryDump fStream)
    {
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

        var oProperties = ReadObjectStreamProperties(oStream, control.Type);
        foreach (var (name, value) in oProperties.Properties)
        {
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
            if (!string.IsNullOrWhiteSpace(tag))
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
            properties["controlTipTextRaw"] = controlTip.Value.Text;
            properties["controlTipText"] = TrimLikelyPropertySuffix(controlTip.Value.Text);
            properties["controlTipTextOffset"] = fileOffsets[controlTip.Value.Offset];
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

    private ObjectStreamProperties ReadObjectStreamProperties(StorageEntryDump stream, string? controlType = null)
    {
        if (controlType?.Equals("CommandButton", StringComparison.OrdinalIgnoreCase) == true &&
            TryReadCommandButtonObjectStream(stream) is { } commandButton)
        {
            return commandButton;
        }

        return ReadObjectStreamPropertiesHeuristic(stream);
    }

    private ObjectStreamProperties ReadObjectStreamPropertiesHeuristic(StorageEntryDump stream)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
        };

        var data = stream.Data;
        var colors = FindSystemColors(data, stream.FileOffsets);
        if (colors.Count > 0)
        {
            properties["systemColors"] = colors;
            if (data.Length >= 16 && data[2] == 0x40)
            {
                var foreColor = colors.FirstOrDefault(c => c.StreamOffset == 8);
                var backColor = colors.FirstOrDefault(c => c.StreamOffset == 12);
                if (backColor is not null)
                {
                    properties["backColor"] = backColor.Value;
                    properties["backColorOffset"] = backColor.Offset;
                }

                if (foreColor is not null)
                {
                    properties["foreColor"] = foreColor.Value;
                    properties["foreColorOffset"] = foreColor.Offset;
                }
            }
        }

        var textRuns = FindTextRuns(data, minLength: 3);
        TextRun? fontRun = FindFontNameRun(data, textRuns);

        if (fontRun is not null)
        {
            properties["fontName"] = fontRun.Value.Text;
            properties["fontNameOffset"] = stream.FileOffsets[fontRun.Value.Offset];
            var fontSize = FindFontSizeBefore(data, fontRun.Value.Offset);
            if (fontSize is not null)
            {
                properties["fontSize"] = fontSize.Value.Size;
                properties["fontSizeRaw"] = fontSize.Value.Raw;
                properties["fontSizeOffset"] = stream.FileOffsets[fontSize.Value.Offset];
                if (fontSize.Value.Offset >= 4)
                {
                    properties["fontStyleRawHex"] = Convert.ToHexString(data.AsSpan(fontSize.Value.Offset - 4, 4));
                    properties["fontStyleRawOffset"] = stream.FileOffsets[fontSize.Value.Offset - 4];
                }
            }
        }

        TextRun? captionRun = null;
        foreach (var run in textRuns)
        {
            if (fontRun is not null && run.Offset == fontRun.Value.Offset)
            {
                continue;
            }

            if (run.Text.Equals("Tahoma", StringComparison.OrdinalIgnoreCase) ||
                run.Text.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            captionRun = run;
            break;
        }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;
        if (captionRun is not null)
        {
            var dimensions = FindPlausiblePair(data, captionRun.Value.Offset + captionRun.Value.Text.Length, 12);
            if (dimensions is not null)
            {
                var captionBytesLength = Math.Max(0, dimensions.Value.Offset - captionRun.Value.Offset);
                var rawCaption = Encoding.Latin1.GetString(data, captionRun.Value.Offset, captionBytesLength);
                var caption = TrimLikelyPropertySuffix(rawCaption);
                var trailingFlags = GetPrintableFlagSuffix(rawCaption, caption.Length);
                properties["captionRaw"] = rawCaption;
                properties["caption"] = caption;
                properties["captionOffset"] = stream.FileOffsets[captionRun.Value.Offset];
                if (captionRun.Value.Offset >= 2 && IsPrintableAscii(data[captionRun.Value.Offset - 2]))
                {
                    properties["accelerator"] = Encoding.Latin1.GetString(data, captionRun.Value.Offset - 2, 1);
                    properties["acceleratorOffset"] = stream.FileOffsets[captionRun.Value.Offset - 2];
                }

                if (!string.IsNullOrEmpty(trailingFlags))
                {
                    properties["captionTrailingFlags"] = trailingFlags;
                    properties["default"] = trailingFlags.Contains('P', StringComparison.Ordinal);
                    properties["cancel"] = trailingFlags.Contains('C', StringComparison.Ordinal);
                }

                width = dimensions.Value.First;
                height = dimensions.Value.Second;
                widthOffset = stream.FileOffsets[dimensions.Value.Offset];
                heightOffset = stream.FileOffsets[dimensions.Value.Offset + 4];
                properties["sizeSource"] = "objectStream";
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadCommandButtonObjectStream(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02)
        {
            return null;
        }

        var cbCommandButton = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        if (cbCommandButton < 4 || 4 + cbCommandButton > data.Length)
        {
            return null;
        }

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        var cursor = 8;
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsCommandButton",
            ["minorVersion"] = data[0],
            ["majorVersion"] = data[1],
            ["cbCommandButton"] = cbCommandButton,
            ["commandButtonPropMask"] = $"0x{propMask:X8}",
            ["commandButtonPropMaskOffset"] = stream.FileOffsets[4],
        };

        CountOfBytesWithCompressionFlag? captionCount = null;
        if (HasBit(propMask, 0))
        {
            ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, formatColor: true);
        }

        if (HasBit(propMask, 1))
        {
            ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);
        }

        if (HasBit(propMask, 2))
        {
            var value = ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            properties["variousPropertyBits"] = $"0x{value:X8}";
            AddVariousPropertyBits(properties, value);
        }

        if (HasBit(propMask, 3))
        {
            Align(ref cursor, 4);
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            captionCount = DecodeCountOfBytesWithCompressionFlag(raw);
            properties["captionByteCount"] = captionCount.Value.Count;
            properties["captionCompressed"] = captionCount.Value.Compressed;
            properties["captionCountOffset"] = stream.FileOffsets[cursor];
            cursor += 4;
        }

        if (HasBit(propMask, 4))
        {
            ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "picturePosition", properties);
        }

        // fSize is stored in the ExtraDataBlock, not the DataBlock.
        if (HasBit(propMask, 6))
        {
            properties["mousePointer"] = ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);
        }

        if (HasBit(propMask, 7))
        {
            ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "pictureMarker", properties);
        }

        if (HasBit(propMask, 8))
        {
            var accelerator = ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "acceleratorCodeUnit", properties);
            if (accelerator != 0)
            {
                properties["accelerator"] = char.ConvertFromUtf32(accelerator);
                properties["acceleratorOffset"] = properties["acceleratorCodeUnitOffset"];
            }
        }

        if (HasBit(propMask, 9))
        {
            properties["takeFocusOnClick"] = true;
        }

        if (HasBit(propMask, 10))
        {
            ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);
        }

        Align(ref cursor, 4);

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;
        if (captionCount is not null)
        {
            var captionOffset = cursor;
            var caption = ReadFmString(data, captionOffset, captionCount.Value);
            properties["caption"] = caption;
            properties["captionRaw"] = caption;
            properties["captionOffset"] = stream.FileOffsets[captionOffset];
            cursor += captionCount.Value.Count;
            Align(ref cursor, 4);
        }

        if (HasBit(propMask, 5))
        {
            Align(ref cursor, 4);
            if (cursor + 8 > data.Length)
            {
                return null;
            }

            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4, 4));
            widthOffset = stream.FileOffsets[cursor];
            heightOffset = stream.FileOffsets[cursor + 4];
            properties["sizeSource"] = "commandButtonExtraDataBlock";
            cursor += 8;
        }

        var colors = FindSystemColors(data, stream.FileOffsets);
        if (colors.Count > 0)
        {
            properties["systemColors"] = colors;
        }

        AddTextPropsHeuristic(data, stream.FileOffsets, properties);
        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static void AddTextPropsHeuristic(byte[] data, int[] fileOffsets, Dictionary<string, object?> properties)
    {
        var textRuns = FindTextRuns(data, minLength: 3);
        var fontRun = FindFontNameRun(data, textRuns);
        if (fontRun is null)
        {
            return;
        }

        properties["fontName"] = fontRun.Value.Text;
        properties["fontNameOffset"] = fileOffsets[fontRun.Value.Offset];
        var fontSize = FindFontSizeBefore(data, fontRun.Value.Offset);
        if (fontSize is null)
        {
            return;
        }

        properties["fontSize"] = fontSize.Value.Size;
        properties["fontSizeRaw"] = fontSize.Value.Raw;
        properties["fontSizeOffset"] = fileOffsets[fontSize.Value.Offset];
        if (fontSize.Value.Offset >= 4)
        {
            properties["fontStyleRawHex"] = Convert.ToHexString(data.AsSpan(fontSize.Value.Offset - 4, 4));
            properties["fontStyleRawOffset"] = fileOffsets[fontSize.Value.Offset - 4];
        }
    }

    private static void AddVariousPropertyBits(Dictionary<string, object?> properties, uint value)
    {
        properties["enabled"] = HasBit(value, 1);
        properties["locked"] = HasBit(value, 2);
        properties["backStyle"] = HasBit(value, 3) ? 1 : 0;
        properties["wordWrap"] = HasBit(value, 23);
        properties["autoSize"] = HasBit(value, 28);
    }

    private static uint ReadAlignedUInt32(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int alignment,
        string property,
        Dictionary<string, object?> properties,
        bool formatColor = false)
    {
        Align(ref cursor, alignment);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        properties[property] = formatColor ? $"&H{value:X8}&" : value;
        properties[$"{property}Offset"] = fileOffsets[cursor];
        cursor += 4;
        return value;
    }

    private static ushort ReadAlignedUInt16(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        string property,
        Dictionary<string, object?> properties)
    {
        Align(ref cursor, 2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        properties[property] = value;
        properties[$"{property}Offset"] = fileOffsets[cursor];
        cursor += 2;
        return value;
    }

    private static byte ReadByte(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        string property,
        Dictionary<string, object?> properties)
    {
        var value = data[cursor];
        properties[property] = value;
        properties[$"{property}Offset"] = fileOffsets[cursor];
        cursor++;
        return value;
    }

    private static void Align(ref int cursor, int alignment)
    {
        var remainder = cursor % alignment;
        if (remainder != 0)
        {
            cursor += alignment - remainder;
        }
    }

    private static bool HasBit(uint value, int bit) => (value & (1u << bit)) != 0;

    private static CountOfBytesWithCompressionFlag DecodeCountOfBytesWithCompressionFlag(uint value) =>
        new((int)(value & 0x7FFF_FFFF), (value & 0x8000_0000) != 0);

    private static string ReadFmString(byte[] data, int offset, CountOfBytesWithCompressionFlag count)
    {
        if (count.Count == 0)
        {
            return string.Empty;
        }

        if (offset + count.Count > data.Length)
        {
            return string.Empty;
        }

        var text = count.Compressed
            ? Encoding.Latin1.GetString(data, offset, count.Count)
            : Encoding.Unicode.GetString(data, offset, count.Count);
        return TrimTrailingBinaryChars(text);
    }

    private static TextRun? FindFontNameRun(byte[] data, IReadOnlyList<TextRun> textRuns)
    {
        foreach (var run in textRuns.OrderByDescending(run => run.Offset))
        {
            if (IsKnownFontName(run.Text))
            {
                return run;
            }

            if (run.Text.Contains(' ', StringComparison.Ordinal) && IsLikelyFontRun(data, run.Offset))
            {
                return run;
            }
        }

        return null;
    }

    private static bool IsKnownFontName(string value) =>
        value.Equals("Tahoma", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Trebuchet MS", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Verdana", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Terminal", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyFontRun(byte[] data, int offset)
    {
        if (offset < 8)
        {
            return false;
        }

        var sizeCandidate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset - 8, 4));
        return sizeCandidate is >= 100 and <= 720;
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

    private static FontSizeCandidate? FindFontSizeBefore(byte[] data, int fontNameOffset)
    {
        for (var offset = fontNameOffset - 4; offset >= Math.Max(0, fontNameOffset - 24); offset--)
        {
            var raw = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            if (raw is >= 100 and <= 1000)
            {
                return new FontSizeCandidate(offset, raw, Math.Round(raw / 20.0, 2));
            }
        }

        return null;
    }

    private static IReadOnlyList<SystemColorValue> FindSystemColors(byte[] data, int[] fileOffsets)
    {
        var colors = new List<SystemColorValue>();
        for (var offset = 0; offset + 4 <= data.Length; offset++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if ((value & 0xFF000000) == 0x80000000)
            {
                colors.Add(new SystemColorValue(offset, fileOffsets[offset], $"&H{value:X8}&"));
            }
        }

        return colors
            .GroupBy(color => color.Offset)
            .Select(group => group.First())
            .ToList();
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
