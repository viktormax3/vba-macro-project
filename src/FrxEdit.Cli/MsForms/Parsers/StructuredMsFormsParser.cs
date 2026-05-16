
internal static class StructuredMsFormsParser
{
    private const int MaxSiteScanBytes = 1024;
    private static readonly HashSet<ushort> KnownMsFormsTypeCodes = [
        0x07, 0x0C, 0x0E, 0x10, 0x11, 0x12, 0x15, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x2F, 0x39
    ];

    public static IReadOnlyList<SiteDescriptor> Parse(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x04)
        {
            return [];
        }

        var cbForm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        var firstPossibleSiteDataOffset = 4 + cbForm;
        if (firstPossibleSiteDataOffset < 8 || firstPossibleSiteDataOffset >= data.Length)
        {
            return [];
        }

        var candidates = new List<FormSiteDataCandidate>();
        var scanEnd = Math.Min(data.Length - 12, firstPossibleSiteDataOffset + MaxSiteScanBytes);
        for (var offset = firstPossibleSiteDataOffset; offset <= scanEnd; offset++)
        {
            if (TryReadFormSiteData(stream, offset, hasClassInfoCount: true, out var withClassTable))
            {
                candidates.Add(withClassTable);
            }

            if (TryReadFormSiteData(stream, offset, hasClassInfoCount: false, out var withoutClassTable))
            {
                candidates.Add(withoutClassTable);
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var best = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Offset)
            .First();

        foreach (var site in best.Sites)
        {
            site.ExtraProperties["siteDataParser"] = best.HasClassInfoCount
                ? "formSiteData"
                : "formSiteDataNoClassCount";
            site.ExtraProperties["siteDataOffset"] = best.Offset;
            site.ExtraProperties["siteDataLocalOffset"] = best.Offset;
            site.ExtraProperties["siteDataCountOfSites"] = best.CountOfSites;
            site.ExtraProperties["siteDataCountOfBytes"] = best.CountOfBytes;
            site.ExtraProperties["siteDataEndOffset"] = best.EndOffset;
            if (best.ClassTable.Count > 0)
            {
                site.ExtraProperties["classTableCount"] = best.ClassTable.Count;
            }
            if (stream.FileOffsets.Length == stream.Data.Length)
            {
                site.ExtraProperties["siteDataFileOffset"] = stream.FileOffsets[best.Offset];
                site.ExtraProperties["siteDataEndFileOffset"] = best.EndOffset < stream.FileOffsets.Length
                    ? stream.FileOffsets[best.EndOffset]
                    : stream.FileOffsets[^1] + 1;
            }
        }

        return best.Sites;
    }

    public static void EnrichFromObjectStream(
        IReadOnlyList<SiteDescriptor> sites,
        StorageEntryDump objectStream)
    {
        var cursor = 0;
        var data = objectStream.Data;

        foreach (var site in sites)
        {
            site.ControlType = ResolveControlType(site.ClsidCacheIndex);
            var isParentStorage = IsParentStorageControl(site.ControlType);

            if (site.ObjectStreamSize is not { } size || size <= 0 || isParentStorage)
            {
                continue;
            }

            if (cursor + size > data.Length)
            {
                site.ExtraProperties["objectStreamError"] = $"ObjectStreamSize {size} at {cursor} exceeds stream length {data.Length}.";
                break;
            }

            site.ObjectStreamLocalOffset = cursor;
            site.ObjectStreamFileOffset = objectStream.FileOffsets.Length > cursor ? objectStream.FileOffsets[cursor] : 0;

            var subStream = StorageEntryDump.CreateSegment(
                data.AsSpan(cursor, size).ToArray(),
                objectStream.FileOffsets.Skip(cursor).Take(size).ToArray());

            site.ObjectProperties = ObjectStreamParser.Read(subStream, site.ControlType);
            cursor += size;
        }

        if (sites.Count > 0)
        {
            var validationTarget = sites.First();
            validationTarget.ExtraProperties["objectStreamConsumedBytes"] = cursor;
            validationTarget.ExtraProperties["objectStreamLength"] = data.Length;
            validationTarget.ExtraProperties["objectStreamSizeValidation"] = cursor == data.Length
                ? "exact"
                : cursor < data.Length
                    ? $"under-consumed by {data.Length - cursor} bytes"
                    : $"over-consumed by {cursor - data.Length} bytes";
        }
    }

    private static bool TryReadFormSiteData(
        StorageEntryDump stream,
        int offset,
        bool hasClassInfoCount,
        out FormSiteDataCandidate candidate)
    {
        candidate = default!;
        var data = stream.Data;
        var cursor = offset;
        ushort classInfoCount = 0;
        IReadOnlyList<ClassTableEntry> classTable = Array.Empty<ClassTableEntry>();

        if (hasClassInfoCount)
        {
            if (cursor + 2 > data.Length)
            {
                return false;
            }

            classInfoCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            if (classInfoCount > 64)
            {
                return false;
            }

            if (classInfoCount > 0)
            {
                if (!TryReadClassTable(data, stream.FileOffsets, ref cursor, classInfoCount, out var parsedClassTable))
                {
                    return false;
                }

                classTable = parsedClassTable;
            }
        }

        if (cursor + 8 > data.Length)
        {
            return false;
        }

        var countOfSites = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        var countOfBytes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4, 4));
        cursor += 8;

        if (countOfSites is 0 or > 500 || countOfBytes is 0 or > 1_000_000)
        {
            return false;
        }

        var expectedEnd = cursor + checked((int)countOfBytes);
        if (expectedEnd > data.Length)
        {
            return false;
        }

        var depthStart = cursor;
        if (!TryReadDepthsAndTypes(data, ref cursor, (int)countOfSites, out var depths))
        {
            return false;
        }

        var sites = new List<SiteDescriptor>((int)countOfSites);
        for (var i = 0; i < (int)countOfSites; i++)
        {
            if (!TryReadSite(stream, ref cursor, depths[i], i, classTable, out var site))
            {
                return false;
            }

            sites.Add(site);
        }

        if (cursor != expectedEnd)
        {
            return false;
        }

        var validNameCount = sites.Count(s => !string.IsNullOrWhiteSpace(s.Name));
        var knownTypeCount = sites.Count(s => IsKnownOrClassTableType(s.ClsidCacheIndex, classTable));
        var plausiblePositionCount = sites.Count(s => s.Left is >= -200_000 and <= 200_000 && s.Top is >= -200_000 and <= 200_000);
        var score = validNameCount * 10 + knownTypeCount * 10 + plausiblePositionCount * 5 + sites.Count;
        if (validNameCount != sites.Count || knownTypeCount != sites.Count)
        {
            return false;
        }

        candidate = new FormSiteDataCandidate(
            offset,
            hasClassInfoCount,
            classInfoCount,
            (int)countOfSites,
            (int)countOfBytes,
            depthStart,
            expectedEnd,
            score,
            classTable,
            sites);
        return true;
    }

    private static bool TryReadDepthsAndTypes(
        byte[] data,
        ref int cursor,
        int count,
        out List<SiteDepthType> depths)
    {
        depths = new List<SiteDepthType>(count);
        var depthStart = cursor;
        while (depths.Count < count && cursor + 2 <= data.Length)
        {
            var depth = data[cursor++];
            var typeOrCountByte = data[cursor++];
            var fCount = (typeOrCountByte & 0x80) != 0;
            var typeOrCount = (byte)(typeOrCountByte & 0x7F);

            if (fCount)
            {
                if (typeOrCount == 0 || cursor >= data.Length)
                {
                    return false;
                }

                var siteType = data[cursor++];
                for (var i = 0; i < typeOrCount && depths.Count < count; i++)
                {
                    depths.Add(new SiteDepthType(depth, siteType));
                }
            }
            else
            {
                depths.Add(new SiteDepthType(depth, typeOrCount));
            }
        }

        if (depths.Count != count)
        {
            return false;
        }

        while ((cursor - depthStart) % 4 != 0)
        {
            cursor++;
        }

        return cursor <= data.Length;
    }

    private static bool TryReadSite(
        StorageEntryDump stream,
        ref int cursor,
        SiteDepthType depth,
        int index,
        IReadOnlyList<ClassTableEntry> classTable,
        out SiteDescriptor site)
    {
        site = default!;
        var data = stream.Data;
        var siteStart = cursor;
        if (siteStart + 8 > data.Length)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteStart, 2));
        var cbSite = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteStart + 2, 2));
        var siteEnd = siteStart + 4 + cbSite;
        if (version != 0 || cbSite < 4 || cbSite > 4096 || siteEnd > data.Length)
        {
            return false;
        }

        var propMaskValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(siteStart + 4, 4));
        if ((propMaskValue & 0xFFFF_8000) != 0)
        {
            return false;
        }

        var mask = new SitePropMask(propMaskValue);
        if (!mask.HasName || !mask.HasPosition || !mask.HasClsidCacheIndex)
        {
            return false;
        }

        site = new SiteDescriptor
        {
            SiteIndex = index,
            Depth = depth.Depth,
            SiteType = depth.SiteType,
            PropMask = propMaskValue,
            StreamStart = siteStart,
            StreamEnd = siteEnd,
        };
        site.ExtraProperties["siteParser"] = "msOFormsOleSiteConcrete";
        site.ExtraProperties["siteOffset"] = stream.FileOffsets.Length > siteStart ? stream.FileOffsets[siteStart] : 0;
        site.ExtraProperties["siteLocalOffset"] = siteStart;
        site.ExtraProperties["cbSite"] = cbSite;
        site.ExtraProperties["sitePropMask"] = $"0x{propMaskValue:X8}";
        site.ExtraProperties["sitePropMaskOffset"] = stream.FileOffsets.Length > siteStart + 4 ? stream.FileOffsets[siteStart + 4] : 0;

        var dataBlockStart = siteStart + 8;
        var dataCursor = dataBlockStart;
        var dataBlock = new InternalSiteDataBlock();

        try
        {
            if (mask.HasName)
            {
                var nameCountLocalOffset = AlignOffset(dataCursor, siteStart, 4);
                site.ExtraProperties["siteNameCountRawLocalOffset"] = nameCountLocalOffset;
                site.ExtraProperties["siteNameCountRawOffset"] = stream.FileOffsets.Length > nameCountLocalOffset ? stream.FileOffsets[nameCountLocalOffset] : 0;
                dataBlock.NameCountLocalOffset = nameCountLocalOffset;
                dataBlock.NameCount = ReadCount(data, ref dataCursor, siteStart, siteEnd, out var raw);
                site.ExtraProperties["siteNameCountRaw"] = raw;
                site.ExtraProperties["siteNameByteCount"] = dataBlock.NameCount.Count;
                site.ExtraProperties["siteNameCompressed"] = dataBlock.NameCount.Compressed;
            }

            if (mask.HasTag)
            {
                dataBlock.TagCountLocalOffset = AlignOffset(dataCursor, siteStart, 4);
                dataBlock.TagCount = ReadCount(data, ref dataCursor, siteStart, siteEnd, out var raw);
                site.ExtraProperties["siteTagCountRaw"] = raw;
                site.ExtraProperties["tagByteCount"] = dataBlock.TagCount.Count;
                site.ExtraProperties["tagCompressed"] = dataBlock.TagCount.Compressed;
            }

            if (mask.HasID)
            {
                site.IdOffset = AlignOffset(dataCursor, siteStart, 4);
                site.Id = ReadUInt32(data, ref dataCursor, siteStart, siteEnd);
                site.ExtraProperties["siteId"] = site.Id;
                site.ExtraProperties["siteIdOffset"] = stream.FileOffsets.Length > site.IdOffset ? stream.FileOffsets[site.IdOffset] : 0;
            }

            if (mask.HasHelpContextID)
            {
                site.HelpContextIdOffset = AlignOffset(dataCursor, siteStart, 4);
                site.HelpContextId = ReadUInt32(data, ref dataCursor, siteStart, siteEnd);
            }

            if (mask.HasBitFlags)
            {
                site.BitFlagsOffset = AlignOffset(dataCursor, siteStart, 4);
                site.BitFlags = ReadUInt32(data, ref dataCursor, siteStart, siteEnd);
                site.ExtraProperties["siteBitFlagsRaw"] = site.BitFlags;
                site.ExtraProperties["siteBitFlagsRawOffset"] = stream.FileOffsets.Length > site.BitFlagsOffset ? stream.FileOffsets[site.BitFlagsOffset] : 0;
            }

            if (mask.HasObjectStreamSize)
            {
                site.ObjectStreamSizeOffset = AlignOffset(dataCursor, siteStart, 4);
                site.ObjectStreamSize = checked((int)ReadUInt32(data, ref dataCursor, siteStart, siteEnd));
                site.ExtraProperties["siteObjectStreamSize"] = site.ObjectStreamSize;
                site.ExtraProperties["siteObjectStreamSizeOffset"] = stream.FileOffsets.Length > site.ObjectStreamSizeOffset ? stream.FileOffsets[site.ObjectStreamSizeOffset] : 0;
                site.ExtraProperties["objectStreamSizeFromSite"] = site.ObjectStreamSize;
            }

            if (mask.HasTabIndex)
            {
                site.TabIndexOffset = AlignOffset(dataCursor, siteStart, 2);
                site.TabIndex = ReadUInt16(data, ref dataCursor, siteStart, siteEnd);
                site.ExtraProperties["tabIndex"] = site.TabIndex;
                site.ExtraProperties["tabIndexOffset"] = stream.FileOffsets.Length > site.TabIndexOffset ? stream.FileOffsets[site.TabIndexOffset] : 0;
            }

            if (mask.HasClsidCacheIndex)
            {
                site.ClsidCacheIndexOffset = AlignOffset(dataCursor, siteStart, 2);
                site.ClsidCacheIndex = ReadUInt16(data, ref dataCursor, siteStart, siteEnd);
                site.ExtraProperties["clsidCacheIndex"] = site.ClsidCacheIndex;
                site.ExtraProperties["clsidCacheIndexOffset"] = stream.FileOffsets.Length > site.ClsidCacheIndexOffset ? stream.FileOffsets[site.ClsidCacheIndexOffset] : 0;
            }

            if (mask.HasGroupID)
            {
                site.GroupIdOffset = AlignOffset(dataCursor, siteStart, 2);
                site.GroupId = ReadUInt16(data, ref dataCursor, siteStart, siteEnd);
            }

            if (mask.HasControlTipText)
            {
                dataBlock.ControlTipCountLocalOffset = AlignOffset(dataCursor, siteStart, 4);
                dataBlock.ControlTipCount = ReadCount(data, ref dataCursor, siteStart, siteEnd, out var raw);
                site.ExtraProperties["controlTipTextCountRaw"] = raw;
                site.ExtraProperties["controlTipTextByteCount"] = dataBlock.ControlTipCount.Count;
                site.ExtraProperties["controlTipTextCompressed"] = dataBlock.ControlTipCount.Compressed;
            }

            if (mask.HasRuntimeLicKey)
            {
                dataBlock.RuntimeLicKeyCountLocalOffset = AlignOffset(dataCursor, siteStart, 4);
                dataBlock.RuntimeLicKeyCount = ReadCount(data, ref dataCursor, siteStart, siteEnd, out var raw);
                site.ExtraProperties["runtimeLicKeyCountRaw"] = raw;
                site.ExtraProperties["runtimeLicKeyByteCount"] = dataBlock.RuntimeLicKeyCount.Count;
                site.ExtraProperties["runtimeLicKeyCompressed"] = dataBlock.RuntimeLicKeyCount.Compressed;
            }

            if (mask.HasControlSource)
            {
                dataBlock.ControlSourceCountLocalOffset = AlignOffset(dataCursor, siteStart, 4);
                dataBlock.ControlSourceCount = ReadCount(data, ref dataCursor, siteStart, siteEnd, out var raw);
                site.ExtraProperties["controlSourceCountRaw"] = raw;
                site.ExtraProperties["controlSourceByteCount"] = dataBlock.ControlSourceCount.Count;
                site.ExtraProperties["controlSourceCompressed"] = dataBlock.ControlSourceCount.Compressed;
            }

            if (mask.HasRowSource)
            {
                dataBlock.RowSourceCountLocalOffset = AlignOffset(dataCursor, siteStart, 4);
                dataBlock.RowSourceCount = ReadCount(data, ref dataCursor, siteStart, siteEnd, out var raw);
                site.ExtraProperties["rowSourceCountRaw"] = raw;
                site.ExtraProperties["rowSourceByteCount"] = dataBlock.RowSourceCount.Count;
                site.ExtraProperties["rowSourceCompressed"] = dataBlock.RowSourceCount.Compressed;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }

        dataCursor = AlignOffset(dataCursor, siteStart, 4);
        if (dataCursor > siteEnd)
        {
            return false;
        }

        var extraCursor = dataCursor;
        try
        {
            if (mask.HasName)
            {
                site.NameOffset = extraCursor;
                site.Name = ReadFmString(data, extraCursor, dataBlock.NameCount, siteEnd);
                site.ExtraProperties["name"] = site.Name;
                site.ExtraProperties["nameRaw"] = site.Name;
                site.ExtraProperties["nameOffset"] = stream.FileOffsets.Length > site.NameOffset ? stream.FileOffsets[site.NameOffset] : 0;
                MsFormsBinary.AddStringSpan(site.ExtraProperties, "name", dataBlock.NameCount, dataBlock.NameCountLocalOffset, site.NameOffset, stream.FileOffsets);
                extraCursor += Align4(dataBlock.NameCount.Count);
            }

            if (mask.HasTag)
            {
                site.TagOffset = extraCursor;
                site.Tag = ReadFmString(data, extraCursor, dataBlock.TagCount, siteEnd);
                site.ExtraProperties["tag"] = site.Tag;
                site.ExtraProperties["tagOffset"] = stream.FileOffsets.Length > site.TagOffset ? stream.FileOffsets[site.TagOffset] : 0;
                MsFormsBinary.AddStringSpan(site.ExtraProperties, "tag", dataBlock.TagCount, dataBlock.TagCountLocalOffset, site.TagOffset, stream.FileOffsets);
                extraCursor += Align4(dataBlock.TagCount.Count);
            }

            if (mask.HasPosition)
            {
                extraCursor = AlignOffset(extraCursor, siteStart, 4);
                if (extraCursor + 8 > siteEnd)
                {
                    return false;
                }

                site.LeftOffset = extraCursor;
                site.Left = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor, 4));
                site.TopOffset = extraCursor + 4;
                site.Top = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor + 4, 4));
                site.ExtraProperties["positionSource"] = "oleSiteConcrete";
                site.ExtraProperties["leftOffset"] = stream.FileOffsets.Length > site.LeftOffset ? stream.FileOffsets[site.LeftOffset] : 0;
                site.ExtraProperties["topOffset"] = stream.FileOffsets.Length > site.TopOffset ? stream.FileOffsets[site.TopOffset] : 0;
                extraCursor += 8;
            }

            if (mask.HasControlTipText)
            {
                extraCursor = AlignOffset(extraCursor, siteStart, 4);
                var offset = extraCursor;
                site.ExtraProperties["controlTipText"] = ReadFmString(data, offset, dataBlock.ControlTipCount, siteEnd);
                site.ExtraProperties["controlTipTextOffset"] = stream.FileOffsets.Length > offset ? stream.FileOffsets[offset] : 0;
                MsFormsBinary.AddStringSpan(site.ExtraProperties, "controlTipText", dataBlock.ControlTipCount, dataBlock.ControlTipCountLocalOffset, offset, stream.FileOffsets);
                extraCursor += Align4(dataBlock.ControlTipCount.Count);
            }

            if (mask.HasRuntimeLicKey)
            {
                extraCursor = AlignOffset(extraCursor, siteStart, 4);
                var offset = extraCursor;
                site.ExtraProperties["runtimeLicKey"] = ReadFmString(data, offset, dataBlock.RuntimeLicKeyCount, siteEnd);
                site.ExtraProperties["runtimeLicKeyOffset"] = stream.FileOffsets.Length > offset ? stream.FileOffsets[offset] : 0;
                MsFormsBinary.AddStringSpan(site.ExtraProperties, "runtimeLicKey", dataBlock.RuntimeLicKeyCount, dataBlock.RuntimeLicKeyCountLocalOffset, offset, stream.FileOffsets);
                extraCursor += Align4(dataBlock.RuntimeLicKeyCount.Count);
            }

            if (mask.HasControlSource)
            {
                extraCursor = AlignOffset(extraCursor, siteStart, 4);
                var offset = extraCursor;
                site.ExtraProperties["controlSource"] = ReadFmString(data, offset, dataBlock.ControlSourceCount, siteEnd);
                site.ExtraProperties["controlSourceOffset"] = stream.FileOffsets.Length > offset ? stream.FileOffsets[offset] : 0;
                MsFormsBinary.AddStringSpan(site.ExtraProperties, "controlSource", dataBlock.ControlSourceCount, dataBlock.ControlSourceCountLocalOffset, offset, stream.FileOffsets);
                extraCursor += Align4(dataBlock.ControlSourceCount.Count);
            }

            if (mask.HasRowSource)
            {
                extraCursor = AlignOffset(extraCursor, siteStart, 4);
                var offset = extraCursor;
                site.ExtraProperties["rowSource"] = ReadFmString(data, offset, dataBlock.RowSourceCount, siteEnd);
                site.ExtraProperties["rowSourceOffset"] = stream.FileOffsets.Length > offset ? stream.FileOffsets[offset] : 0;
                MsFormsBinary.AddStringSpan(site.ExtraProperties, "rowSource", dataBlock.RowSourceCount, dataBlock.RowSourceCountLocalOffset, offset, stream.FileOffsets);
                extraCursor += Align4(dataBlock.RowSourceCount.Count);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(site.Name) || !IsKnownOrClassTableType(site.ClsidCacheIndex, classTable))
        {
            return false;
        }

        if (site.ClsidCacheIndex is >= 0x8000)
        {
            var classTableIndex = site.ClsidCacheIndex.Value - 0x8000;
            site.ExtraProperties["classTableIndex"] = classTableIndex;
            if (classTableIndex >= 0 && classTableIndex < classTable.Count)
            {
                var classInfo = classTable[classTableIndex];
                site.ExtraProperties["classInfoPropMask"] = $"0x{classInfo.PropMask:X8}";
                site.ExtraProperties["classInfoOffset"] = classInfo.FileOffset;
                site.ExtraProperties["classInfoLocalOffset"] = classInfo.LocalOffset;
                site.ExtraProperties["classInfoCb"] = classInfo.CbClassTable;
                if (classInfo.ClsId is not null) site.ExtraProperties["classInfoClsId"] = classInfo.ClsId;
                if (classInfo.DispEvent is not null) site.ExtraProperties["classInfoDispEvent"] = classInfo.DispEvent;
                if (classInfo.DefaultProg is not null) site.ExtraProperties["classInfoDefaultProg"] = classInfo.DefaultProg;
            }
        }

        if (site.Left is { } left && site.Top is { } top &&
            (left < -200_000 || left > 200_000 || top < -200_000 || top > 200_000))
        {
            return false;
        }

        site.ControlType = ResolveControlType(site.ClsidCacheIndex);
        cursor = siteEnd;
        return true;
    }


    private static bool IsKnownOrClassTableType(ushort? clsidCacheIndex, IReadOnlyList<ClassTableEntry> classTable)
    {
        if (clsidCacheIndex is not { } value)
        {
            return false;
        }

        if (KnownMsFormsTypeCodes.Contains(value))
        {
            return true;
        }

        return value >= 0x8000 && value - 0x8000 < classTable.Count;
    }

    private static bool TryReadClassTable(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        ushort classInfoCount,
        out IReadOnlyList<ClassTableEntry> classTable)
    {
        var result = new List<ClassTableEntry>(classInfoCount);
        for (var i = 0; i < classInfoCount; i++)
        {
            if (!TryReadSiteClassInfo(data, fileOffsets, ref cursor, i, out var entry))
            {
                classTable = Array.Empty<ClassTableEntry>();
                return false;
            }

            result.Add(entry);
        }

        classTable = result;
        return true;
    }

    private static bool TryReadSiteClassInfo(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int index,
        out ClassTableEntry entry)
    {
        entry = default!;
        var start = cursor;
        if (start + 8 > data.Length)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start, 2));
        var cbClassTable = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 2, 2));
        var end = start + 4 + cbClassTable;
        if (version != 0 || cbClassTable < 4 || end > data.Length)
        {
            return false;
        }

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + 4, 4));
        var dataCursor = start + 8;

        // ClassInfoDataBlock. The fields smaller than 4 bytes are packed in documentation order;
        // padding is only needed before DispidRowset and at the end of the data block.
        if (MsFormsBinary.HasBit(propMask, 4))
        {
            dataCursor += 4; // ClassTableFlags + VarFlags, 2 bytes each.
        }
        if (MsFormsBinary.HasBit(propMask, 5)) dataCursor += 4; // CountOfMethods
        if (MsFormsBinary.HasBit(propMask, 6)) dataCursor += 4; // DispidBind
        if (MsFormsBinary.HasBit(propMask, 7)) dataCursor += 2; // GetBindIndex
        if (MsFormsBinary.HasBit(propMask, 8)) dataCursor += 2; // PutBindIndex
        if (MsFormsBinary.HasBit(propMask, 9)) dataCursor += 2; // BindType
        if (MsFormsBinary.HasBit(propMask, 10)) dataCursor += 2; // GetValueIndex
        if (MsFormsBinary.HasBit(propMask, 11)) dataCursor += 2; // PutValueIndex
        if (MsFormsBinary.HasBit(propMask, 12)) dataCursor += 2; // ValueType
        if (MsFormsBinary.HasBit(propMask, 13))
        {
            dataCursor = AlignAbsolute(dataCursor, 4);
            dataCursor += 4; // DispidRowset
        }
        if (MsFormsBinary.HasBit(propMask, 14)) dataCursor += 2; // SetRowset
        dataCursor = AlignAbsolute(dataCursor, 4);

        if (dataCursor > end)
        {
            return false;
        }

        string? clsId = null;
        string? dispEvent = null;
        string? defaultProg = null;
        var extraCursor = dataCursor;
        if (MsFormsBinary.HasBit(propMask, 0))
        {
            if (extraCursor + 16 > end) return false;
            clsId = ReadGuidString(data, extraCursor);
            extraCursor += 16;
        }
        if (MsFormsBinary.HasBit(propMask, 1))
        {
            if (extraCursor + 16 > end) return false;
            dispEvent = ReadGuidString(data, extraCursor);
            extraCursor += 16;
        }
        if (MsFormsBinary.HasBit(propMask, 3))
        {
            if (extraCursor + 16 > end) return false;
            defaultProg = ReadGuidString(data, extraCursor);
            extraCursor += 16;
        }

        entry = new ClassTableEntry(
            index,
            start,
            MsFormsBinary.OffsetAt(fileOffsets, start),
            cbClassTable,
            propMask,
            clsId,
            dispEvent,
            defaultProg);
        cursor = end;
        return true;
    }

    private static int AlignAbsolute(int cursor, int alignment)
    {
        var remainder = cursor % alignment;
        return remainder == 0 ? cursor : cursor + alignment - remainder;
    }

    private static string ReadGuidString(byte[] data, int offset) => new Guid(data.AsSpan(offset, 16)).ToString("B").ToUpperInvariant();

    private static CountOfBytesWithCompressionFlag ReadCount(
        byte[] data,
        ref int cursor,
        int controlStart,
        int siteEnd,
        out uint raw)
    {
        raw = ReadUInt32(data, ref cursor, controlStart, siteEnd);
        return MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(raw);
    }

    private static uint ReadUInt32(byte[] data, ref int cursor, int controlStart, int siteEnd)
    {
        cursor = AlignOffset(cursor, controlStart, 4);
        if (cursor + 4 > siteEnd)
        {
            throw new InvalidDataException("Unexpected end of SiteDataBlock while reading a UInt32 value.");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        return value;
    }

    private static ushort ReadUInt16(byte[] data, ref int cursor, int controlStart, int siteEnd)
    {
        cursor = AlignOffset(cursor, controlStart, 2);
        if (cursor + 2 > siteEnd)
        {
            throw new InvalidDataException("Unexpected end of SiteDataBlock while reading a UInt16 value.");
        }

        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        return value;
    }

    private static int Align4(int count) => (count + 3) & ~3;

    private static int AlignOffset(int cursor, int controlStart, int alignment)
    {
        var remainder = (cursor - controlStart) % alignment;
        return remainder == 0 ? cursor : cursor + alignment - remainder;
    }

    private static string ReadFmString(byte[] data, int offset, CountOfBytesWithCompressionFlag count, int siteEnd)
    {
        if (count.Count == 0)
        {
            return string.Empty;
        }

        if (count.Count < 0 || count.Count > 1_000_000 || offset + count.Count > siteEnd || offset + count.Count > data.Length)
        {
            throw new InvalidDataException("Invalid fmString byte count.");
        }

        var text = count.Compressed
            ? Encoding.Latin1.GetString(data, offset, count.Count)
            : Encoding.Unicode.GetString(data, offset, count.Count);
        return MsFormsBinary.TrimTrailingBinaryChars(text);
    }

    private static string ResolveControlType(ushort? clsidCacheIndex)
    {
        if (clsidCacheIndex is not { } value)
        {
            return "Unknown";
        }

        if (value >= 0x8000)
        {
            return "ActiveX";
        }

        return ControlTypeSchema.TryGetMsFormsType((byte)(value & 0xFF), out var type)
            ? type
            : "Unknown";
    }

    private static bool IsParentStorageControl(string? controlType) =>
        controlType is "Frame" or "MultiPage" or "Page";

    private readonly record struct SiteDepthType(byte Depth, byte SiteType);

    private sealed record ClassTableEntry(
        int Index,
        int LocalOffset,
        int FileOffset,
        int CbClassTable,
        uint PropMask,
        string? ClsId,
        string? DispEvent,
        string? DefaultProg);

    private sealed record FormSiteDataCandidate(
        int Offset,
        bool HasClassInfoCount,
        ushort ClassInfoCount,
        int CountOfSites,
        int CountOfBytes,
        int DepthStartOffset,
        int EndOffset,
        int Score,
        IReadOnlyList<ClassTableEntry> ClassTable,
        IReadOnlyList<SiteDescriptor> Sites);

    private sealed class InternalSiteDataBlock
    {
        public CountOfBytesWithCompressionFlag NameCount { get; set; }
        public int NameCountLocalOffset { get; set; } = -1;
        public CountOfBytesWithCompressionFlag TagCount { get; set; }
        public int TagCountLocalOffset { get; set; } = -1;
        public CountOfBytesWithCompressionFlag ControlTipCount { get; set; }
        public int ControlTipCountLocalOffset { get; set; } = -1;
        public CountOfBytesWithCompressionFlag RuntimeLicKeyCount { get; set; }
        public int RuntimeLicKeyCountLocalOffset { get; set; } = -1;
        public CountOfBytesWithCompressionFlag ControlSourceCount { get; set; }
        public int ControlSourceCountLocalOffset { get; set; } = -1;
        public CountOfBytesWithCompressionFlag RowSourceCount { get; set; }
        public int RowSourceCountLocalOffset { get; set; } = -1;
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
