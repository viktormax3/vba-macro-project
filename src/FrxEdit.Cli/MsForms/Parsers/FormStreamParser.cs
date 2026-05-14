internal static class FormStreamParser
{
    private const uint AllowedSitePropMask = 0x0000_7BFF;

    public static IReadOnlyList<StructuredControlRecord> Read(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames)
    {
        var siteRecords = TryReadSiteData(stream, knownControlNames);
        return siteRecords.Count > 0
            ? siteRecords
            : ReadByStructuredMarkers(stream, knownControlNames);
    }

    private static IReadOnlyList<StructuredControlRecord> TryReadSiteData(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames)
    {
        var data = stream.Data;
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x04)
        {
            return [];
        }

        var cbForm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        var siteDataOffset = 4 + cbForm;
        if (siteDataOffset + 10 > data.Length)
        {
            return [];
        }

        return TryReadSiteDataAt(stream, knownControlNames, siteDataOffset, hasClassInfoCount: true)
            is { Count: > 0 } withClassInfo
                ? withClassInfo
                : TryReadSiteDataAt(stream, knownControlNames, siteDataOffset, hasClassInfoCount: false);
    }

    private static IReadOnlyList<StructuredControlRecord> TryReadSiteDataAt(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames,
        int siteDataOffset,
        bool hasClassInfoCount)
    {
        var data = stream.Data;
        var cursor = siteDataOffset;
        var propertiesPrefix = hasClassInfoCount ? "formSiteData" : "formSiteDataNoClassCount";

        if (hasClassInfoCount)
        {
            if (cursor + 2 > data.Length)
            {
                return [];
            }

            var classInfoCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            for (var i = 0; i < classInfoCount; i++)
            {
                if (cursor + 4 > data.Length)
                {
                    return [];
                }

                var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
                var cbClassTable = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor + 2, 2));
                if (version != 0 || cbClassTable < 4 || cursor + 4 + cbClassTable > data.Length)
                {
                    return [];
                }

                cursor += 4 + cbClassTable;
            }
        }

        if (cursor + 8 > data.Length)
        {
            return [];
        }

        var countOfSites = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        var countOfBytes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4, 4));
        if (countOfSites is 0 or > 2048 || countOfBytes > data.Length - cursor)
        {
            return [];
        }

        cursor += 8;
        var depthStart = cursor;
        var depths = new List<SiteDepthType>((int)countOfSites);
        while (depths.Count < countOfSites && cursor + 2 <= data.Length)
        {
            var depth = data[cursor];
            var typeOrCount = (byte)(data[cursor + 1] & 0x7F);
            var isCount = (data[cursor + 1] & 0x80) != 0;
            cursor += 2;

            if (isCount)
            {
                if (cursor >= data.Length || typeOrCount == 0)
                {
                    return [];
                }

                var siteType = data[cursor++];
                for (var i = 0; i < typeOrCount && depths.Count < countOfSites; i++)
                {
                    depths.Add(new SiteDepthType(depth, siteType));
                }
            }
            else
            {
                depths.Add(new SiteDepthType(depth, typeOrCount));
            }
        }

        if (depths.Count != countOfSites)
        {
            return [];
        }

        var depthBytes = cursor - depthStart;
        while (depthBytes % 4 != 0 && cursor < data.Length)
        {
            cursor++;
            depthBytes++;
        }

        var records = new List<StructuredControlRecord>((int)countOfSites);
        for (var i = 0; i < depths.Count; i++)
        {
            var siteOffset = cursor;
            var site = TryReadOleSiteConcrete(stream, siteOffset, knownControlNames, depths[i], i, propertiesPrefix);
            if (site is null)
            {
                return [];
            }

            records.Add(site);
            cursor = site.RecordEndOffset;
        }

        return records;
    }

    private static StructuredControlRecord? TryReadOleSiteConcrete(
        StorageEntryDump stream,
        int siteOffset,
        IReadOnlySet<string>? knownControlNames,
        SiteDepthType depth,
        int siteIndex,
        string parserName)
    {
        var data = stream.Data;
        if (siteOffset + 8 > data.Length)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteOffset, 2));
        var cbSite = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteOffset + 2, 2));
        var siteEnd = siteOffset + 4 + cbSite;
        if (version != 0 || cbSite < 4 || siteEnd > data.Length)
        {
            return null;
        }

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(siteOffset + 4, 4));
        if ((propMask & ~AllowedSitePropMask) != 0)
        {
            return null;
        }

        var dataBlockStart = siteOffset + 8;
        var cursor = dataBlockStart;
        CountOfBytesWithCompressionFlag? nameCount = null;
        CountOfBytesWithCompressionFlag? tagCount = null;
        CountOfBytesWithCompressionFlag? controlTipCount = null;
        CountOfBytesWithCompressionFlag? runtimeLicKeyCount = null;
        CountOfBytesWithCompressionFlag? controlSourceCount = null;
        CountOfBytesWithCompressionFlag? rowSourceCount = null;
        ushort? tabIndex = null;
        ushort? clsidCacheIndex = null;
        int? objectStreamSize = null;
        int? left = null;
        int? top = null;
        int? leftOffset = null;
        int? topOffset = null;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["siteParser"] = "msOFormsOleSiteConcrete",
            ["siteDataParser"] = parserName,
            ["siteIndex"] = siteIndex,
            ["siteDepth"] = depth.Depth,
            ["siteType"] = depth.SiteType,
            ["siteOffset"] = stream.FileOffsets[siteOffset],
            ["siteLocalOffset"] = siteOffset,
            ["cbSite"] = cbSite,
            ["sitePropMask"] = $"0x{propMask:X8}",
            ["sitePropMaskOffset"] = stream.FileOffsets[siteOffset + 4],
        };

        if (MsFormsBinary.HasBit(propMask, 0))
        {
            nameCount = ReadCount(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteName", properties);
            properties["siteNameByteCount"] = nameCount.Value.Count;
            properties["siteNameCompressed"] = nameCount.Value.Compressed;
        }

        if (MsFormsBinary.HasBit(propMask, 1))
        {
            tagCount = ReadCount(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteTag", properties);
            properties["siteTagByteCount"] = tagCount.Value.Count;
            properties["siteTagCompressed"] = tagCount.Value.Compressed;
        }

        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var id = ReadSiteUInt32(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteId", properties);
            properties["siteId"] = (int)id;
        }

        if (MsFormsBinary.HasBit(propMask, 3))
        {
            var help = ReadSiteUInt32(data, stream.FileOffsets, ref cursor, dataBlockStart, "helpContextID", properties);
            properties["helpContextID"] = (int)help;
        }

        if (MsFormsBinary.HasBit(propMask, 4))
        {
            var bitFlags = ReadSiteUInt32(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteBitFlagsRaw", properties);
            properties["siteBitFlags"] = $"0x{bitFlags:X8}";
            AddSiteFlags(properties, bitFlags);
        }

        if (MsFormsBinary.HasBit(propMask, 5))
        {
            objectStreamSize = (int)ReadSiteUInt32(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteObjectStreamSize", properties);
            properties["objectStreamSizeFromSite"] = objectStreamSize;
        }

        if (MsFormsBinary.HasBit(propMask, 6))
        {
            tabIndex = ReadSiteUInt16(data, stream.FileOffsets, ref cursor, dataBlockStart, "tabIndex", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 7))
        {
            clsidCacheIndex = ReadSiteUInt16(data, stream.FileOffsets, ref cursor, dataBlockStart, "clsidCacheIndex", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 9))
        {
            ReadSiteUInt16(data, stream.FileOffsets, ref cursor, dataBlockStart, "groupId", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 11))
        {
            controlTipCount = ReadCount(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteControlTipText", properties);
            properties["controlTipTextByteCount"] = controlTipCount.Value.Count;
            properties["controlTipTextCompressed"] = controlTipCount.Value.Compressed;
        }

        if (MsFormsBinary.HasBit(propMask, 12))
        {
            runtimeLicKeyCount = ReadCount(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteRuntimeLicKey", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 13))
        {
            controlSourceCount = ReadCount(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteControlSource", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 14))
        {
            rowSourceCount = ReadCount(data, stream.FileOffsets, ref cursor, dataBlockStart, "siteRowSource", properties);
        }

        if (cursor > siteEnd)
        {
            return null;
        }

        var extraCursor = cursor;
        string? rawName = null;
        var nameOffset = siteOffset;
        if (nameCount is not null)
        {
            if (!TryReadExtraString(data, stream.FileOffsets, ref extraCursor, siteEnd, nameCount.Value, "name", properties, out rawName))
            {
                return null;
            }

            nameOffset = GetInt(properties, "nameOffset") ?? siteOffset;
        }

        if (tagCount is not null)
        {
            if (!TryReadExtraString(data, stream.FileOffsets, ref extraCursor, siteEnd, tagCount.Value, "tag", properties, out var tag))
            {
                return null;
            }

            properties["tag"] = tag;
        }

        if (MsFormsBinary.HasBit(propMask, 8))
        {
            extraCursor = SkipZeroBytes(data, extraCursor);
            MsFormsBinary.Align(ref extraCursor, 2);
            if (extraCursor + 8 > siteEnd)
            {
                return null;
            }

            left = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor, 4));
            top = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor + 4, 4));
            leftOffset = stream.FileOffsets[extraCursor];
            topOffset = stream.FileOffsets[extraCursor + 4];
            properties["positionSource"] = "oleSiteConcrete";
            properties["leftOffset"] = leftOffset;
            properties["topOffset"] = topOffset;
            extraCursor += 8;
        }

        if (controlTipCount is not null)
        {
            if (!TryReadExtraString(data, stream.FileOffsets, ref extraCursor, siteEnd, controlTipCount.Value, "controlTipText", properties, out var controlTipText))
            {
                return null;
            }

            properties["controlTipText"] = controlTipText;
        }

        if (runtimeLicKeyCount is not null)
        {
            _ = TryReadExtraString(data, stream.FileOffsets, ref extraCursor, siteEnd, runtimeLicKeyCount.Value, "runtimeLicKey", properties, out _);
        }

        if (controlSourceCount is not null)
        {
            _ = TryReadExtraString(data, stream.FileOffsets, ref extraCursor, siteEnd, controlSourceCount.Value, "controlSource", properties, out _);
        }

        if (rowSourceCount is not null)
        {
            _ = TryReadExtraString(data, stream.FileOffsets, ref extraCursor, siteEnd, rowSourceCount.Value, "rowSource", properties, out _);
        }

        if (rawName is null ||
            left is null ||
            top is null ||
            leftOffset is null ||
            topOffset is null ||
            clsidCacheIndex is null ||
            !ControlTypeSchema.TryGetMsFormsType((byte)(clsidCacheIndex.Value & 0xFF), out var type))
        {
            return null;
        }

        var name = NormalizeStructuredName(rawName, knownControlNames);
        var marker = new ControlTypeMarker(
            GetLocalOffset(stream.FileOffsets, GetInt(properties, "tabIndexOffset") ?? stream.FileOffsets[siteOffset]) ?? siteOffset,
            (byte)(tabIndex ?? 0),
            (byte)(clsidCacheIndex.Value & 0xFF));
        var placement = new Placement(left.Value, top.Value, null, null, leftOffset.Value, topOffset.Value, null, null);
        properties["parser"] = "msOFormsFormSiteData";

        return new StructuredControlRecord(
            stream,
            marker,
            siteOffset,
            siteEnd,
            GetLocalOffset(stream.FileOffsets, nameOffset) ?? siteOffset,
            rawName.Length,
            rawName,
            name,
            type,
            placement,
            properties,
            ObjectStream: null);
    }

    private static IReadOnlyList<StructuredControlRecord> ReadByStructuredMarkers(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames)
    {
        var data = stream.Data;
        var candidates = new List<StructuredControlCandidate>();
        for (var textOffset = 4; textOffset < data.Length; textOffset++)
        {
            if (!MsFormsBinary.IsPrintableAscii(data[textOffset]) ||
                textOffset > 0 && IsIdentifierPart(data[textOffset - 1]))
            {
                continue;
            }

            var marker = FindStructuredMarkerBefore(data, textOffset);
            if (marker is null || !ControlTypeSchema.TryGetMsFormsType(marker.TypeCode, out var type))
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
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["parser"] = "structuredStorageFStream"
                },
                ObjectStream: null));
        }

        return records;
    }

    private static CountOfBytesWithCompressionFlag ReadCount(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int blockStart,
        string property,
        Dictionary<string, object?> properties)
    {
        var raw = ReadSiteUInt32(data, fileOffsets, ref cursor, blockStart, $"{property}CountRaw", properties);
        return MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(raw);
    }

    private static bool TryReadExtraString(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int siteEnd,
        CountOfBytesWithCompressionFlag count,
        string property,
        Dictionary<string, object?> properties,
        out string value)
    {
        value = string.Empty;
        cursor = SkipZeroBytes(data, cursor);
        var offset = cursor;
        if (offset < 0 || offset >= fileOffsets.Length || offset + count.Count > siteEnd)
        {
            return false;
        }

        value = MsFormsBinary.ReadFmString(data, offset, count);
        properties[property] = value;
        properties[$"{property}Raw"] = value;
        properties[$"{property}Offset"] = fileOffsets[offset];
        cursor += count.Count;
        return true;
    }

    private static uint ReadSiteUInt32(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int blockStart,
        string property,
        Dictionary<string, object?> properties)
    {
        AlignRelative(ref cursor, blockStart, 4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        properties[property] = value;
        properties[$"{property}Offset"] = fileOffsets[cursor];
        cursor += 4;
        return value;
    }

    private static ushort ReadSiteUInt16(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int blockStart,
        string property,
        Dictionary<string, object?> properties)
    {
        AlignRelative(ref cursor, blockStart, 2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        properties[property] = value;
        properties[$"{property}Offset"] = fileOffsets[cursor];
        cursor += 2;
        return value;
    }

    private static void AlignRelative(ref int cursor, int blockStart, int alignment)
    {
        var remainder = (cursor - blockStart) % alignment;
        if (remainder != 0)
        {
            cursor += alignment - remainder;
        }
    }

    private static void AddSiteFlags(Dictionary<string, object?> properties, uint value)
    {
        properties["tabStop"] = MsFormsBinary.HasBit(value, 0);
        properties["visible"] = MsFormsBinary.HasBit(value, 1);
        properties["default"] = MsFormsBinary.HasBit(value, 2);
        properties["cancel"] = MsFormsBinary.HasBit(value, 3);
        properties["streamed"] = MsFormsBinary.HasBit(value, 4);
        properties["siteAutoSize"] = MsFormsBinary.HasBit(value, 5);
        properties["fitToParent"] = MsFormsBinary.HasBit(value, 9);
        properties["selectChild"] = MsFormsBinary.HasBit(value, 13);
        properties["promoteControls"] = MsFormsBinary.HasBit(value, 18);
    }

    private static int? GetInt(Dictionary<string, object?> properties, string property) =>
        properties.TryGetValue(property, out var value) && value is int result ? result : null;

    private static int? GetLocalOffset(int[] fileOffsets, int fileOffset)
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
                ControlTypeSchema.TryGetMsFormsType(typeCode, out _))
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

    private static Placement? TryReadStreamPlacement(byte[] data, int[] fileOffsets, int afterNameOffset)
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
            if (IsPlausiblePosition(left) && IsPlausiblePosition(top))
            {
                return new Placement(left, top, null, null, fileOffsets[offset], fileOffsets[offset + 4], null, null);
            }
        }

        return null;
    }

    private static string NormalizeStructuredName(string binaryName, IReadOnlySet<string>? knownControlNames)
    {
        if (knownControlNames is not null && knownControlNames.Contains(binaryName))
        {
            return binaryName;
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

        return end - offset < 3 ? null : Encoding.Latin1.GetString(data, offset, end - offset);
    }

    private static int SkipZeroBytes(byte[] data, int offset)
    {
        while (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }

        return offset;
    }

    private static bool IsIdentifierStart(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ||
        value is >= (byte)'a' and <= (byte)'z' ||
        value == (byte)'_';

    private static bool IsIdentifierPart(byte value) =>
        IsIdentifierStart(value) ||
        value is >= (byte)'0' and <= (byte)'9';

    private static bool IsKnownNonControlIdentifier(string name) =>
        name.Equals("Tahoma", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Forms", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Object", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlausiblePosition(int value) => value is >= 0 and <= 40_000;

    private readonly record struct SiteDepthType(byte Depth, byte SiteType);
}
