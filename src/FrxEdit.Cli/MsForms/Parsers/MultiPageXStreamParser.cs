internal static class MultiPageXStreamParser
{
    public static bool TryRead(StorageEntryDump stream, out MultiPageXStreamProperties result)
    {
        result = default!;
        var data = stream.Data;
        if (data.Length < 16)
        {
            return false;
        }

        var candidates = new List<MultiPageXStreamProperties>();
        var maxPagePropertyCount = Math.Min(128, data.Length / 8);
        for (var pagePropertyCount = 1; pagePropertyCount <= maxPagePropertyCount; pagePropertyCount++)
        {
            var cursor = 0;
            var pageProperties = new List<Dictionary<string, object?>>(pagePropertyCount);
            var ok = true;
            for (var i = 0; i < pagePropertyCount; i++)
            {
                if (!TryReadPageProperties(data, stream.FileOffsets, ref cursor, i, out var pageProperty))
                {
                    ok = false;
                    break;
                }

                pageProperties.Add(pageProperty);
            }

            if (!ok)
            {
                continue;
            }

            if (!TryReadMultiPageProperties(data, stream.FileOffsets, ref cursor, data.Length, pagePropertyCount, out var multiPageProperties, out var pageIds))
            {
                continue;
            }

            if (cursor != data.Length)
            {
                continue;
            }

            candidates.Add(new MultiPageXStreamProperties(multiPageProperties, pageProperties, pageIds));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // Prefer the candidate whose PageProperties count is exactly PageCount + 1, as required by MS-OFORMS.
        result = candidates
            .OrderByDescending(c => c.PageIds.Count > 0 && c.PageProperties.Count == c.PageIds.Count + 1 ? 1 : 0)
            .ThenBy(c => c.PageProperties.Count)
            .First();
        return true;
    }

    private static bool TryReadPageProperties(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int index,
        out Dictionary<string, object?> properties)
    {
        properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var start = cursor;
        if (start + 8 > data.Length)
        {
            return false;
        }

        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbPage = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var end = start + 4 + cbPage;
        if (minorVersion != 0 || majorVersion != 2 || cbPage < 4 || end > data.Length)
        {
            cursor = start;
            return false;
        }

        var propMaskLocalOffset = cursor;
        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        // PagePropMask uses bit 1 for TransitionEffect and bit 2 for TransitionPeriod. Bit 0 and all
        // higher bits are unused in the documented structure.
        if ((propMask & ~0x0000_0006u) != 0)
        {
            cursor = start;
            return false;
        }

        properties["index"] = index;
        properties["ignored"] = index == 0;
        properties["localOffset"] = start;
        properties["offset"] = MsFormsBinary.OffsetAt(fileOffsets, start);
        properties["minorVersion"] = minorVersion;
        properties["majorVersion"] = majorVersion;
        properties["cbPage"] = cbPage;
        properties["propMask"] = $"0x{propMask:X8}";
        properties["propMaskLocalOffset"] = propMaskLocalOffset;
        properties["propMaskOffset"] = MsFormsBinary.OffsetAt(fileOffsets, propMaskLocalOffset);

        if (MsFormsBinary.HasBit(propMask, 1))
        {
            if (cursor + 4 > end)
            {
                cursor = start;
                return false;
            }

            properties["transitionEffect"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["transitionEffectLocalOffset"] = cursor;
            properties["transitionEffectOffset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
            cursor += 4;
        }

        if (MsFormsBinary.HasBit(propMask, 2))
        {
            if (cursor + 4 > end)
            {
                cursor = start;
                return false;
            }

            properties["transitionPeriod"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["transitionPeriodLocalOffset"] = cursor;
            properties["transitionPeriodOffset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
            cursor += 4;
        }

        if (cursor != end)
        {
            cursor = start;
            return false;
        }

        return true;
    }

    private static bool TryReadMultiPageProperties(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int endOfStream,
        int pagePropertyCount,
        out Dictionary<string, object?> properties,
        out List<uint> pageIds)
    {
        properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        pageIds = [];
        var start = cursor;
        if (start + 8 > endOfStream)
        {
            return false;
        }

        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbMultiPageControlProperties = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var declaredEnd = start + 4 + cbMultiPageControlProperties;
        if (minorVersion != 0 || majorVersion != 2 || cbMultiPageControlProperties < 4 || declaredEnd > endOfStream)
        {
            cursor = start;
            return false;
        }

        var propMaskLocalOffset = cursor;
        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        // MultiPagePropertiesPropMask uses bit 1 for PageCount, bit 2 for ID and bit 3 for Flags.
        // fFlags is a flag-only signal in the DataBlock examples and does not add bytes here.
        if ((propMask & ~0x0000_000Eu) != 0)
        {
            cursor = start;
            return false;
        }

        properties["xStreamParser"] = "msOFormsMultiPageXStream";
        properties["xStreamLocalOffset"] = start;
        properties["xStreamOffset"] = MsFormsBinary.OffsetAt(fileOffsets, start);
        properties["multiPageMinorVersion"] = minorVersion;
        properties["multiPageMajorVersion"] = majorVersion;
        properties["cbMultiPageControlProperties"] = cbMultiPageControlProperties;
        properties["multiPagePropMask"] = $"0x{propMask:X8}";
        properties["multiPagePropMaskLocalOffset"] = propMaskLocalOffset;
        properties["multiPagePropMaskOffset"] = MsFormsBinary.OffsetAt(fileOffsets, propMaskLocalOffset);

        int? pageCount = null;
        if (MsFormsBinary.HasBit(propMask, 1))
        {
            if (cursor + 4 > declaredEnd)
            {
                cursor = start;
                return false;
            }

            pageCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["multiPagePageCount"] = pageCount;
            properties["multiPagePageCountLocalOffset"] = cursor;
            properties["multiPagePageCountOffset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
            cursor += 4;
        }

        if (MsFormsBinary.HasBit(propMask, 2))
        {
            if (cursor + 4 > declaredEnd)
            {
                cursor = start;
                return false;
            }

            properties["multiPageId"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["multiPageIdLocalOffset"] = cursor;
            properties["multiPageIdOffset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
            cursor += 4;
        }

        if (MsFormsBinary.HasBit(propMask, 3))
        {
            properties["multiPageFlagsNonDefault"] = true;
        }

        if (cursor != declaredEnd)
        {
            cursor = start;
            return false;
        }

        if (pageCount is null or < 0 or > 127)
        {
            cursor = start;
            return false;
        }

        if (pagePropertyCount != pageCount.Value + 1)
        {
            cursor = start;
            return false;
        }

        var pageIdsLocalOffset = cursor;
        for (var i = 0; i < pageCount.Value; i++)
        {
            if (cursor + 4 > endOfStream)
            {
                cursor = start;
                return false;
            }

            pageIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)));
            cursor += 4;
        }

        properties["multiPagePageIds"] = pageIds.ToArray();
        properties["multiPagePageIdsLocalOffset"] = pageIdsLocalOffset;
        properties["multiPagePageIdsOffset"] = MsFormsBinary.OffsetAt(fileOffsets, pageIdsLocalOffset);
        properties["multiPagePageIdsEndLocalOffset"] = cursor;
        properties["multiPagePageIdsEndOffset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
        properties["multiPageXStreamLength"] = endOfStream;
        properties["multiPageXStreamValidation"] = cursor == endOfStream ? "exact" : $"remaining {endOfStream - cursor} bytes";
        return true;
    }
}

internal sealed record MultiPageXStreamProperties(
    Dictionary<string, object?> MultiPageProperties,
    IReadOnlyList<Dictionary<string, object?>> PageProperties,
    IReadOnlyList<uint> PageIds);
