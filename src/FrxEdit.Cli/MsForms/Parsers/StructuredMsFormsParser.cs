
internal static class StructuredMsFormsParser
{
    private static int Align4(int n) => (n + 3) & ~3;

    public static IReadOnlyList<SiteDescriptor> Parse(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x04)
        {
            return [];
        }

        var cbForm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        var siteDataOffset = 4 + cbForm;
        
        // Skip ClassInfoCount and ClassTable if present
        var cursor = siteDataOffset;
        if (cursor + 2 > data.Length) return [];
        
        var classInfoCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        for (var i = 0; i < classInfoCount; i++)
        {
            if (cursor + 4 > data.Length) return [];
            var cbClassTable = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor + 2, 2));
            cursor += 4 + cbClassTable;
        }

        if (cursor + 8 > data.Length) return [];

        var countOfSites = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        var countOfBytes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4, 4));
        cursor += 8;

        var depths = ReadDepths(data, ref cursor, (int)countOfSites);
        
        // Align to 4 bytes after depths
        while ((cursor - (siteDataOffset + 2)) % 4 != 0 && cursor < data.Length)
        {
            cursor++;
        }
        // Actually, the original code used:
        // var depthBytes = cursor - depthStart;
        // while (depthBytes % 4 != 0 && cursor < data.Length) { cursor++; depthBytes++; }
        
        var sites = new List<SiteDescriptor>();
        var count = Math.Min((int)countOfSites, depths.Count);
        for (var i = 0; i < count; i++)
        {
            var site = ReadSite(stream, ref cursor, depths[i], i);
            if (site != null)
            {
                sites.Add(site);
            }
        }

        return sites;
    }

    public static void EnrichFromObjectStream(
        IReadOnlyList<SiteDescriptor> sites,
        StorageEntryDump objectStream)
    {
        var cursor = 0;
        var data = objectStream.Data;

        foreach (var site in sites)
        {
            var clsid = (byte)(site.ClsidCacheIndex ?? 0);
            var isParentStorage = clsid is 0x0E or 0x39 or 0x07; // Frame, MultiPage, Page
            
            if (site.ObjectStreamSize is not { } size || size <= 0 || isParentStorage)
            {
                continue;
            }

            if (cursor + size > data.Length)
            {
                break;
            }

            site.ObjectStreamLocalOffset = cursor;
            site.ObjectStreamFileOffset = objectStream.FileOffsets[cursor];
            
            var controlType = string.Empty;
            if (site.SiteType != 0)
            {
                ControlTypeSchema.TryGetMsFormsType(site.SiteType, out controlType);
            }

            var subStream = StorageEntryDump.CreateSegment(
                data.AsSpan(cursor, size).ToArray(),
                objectStream.FileOffsets.Skip(cursor).Take(size).ToArray());

            site.ObjectProperties = ObjectStreamParser.Read(subStream, controlType);
            cursor += size;
        }
    }

    private static List<SiteDepthType> ReadDepths(byte[] data, ref int cursor, int count)
    {
        var depths = new List<SiteDepthType>(count);
        while (depths.Count < count && cursor + 2 <= data.Length)
        {
            var depth = data[cursor];
            var typeOrCount = (byte)(data[cursor + 1] & 0x7F);
            var isCount = (data[cursor + 1] & 0x80) != 0;
            cursor += 2;

            if (isCount)
            {
                if (cursor >= data.Length || typeOrCount == 0) break;
                
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
        
        return depths;
    }

    private static SiteDescriptor? ReadSite(StorageEntryDump stream, ref int cursor, SiteDepthType depth, int index)
    {
        var data = stream.Data;
        var siteStart = cursor;
        if (siteStart + 8 > data.Length) return null;

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteStart, 2));
        var cbSite = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(siteStart + 2, 2));
        var siteEnd = siteStart + 4 + cbSite;
        if (siteEnd > data.Length) return null;
        
        var propMaskValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(siteStart + 4, 4));
        var mask = new SitePropMask(propMaskValue);
        
        var site = new SiteDescriptor
        {
            SiteIndex = index,
            Depth = depth.Depth,
            SiteType = depth.SiteType,
            PropMask = propMaskValue,
            StreamStart = siteStart,
            StreamEnd = siteEnd
        };

        var dataBlockStart = siteStart + 8;
        var dataCursor = dataBlockStart;
        
        var dataBlock = new InternalSiteDataBlock();

        if (mask.HasName) dataBlock.NameCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
        if (mask.HasTag) dataBlock.TagCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
        if (mask.HasID)
        {
            site.IdOffset = dataCursor;
            site.Id = ReadUInt32(data, ref dataCursor, dataBlockStart, siteEnd);
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
        }
        if (mask.HasObjectStreamSize)
        {
            site.ObjectStreamSizeOffset = dataCursor;
            site.ObjectStreamSize = (int)ReadUInt32(data, ref dataCursor, dataBlockStart, siteEnd);
        }
        if (mask.HasTabIndex)
        {
            site.TabIndexOffset = dataCursor;
            site.TabIndex = ReadUInt16(data, ref dataCursor, dataBlockStart, siteEnd);
        }
        if (mask.HasClsidCacheIndex)
        {
            site.ClsidCacheIndexOffset = dataCursor;
            site.ClsidCacheIndex = ReadUInt16(data, ref dataCursor, dataBlockStart, siteEnd);
        }
        if (mask.HasGroupID)
        {
            site.GroupIdOffset = dataCursor;
            site.GroupId = ReadUInt16(data, ref dataCursor, dataBlockStart, siteEnd);
        }

        if (mask.HasControlTipText)
        {
            AlignRelative(ref dataCursor, dataBlockStart, 4);
            dataBlock.ControlTipCount = ReadCount(data, ref dataCursor, dataBlockStart, siteEnd);
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

        if (dataCursor > siteEnd) { cursor = siteEnd; return null; }

        // ExtraDataBlock
        var extraCursor = dataCursor;
        
        if (mask.HasName)
        {
            AlignRelative(ref extraCursor, siteStart, 4);
            site.NameOffset = extraCursor;
            site.Name = ReadFmString(data, extraCursor, dataBlock.NameCount, siteEnd);
            extraCursor += dataBlock.NameCount.Count;
        }

        if (mask.HasTag)
        {
            AlignRelative(ref extraCursor, siteStart, 4);
            site.TagOffset = extraCursor;
            site.Tag = ReadFmString(data, extraCursor, dataBlock.TagCount, siteEnd);
            extraCursor += dataBlock.TagCount.Count;
        }

        if (mask.HasPosition)
        {
            AlignRelative(ref extraCursor, siteStart, 4);
            if (extraCursor + 8 <= siteEnd)
            {
                site.LeftOffset = extraCursor;
                site.Left = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor, 4));
                site.TopOffset = extraCursor + 4;
                site.Top = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor + 4, 4));
                extraCursor += 8;
            }
        }

        if (mask.HasControlTipText)
        {
            AlignRelative(ref extraCursor, siteStart, 4);
            site.ExtraProperties["controlTipText"] = ReadFmString(data, extraCursor, dataBlock.ControlTipCount, siteEnd);
            extraCursor += dataBlock.ControlTipCount.Count;
        }

        if (mask.HasRuntimeLicKey)
        {
            AlignRelative(ref extraCursor, siteStart, 4);
            site.ExtraProperties["runtimeLicKey"] = ReadFmString(data, extraCursor, dataBlock.RuntimeLicKeyCount, siteEnd);
            extraCursor += dataBlock.RuntimeLicKeyCount.Count;
        }

        if (mask.HasControlSource)
        {
            AlignRelative(ref extraCursor, siteStart, 4);
            site.ExtraProperties["controlSource"] = ReadFmString(data, extraCursor, dataBlock.ControlSourceCount, siteEnd);
            extraCursor += dataBlock.ControlSourceCount.Count;
        }

        if (mask.HasRowSource)
        {
            AlignRelative(ref extraCursor, siteStart, 4);
            site.ExtraProperties["rowSource"] = ReadFmString(data, extraCursor, dataBlock.RowSourceCount, siteEnd);
            extraCursor += dataBlock.RowSourceCount.Count;
        }

        AlignRelative(ref extraCursor, siteStart, 4);
        cursor = extraCursor;
        return site;
    }

    private static CountOfBytesWithCompressionFlag ReadCount(byte[] data, ref int cursor, int blockStart, int siteEnd)
    {
        var raw = ReadUInt32(data, ref cursor, blockStart, siteEnd);
        return MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(raw);
    }

    private static uint ReadUInt32(byte[] data, ref int cursor, int blockStart, int siteEnd)
    {
        AlignRelative(ref cursor, blockStart, 4);
        if (cursor + 4 > siteEnd) return 0;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        return value;
    }

    private static ushort ReadUInt16(byte[] data, ref int cursor, int blockStart, int siteEnd)
    {
        AlignRelative(ref cursor, blockStart, 2);
        if (cursor + 2 > siteEnd) return 0;
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
        if (count.Count == 0) return string.Empty;
        if (offset + count.Count > siteEnd || offset + count.Count > data.Length) return string.Empty;
        
        return count.Compressed
            ? Encoding.Latin1.GetString(data, offset, count.Count)
            : Encoding.Unicode.GetString(data, offset, count.Count);
    }

    private readonly record struct SiteDepthType(byte Depth, byte SiteType);

    private class InternalSiteDataBlock
    {
        public CountOfBytesWithCompressionFlag NameCount { get; set; }
        public CountOfBytesWithCompressionFlag TagCount { get; set; }
        public CountOfBytesWithCompressionFlag ControlTipCount { get; set; }
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
