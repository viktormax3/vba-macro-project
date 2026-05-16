internal static class StructuredMsFormsParser
{
    private const uint AllowedSitePropMask = 0x0000_7BFF; // bits 0-9 and 11-14; bit 10 and high bits are unused

    private static int Align4(int n) => (n + 3) & ~3;

    public static IReadOnlyList<SiteDescriptor> Parse(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x04)
        {
            return [];
        }

        var cbForm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        var firstPossibleSiteData = 4 + cbForm;
        if (firstPossibleSiteData >= data.Length)
        {
            return [];
        }

        var candidates = new List<SiteDataCandidate>();

        // MS-OFORMS places StreamData between FormExtraDataBlock and FormSiteData. cbForm does
        // not include StreamData, so 4 + cbForm is only the earliest possible SiteData offset.
        // Try the documented StreamData skip first, then fall back to a validated scan. This keeps
        // Office files strict and still tolerates host-produced variants such as Corel VBA forms.
        foreach (var offset in EnumerateSiteDataOffsets(data, firstPossibleSiteData))
        {
            if (TryReadSiteDataAt(stream, offset, hasClassInfoCount: true, out var withClassInfo))
            {
                candidates.Add(withClassInfo);
            }

            if (TryReadSiteDataAt(stream, offset, hasClassInfoCount: false, out var withoutClassInfo))
            {
                candidates.Add(withoutClassInfo);
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Offset)
            .Select(c => c.Sites)
            .FirstOrDefault() ?? [];
    }

    public static void EnrichFromObjectStream(
        IReadOnlyList<SiteDescriptor> sites,
        StorageEntryDump objectStream)
    {
        var cursor = 0;
        var data = objectStream.Data;

        foreach (var site in sites)
        {
            var typeCode = GetControlTypeCode(site);
            var isParentStorage = typeCode is 0x0E or 0x39 or 0x07; // Frame, MultiPage, Page

            if (site.ObjectStreamSize is not { } size || size <= 0 || isParentStorage)
            {
                continue;
            }

            if (cursor + size > data.Length)
            {
                break;
            }

            site.ObjectStreamLocalOffset = cursor;
            site.ObjectStreamFileOffset = objectStream.FileOffsets.Length > cursor ? objectStream.FileOffsets[cursor] : 0;

            ControlTypeSchema.TryGetMsFormsType(typeCode, out var controlType);
            var subStream = StorageEntryDump.CreateSegment(
                data.AsSpan(cursor, size).ToArray(),
                objectStream.FileOffsets.Skip(cursor).Take(size).ToArray());

            site.ObjectProperties = ObjectStreamParser.Read(subStream, controlType);
            cursor += size;
        }
    }

    private static IEnumerable<int> EnumerateSiteDataOffsets(byte[] data, int firstPossibleSiteData)
    {
        var yielded = new HashSet<int>();
        foreach (var offset in EnumerateLikelySiteDataOffsets(data, firstPossibleSiteData))
        {
            if (offset >= firstPossibleSiteData && offset < data.Length && yielded.Add(offset))
            {
                yield return offset;
            }
        }

        // Bounded validated scan. It is intentionally conservative: FormStreamData is usually small
        // (font/picture metadata), but real projects can include additional persisted data.
        var scanEnd = Math.Min(data.Length - 12, firstPossibleSiteData + 512);
        for (var offset = firstPossibleSiteData; offset <= scanEnd; offset++)
        {
            if (yielded.Add(offset))
            {
                yield return offset;
            }
        }
    }

    private static IEnumerable<int> EnumerateLikelySiteDataOffsets(byte[] data, int firstPossibleSiteData)
    {
        yield return firstPossibleSiteData;

        var formPropMask = data.Length >= 8
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4))
            : 0;

        var cursor = firstPossibleSiteData;

        // FormPropMask bit T / index 19 = fFont. When set, a GuidAndFont is persisted in StreamData.
        if (MsFormsBinary.HasBit(formPropMask, 19) && TrySkipGuidAndFont(data, cursor, out var afterFont))
        {
            yield return afterFont;
            cursor = afterFont;
        }

        // Pictures are not fully implemented here. Keep the validated scan as the compatibility path.
        yield return cursor;
    }

    private static bool TrySkipGuidAndFont(byte[] data, int offset, out int end)
    {
        end = offset;
        if (offset + 16 + 11 > data.Length)
        {
            return false;
        }

        var cursor = offset + 16; // GUID
        if (data[cursor] != 0x01)
        {
            return false;
        }

        cursor += 1; // Version
        cursor += 2; // sCharset
        cursor += 1; // bFlags
        cursor += 2; // sWeight
        cursor += 4; // ulHeight

        if (cursor >= data.Length)
        {
            return false;
        }

        var faceLen = data[cursor++];
        if (cursor + faceLen > data.Length)
        {
            return false;
        }

        cursor += faceLen;
        end = cursor;
        return true;
    }

    private static bool TryReadSiteDataAt(
        StorageEntryDump stream,
        int offset,
        bool hasClassInfoCount,
        out SiteDataCandidate candidate)
    {
        candidate = default!;
        var data = stream.Data;
        var cursor = offset;

        if (cursor < 0 || cursor + 8 > data.Length)
        {
            return false;
        }

        var classInfoCount = 0;
        if (hasClassInfoCount)
        {
            if (cursor + 2 > data.Length)
            {
                return false;
            }

            classInfoCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            if (classInfoCount is < 0 or > 128)
            {
                return false;
            }

            cursor += 2;
            for (var i = 0; i < classInfoCount; i++)
            {
                if (cursor + 8 > data.Length)
                {
                    return false;
                }

                var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
                var cbClassTable = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor + 2, 2));
                if (version != 0 || cbClassTable < 4)
                {
                    return false;
                }

                var end = cursor + 4 + cbClassTable;
                if (end > data.Length)
                {
                    return false;
                }

                cursor = end;
            }
        }

        if (cursor + 8 > data.Length)
        {
            return false;
        }

        var countOfSitesRaw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        var countOfBytesRaw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4, 4));
        if (countOfSitesRaw == 0 || countOfSitesRaw > 512 || countOfBytesRaw < 4 || countOfBytesRaw > data.Length)
        {
            return false;
        }

        var countOfSites = (int)countOfSitesRaw;
        var countOfBytes = (int)countOfBytesRaw;
        cursor += 8;

        var depthStart = cursor;
        if (!TryReadDepths(data, ref cursor, countOfSites, out var depths))
        {
            return false;
        }

        AlignRelative(ref cursor, depthStart, 4);
        var siteDataEnd = depthStart + countOfBytes;
        if (siteDataEnd > data.Length || cursor > siteDataEnd)
        {
            return false;
        }

        var sites = new List<SiteDescriptor>(countOfSites);
        var siteCursor = cursor;
        var parserKind = hasClassInfoCount ? "formSiteData" : "formSiteDataNoClassCount";
        for (var i = 0; i < countOfSites; i++)
        {
            var site = ReadSite(stream, ref siteCursor, depths[i], i, siteDataEnd, parserKind);
            if (site is null)
            {
                return false;
            }

            sites.Add(site);
        }

        if (siteCursor > siteDataEnd)
        {
            return false;
        }

        var leftover = siteDataEnd - siteCursor;
        var knownTypes = sites.Count(s => ControlTypeSchema.TryGetMsFormsType(GetControlTypeCode(s), out _));
        var namedSites = sites.Count(s => !string.IsNullOrWhiteSpace(s.Name));
        var plausiblePositions = sites.Count(s => IsPlausiblePosition(s.Left) && IsPlausiblePosition(s.Top));
        var exactBonus = leftover == 0 ? 500 : 0;
        var classBonus = hasClassInfoCount ? 25 : 0;
        var score = exactBonus + classBonus + knownTypes * 80 + namedSites * 40 + plausiblePositions * 20 - leftover - offset;

        candidate = new SiteDataCandidate(offset, siteDataEnd, score, sites);
        return namedSites == countOfSites && knownTypes > 0;
    }

    private static bool TryReadDepths(byte[] data, ref int cursor, int count, out List<SiteDepthType> depths)
    {
        depths = new List<SiteDepthType>(count);
        while (depths.Count < count && cursor + 2 <= data.Length)
        {
            var depth = data[cursor++];
            var typeOrCountByte = data[cursor++];
            var typeOrCount = (byte)(typeOrCountByte & 0x7F);
            var isCount = (typeOrCountByte & 0x80) != 0;

            if (isCount)
            {
                if (cursor >= data.Length || typeOrCount == 0)
                {
                    return false;
                }

                var siteType = data[cursor++];
                if (siteType != 0x01)
                {
                    return false;
                }

                for (var i = 0; i < typeOrCount && depths.Count < count; i++)
                {
                    depths.Add(new SiteDepthType(depth, siteType));
                }
            }
            else
            {
                if (typeOrCount != 0x01)
                {
                    return false;
                }

                depths.Add(new SiteDepthType(depth, typeOrCount));
            }
        }

        return depths.Count == count;
    }

    private static SiteDescriptor? ReadSite(
        StorageEntryDump stream,
        ref int cursor,
        SiteDepthType depth,
        int index,
        int siteDataEnd,
        string parserKind)
    {
        var data = stream.Data;
        var siteStart = cursor;
        if (siteStart + 8 > data.Length || siteStart + 8 > siteDataEnd)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteStart, 2));
        var cbSite = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteStart + 2, 2));
        if (version != 0 || cbSite < 4)
        {
            return null;
        }

        var siteEnd = siteStart + 4 + cbSite;
        if (siteEnd > data.Length || siteEnd > siteDataEnd)
        {
            return null;
        }

        var propMaskValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(siteStart + 4, 4));
        if ((propMaskValue & ~AllowedSitePropMask) != 0)
        {
            return null;
        }

        var mask = new SitePropMask(propMaskValue);
        var site = new SiteDescriptor
        {
            SiteIndex = index,
            Depth = depth.Depth,
            SiteType = depth.SiteType,
            PropMask = propMaskValue,
            StreamStart = siteStart,
            StreamEnd = siteEnd,
        };
        site.ExtraProperties["siteParser"] = "msOFormsOleSiteConcrete";
        site.ExtraProperties["siteDataParser"] = parserKind;
        site.ExtraProperties["cbSite"] = cbSite;
        site.ExtraProperties["sitePropMaskOffset"] = FileOffset(stream, siteStart + 4);

        var dataBlockStart = siteStart + 8;
        var dataCursor = dataBlockStart;
        var dataBlock = new InternalSiteDataBlock();

        if (mask.HasName)
        {
            dataBlock.NameCountOffset = dataCursor;
            dataBlock.NameCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
            AddCountProperties(site, stream, "siteName", dataBlock.NameCount, dataBlock.NameCountOffset);
        }
        if (mask.HasTag)
        {
            dataBlock.TagCountOffset = dataCursor;
            dataBlock.TagCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
            AddCountProperties(site, stream, "siteTag", dataBlock.TagCount, dataBlock.TagCountOffset);
        }
        if (mask.HasID)
        {
            site.IdOffset = dataCursor;
            site.Id = ReadUInt32(data, ref dataCursor, dataBlockStart, siteEnd);
            site.ExtraProperties["siteId"] = site.Id;
            site.ExtraProperties["siteIdOffset"] = FileOffset(stream, site.IdOffset);
        }
        if (mask.HasHelpContextID)
        {
            site.HelpContextIdOffset = dataCursor;
            site.HelpContextId = ReadUInt32(data, ref dataCursor, dataBlockStart, siteEnd);
        }
        if (mask.HasBitFlags)
        {
            site.BitFlagsOffset = dataCursor;
            site.BitFlags = ReadUInt32(data, ref dataCursor, dataBlockStart, siteEnd);
            site.ExtraProperties["siteBitFlagsRaw"] = site.BitFlags;
            site.ExtraProperties["siteBitFlagsRawOffset"] = FileOffset(stream, site.BitFlagsOffset);
        }
        if (mask.HasObjectStreamSize)
        {
            site.ObjectStreamSizeOffset = dataCursor;
            site.ObjectStreamSize = (int)ReadUInt32(data, ref dataCursor, dataBlockStart, siteEnd);
            site.ExtraProperties["siteObjectStreamSize"] = site.ObjectStreamSize;
            site.ExtraProperties["siteObjectStreamSizeOffset"] = FileOffset(stream, site.ObjectStreamSizeOffset);
        }
        if (mask.HasTabIndex)
        {
            site.TabIndexOffset = dataCursor;
            site.TabIndex = ReadUInt16(data, ref dataCursor, dataBlockStart, siteEnd);
            site.ExtraProperties["tabIndexOffset"] = FileOffset(stream, site.TabIndexOffset);
        }
        if (mask.HasClsidCacheIndex)
        {
            site.ClsidCacheIndexOffset = dataCursor;
            site.ClsidCacheIndex = ReadUInt16(data, ref dataCursor, dataBlockStart, siteEnd);
            site.ExtraProperties["clsidCacheIndexOffset"] = FileOffset(stream, site.ClsidCacheIndexOffset);
        }
        if (mask.HasGroupID)
        {
            site.GroupIdOffset = dataCursor;
            site.GroupId = ReadUInt16(data, ref dataCursor, dataBlockStart, siteEnd);
        }

        if (mask.HasControlTipText)
        {
            AlignRelative(ref dataCursor, dataBlockStart, 4);
            dataBlock.ControlTipCountOffset = dataCursor;
            dataBlock.ControlTipCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
            AddCountProperties(site, stream, "siteControlTipText", dataBlock.ControlTipCount, dataBlock.ControlTipCountOffset);
        }
        if (mask.HasRuntimeLicKey)
        {
            AlignRelative(ref dataCursor, dataBlockStart, 4);
            dataBlock.RuntimeLicKeyCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
        }
        if (mask.HasControlSource)
        {
            AlignRelative(ref dataCursor, dataBlockStart, 4);
            dataBlock.ControlSourceCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
        }
        if (mask.HasRowSource)
        {
            AlignRelative(ref dataCursor, dataBlockStart, 4);
            dataBlock.RowSourceCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
        }

        AlignRelative(ref dataCursor, dataBlockStart, 4);
        if (dataCursor > siteEnd)
        {
            return null;
        }

        var extraCursor = dataCursor;

        if (mask.HasName)
        {
            site.NameOffset = extraCursor;
            site.Name = ReadFmString(data, extraCursor, dataBlock.NameCount, siteEnd);
            site.ExtraProperties["name"] = site.Name;
            site.ExtraProperties["nameRaw"] = site.Name;
            site.ExtraProperties["nameOffset"] = FileOffset(stream, site.NameOffset);
            extraCursor += Align4(dataBlock.NameCount.Count);
        }

        if (mask.HasTag)
        {
            site.TagOffset = extraCursor;
            site.Tag = ReadFmString(data, extraCursor, dataBlock.TagCount, siteEnd);
            site.ExtraProperties["tag"] = site.Tag;
            site.ExtraProperties["tagOffset"] = FileOffset(stream, site.TagOffset);
            extraCursor += Align4(dataBlock.TagCount.Count);
        }

        if (mask.HasPosition)
        {
            if (extraCursor + 8 <= siteEnd)
            {
                site.LeftOffset = extraCursor;
                site.Left = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor, 4));
                site.TopOffset = extraCursor + 4;
                site.Top = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor + 4, 4));
                site.ExtraProperties["positionSource"] = "oleSiteConcrete";
                site.ExtraProperties["leftOffset"] = FileOffset(stream, site.LeftOffset);
                site.ExtraProperties["topOffset"] = FileOffset(stream, site.TopOffset);
                extraCursor += 8;
            }
        }

        if (mask.HasControlTipText)
        {
            site.ExtraProperties["controlTipText"] = ReadFmString(data, extraCursor, dataBlock.ControlTipCount, siteEnd);
            site.ExtraProperties["controlTipTextOffset"] = FileOffset(stream, extraCursor);
            extraCursor += Align4(dataBlock.ControlTipCount.Count);
        }

        if (mask.HasRuntimeLicKey)
        {
            site.ExtraProperties["runtimeLicKey"] = ReadFmString(data, extraCursor, dataBlock.RuntimeLicKeyCount, siteEnd);
            extraCursor += Align4(dataBlock.RuntimeLicKeyCount.Count);
        }

        if (mask.HasControlSource)
        {
            site.ExtraProperties["controlSource"] = ReadFmString(data, extraCursor, dataBlock.ControlSourceCount, siteEnd);
            extraCursor += Align4(dataBlock.ControlSourceCount.Count);
        }

        if (mask.HasRowSource)
        {
            site.ExtraProperties["rowSource"] = ReadFmString(data, extraCursor, dataBlock.RowSourceCount, siteEnd);
            extraCursor += Align4(dataBlock.RowSourceCount.Count);
        }

        if (extraCursor > siteEnd)
        {
            return null;
        }

        cursor = siteEnd;
        return site;
    }

    private static void AddCountProperties(
        SiteDescriptor site,
        StorageEntryDump stream,
        string prefix,
        CountOfBytesWithCompressionFlag count,
        int offset)
    {
        site.ExtraProperties[$"{prefix}CountRaw"] = (uint)count.Count | (count.Compressed ? 0x8000_0000u : 0u);
        site.ExtraProperties[$"{prefix}CountRawOffset"] = FileOffset(stream, offset);
        site.ExtraProperties[$"{prefix}ByteCount"] = count.Count;
        site.ExtraProperties[$"{prefix}Compressed"] = count.Compressed;
    }

    private static CountOfBytesWithCompressionFlag ReadCount(byte[] data, ref int cursor, int blockStart, int siteEnd)
    {
        var raw = ReadUInt32(data, ref cursor, blockStart, siteEnd);
        return MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(raw);
    }

    private static uint ReadUInt32(byte[] data, ref int cursor, int blockStart, int siteEnd)
    {
        AlignRelative(ref cursor, blockStart, 4);
        if (cursor + 4 > siteEnd)
        {
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        return value;
    }

    private static ushort ReadUInt16(byte[] data, ref int cursor, int blockStart, int siteEnd)
    {
        AlignRelative(ref cursor, blockStart, 2);
        if (cursor + 2 > siteEnd)
        {
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
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

    private static string ReadFmString(byte[] data, int offset, CountOfBytesWithCompressionFlag count, int siteEnd)
    {
        if (count.Count == 0)
        {
            return string.Empty;
        }

        if (offset + count.Count > siteEnd || offset + count.Count > data.Length)
        {
            return string.Empty;
        }

        var value = count.Compressed
            ? Encoding.Latin1.GetString(data, offset, count.Count)
            : Encoding.Unicode.GetString(data, offset, count.Count);
        return MsFormsBinary.TrimTrailingBinaryChars(value);
    }

    private static byte GetControlTypeCode(SiteDescriptor site) =>
        (byte)((site.ClsidCacheIndex ?? 0) & 0x00FF);

    private static int FileOffset(StorageEntryDump stream, int localOffset) =>
        localOffset >= 0 && localOffset < stream.FileOffsets.Length ? stream.FileOffsets[localOffset] : 0;

    private static bool IsPlausiblePosition(int? value) =>
        value is >= -200_000 and <= 200_000;

    private readonly record struct SiteDepthType(byte Depth, byte SiteType);

    private sealed record SiteDataCandidate(
        int Offset,
        int EndOffset,
        int Score,
        IReadOnlyList<SiteDescriptor> Sites);

    private sealed class InternalSiteDataBlock
    {
        public CountOfBytesWithCompressionFlag NameCount { get; set; }
        public int NameCountOffset { get; set; }
        public CountOfBytesWithCompressionFlag TagCount { get; set; }
        public int TagCountOffset { get; set; }
        public CountOfBytesWithCompressionFlag ControlTipCount { get; set; }
        public int ControlTipCountOffset { get; set; }
        public CountOfBytesWithCompressionFlag RuntimeLicKeyCount { get; set; }
        public CountOfBytesWithCompressionFlag ControlSourceCount { get; set; }
        public CountOfBytesWithCompressionFlag RowSourceCount { get; set; }
    }
}

internal readonly record struct SitePropMask(uint Value)
{
    public bool HasName => MsFormsBinary.HasBit(Value, 0);
    public bool HasTag => MsFormsBinary.HasBit(Value, 1);
    public bool HasID => MsFormsBinary.HasBit(Value, 2);
    public bool HasHelpContextID => MsFormsBinary.HasBit(Value, 3);
    public bool HasBitFlags => MsFormsBinary.HasBit(Value, 4);
    public bool HasObjectStreamSize => MsFormsBinary.HasBit(Value, 5);
    public bool HasTabIndex => MsFormsBinary.HasBit(Value, 6);
    public bool HasClsidCacheIndex => MsFormsBinary.HasBit(Value, 7);
    public bool HasPosition => MsFormsBinary.HasBit(Value, 8);
    public bool HasGroupID => MsFormsBinary.HasBit(Value, 9);
    public bool HasControlTipText => MsFormsBinary.HasBit(Value, 11);
    public bool HasRuntimeLicKey => MsFormsBinary.HasBit(Value, 12);
    public bool HasControlSource => MsFormsBinary.HasBit(Value, 13);
    public bool HasRowSource => MsFormsBinary.HasBit(Value, 14);
}
