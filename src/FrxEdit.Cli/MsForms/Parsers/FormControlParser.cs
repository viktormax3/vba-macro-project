internal static class FormControlParser
{
    public static bool TryRead(StorageEntryDump stream, out FormControlProperties properties)
    {
        properties = default!;
        var data = stream.Data;
        var fileOffsets = stream.FileOffsets;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x04)
        {
            return false;
        }

        var minorVersion = data[0];
        var majorVersion = data[1];
        var cbForm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        if (cbForm < 4 || 4 + cbForm > data.Length)
        {
            return false;
        }

        var blockEnd = 4 + cbForm;
        var cursor = 4;
        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["formControlParser"] = "msOFormsFormControl",
            ["formMinorVersion"] = minorVersion,
            ["formMajorVersion"] = majorVersion,
            ["cbForm"] = cbForm,
            ["formPropMask"] = $"0x{propMask:X8}",
            ["formPropMaskOffset"] = OffsetAt(fileOffsets, 4),
        };

        CountOfBytesWithCompressionFlag? captionCount = null;

        try
        {
            // FormDataBlock, in the documented property-mask order.
            if (Has(propMask, 1)) ReadColor(data, fileOffsets, ref cursor, blockEnd, "formBackColor", result);
            if (Has(propMask, 2)) ReadColor(data, fileOffsets, ref cursor, blockEnd, "formForeColor", result);
            if (Has(propMask, 3)) ReadUInt32(data, fileOffsets, ref cursor, blockEnd, "nextAvailableId", result);
            if (Has(propMask, 6))
            {
                var value = ReadUInt32(data, fileOffsets, ref cursor, blockEnd, "formBooleanPropertiesRaw", result);
                result["formBooleanProperties"] = $"0x{value:X8}";
            }
            if (Has(propMask, 7)) ReadByte(data, fileOffsets, ref cursor, blockEnd, "formBorderStyle", result);
            if (Has(propMask, 8)) ReadByte(data, fileOffsets, ref cursor, blockEnd, "formMousePointer", result);
            if (Has(propMask, 9)) ReadByte(data, fileOffsets, ref cursor, blockEnd, "formScrollBars", result);
            Align(ref cursor, 4);
            if (Has(propMask, 13)) ReadInt32(data, fileOffsets, ref cursor, blockEnd, "formGroupCount", result);
            if (Has(propMask, 15)) ReadUInt16(data, fileOffsets, ref cursor, blockEnd, "formMouseIconMarker", result);
            if (Has(propMask, 16)) ReadByte(data, fileOffsets, ref cursor, blockEnd, "formCycle", result);
            if (Has(propMask, 17)) ReadByte(data, fileOffsets, ref cursor, blockEnd, "formSpecialEffect", result);
            Align(ref cursor, 4);
            if (Has(propMask, 18)) ReadColor(data, fileOffsets, ref cursor, blockEnd, "formBorderColor", result);
            Align(ref cursor, 4);
            int formCaptionCountLocalOffset = -1;
            if (Has(propMask, 19))
            {
                cursor = AlignTo(cursor, 4);
                formCaptionCountLocalOffset = cursor;
                var raw = ReadUInt32(data, fileOffsets, ref cursor, blockEnd, "formCaptionCountRaw", result);
                captionCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(raw);
                result["formCaptionByteCount"] = captionCount.Value.Count;
                result["formCaptionCompressed"] = captionCount.Value.Compressed;
                result["formCaptionPaddedByteCount"] = MsFormsBinary.Align4(captionCount.Value.Count);
                result["formCaptionCountLocalOffset"] = formCaptionCountLocalOffset;
            }
            Align(ref cursor, 2);
            if (Has(propMask, 20)) ReadUInt16(data, fileOffsets, ref cursor, blockEnd, "formFontMarker", result);
            Align(ref cursor, 2);
            if (Has(propMask, 21)) ReadUInt16(data, fileOffsets, ref cursor, blockEnd, "formPictureMarker", result);
            Align(ref cursor, 4);
            if (Has(propMask, 22)) ReadUInt32(data, fileOffsets, ref cursor, blockEnd, "formZoom", result);
            if (Has(propMask, 23)) ReadByte(data, fileOffsets, ref cursor, blockEnd, "formPictureAlignment", result);
            if (Has(propMask, 25)) ReadByte(data, fileOffsets, ref cursor, blockEnd, "formPictureSizeMode", result);
            Align(ref cursor, 4);
            if (Has(propMask, 26)) ReadUInt32(data, fileOffsets, ref cursor, blockEnd, "formShapeCookie", result);
            Align(ref cursor, 4);
            if (Has(propMask, 27)) ReadUInt32(data, fileOffsets, ref cursor, blockEnd, "formDrawBuffer", result);
            Align(ref cursor, 4);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            return false;
        }

        if (cursor > blockEnd)
        {
            return false;
        }

        cursor = AlignTo(cursor, 4);
        int? displayedWidth = null;
        int? displayedHeight = null;
        int? displayedWidthOffset = null;
        int? displayedHeightOffset = null;
        int? logicalWidth = null;
        int? logicalHeight = null;
        int? logicalWidthOffset = null;
        int? logicalHeightOffset = null;

        try
        {
            // FormExtraDataBlock, in documented order.
            if (Has(propMask, 10))
            {
                EnsureAvailable(cursor, 8, blockEnd);
                displayedWidthOffset = OffsetAt(fileOffsets, cursor);
                displayedWidth = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
                cursor += 4;
                displayedHeightOffset = OffsetAt(fileOffsets, cursor);
                displayedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
                cursor += 4;
                result["displayedWidth"] = displayedWidth;
                result["displayedHeight"] = displayedHeight;
                result["displayedWidthOffset"] = displayedWidthOffset;
                result["displayedHeightOffset"] = displayedHeightOffset;
            }

            if (Has(propMask, 11))
            {
                EnsureAvailable(cursor, 8, blockEnd);
                logicalWidthOffset = OffsetAt(fileOffsets, cursor);
                logicalWidth = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
                cursor += 4;
                logicalHeightOffset = OffsetAt(fileOffsets, cursor);
                logicalHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
                cursor += 4;
                result["logicalWidth"] = logicalWidth;
                result["logicalHeight"] = logicalHeight;
                result["logicalWidthOffset"] = logicalWidthOffset;
                result["logicalHeightOffset"] = logicalHeightOffset;
            }

            if (Has(propMask, 12))
            {
                EnsureAvailable(cursor, 8, blockEnd);
                result["scrollLeftOffset"] = OffsetAt(fileOffsets, cursor);
                result["scrollLeft"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
                cursor += 4;
                result["scrollTopOffset"] = OffsetAt(fileOffsets, cursor);
                result["scrollTop"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
                cursor += 4;
            }

            if (captionCount is not null)
            {
                EnsureAvailable(cursor, captionCount.Value.Count, blockEnd);
                result["formCaption"] = MsFormsBinary.ReadFmString(data, cursor, captionCount.Value);
                result["formCaptionOffset"] = OffsetAt(fileOffsets, cursor);
                var localCountOffset = result.TryGetValue("formCaptionCountLocalOffset", out var countLocal) && countLocal is int countLocalOffset
                    ? countLocalOffset
                    : -1;
                MsFormsBinary.AddStringSpan(result, "formCaption", captionCount.Value, localCountOffset, cursor, fileOffsets);
                cursor += Align4(captionCount.Value.Count);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            return false;
        }

        properties = new FormControlProperties(
            result,
            displayedWidth,
            displayedHeight,
            displayedWidthOffset,
            displayedHeightOffset,
            logicalWidth,
            logicalHeight,
            logicalWidthOffset,
            logicalHeightOffset);
        return true;
    }

    private static bool Has(uint value, int bit) => MsFormsBinary.HasBit(value, bit);

    private static int Align4(int count) => (count + 3) & ~3;

    private static void Align(ref int cursor, int alignment) => cursor = AlignTo(cursor, alignment);

    private static int AlignTo(int cursor, int alignment)
    {
        var remainder = cursor % alignment;
        return remainder == 0 ? cursor : cursor + alignment - remainder;
    }

    private static void EnsureAvailable(int cursor, int count, int end)
    {
        if (cursor < 0 || count < 0 || cursor + count > end)
        {
            throw new InvalidDataException();
        }
    }

    private static int OffsetAt(int[] fileOffsets, int streamOffset)
    {
        return streamOffset >= 0 && streamOffset < fileOffsets.Length ? fileOffsets[streamOffset] : 0;
    }

    private static uint ReadUInt32(byte[] data, int[] fileOffsets, ref int cursor, int end, string name, Dictionary<string, object?> properties)
    {
        Align(ref cursor, 4);
        EnsureAvailable(cursor, 4, end);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        properties[name] = value;
        properties[$"{name}Offset"] = OffsetAt(fileOffsets, cursor);
        cursor += 4;
        return value;
    }

    private static int ReadInt32(byte[] data, int[] fileOffsets, ref int cursor, int end, string name, Dictionary<string, object?> properties)
    {
        Align(ref cursor, 4);
        EnsureAvailable(cursor, 4, end);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
        properties[name] = value;
        properties[$"{name}Offset"] = OffsetAt(fileOffsets, cursor);
        cursor += 4;
        return value;
    }

    private static ushort ReadUInt16(byte[] data, int[] fileOffsets, ref int cursor, int end, string name, Dictionary<string, object?> properties)
    {
        Align(ref cursor, 2);
        EnsureAvailable(cursor, 2, end);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        properties[name] = value;
        properties[$"{name}Offset"] = OffsetAt(fileOffsets, cursor);
        cursor += 2;
        return value;
    }

    private static byte ReadByte(byte[] data, int[] fileOffsets, ref int cursor, int end, string name, Dictionary<string, object?> properties)
    {
        EnsureAvailable(cursor, 1, end);
        var value = data[cursor];
        properties[name] = value;
        properties[$"{name}Offset"] = OffsetAt(fileOffsets, cursor);
        cursor++;
        return value;
    }

    private static string ReadColor(byte[] data, int[] fileOffsets, ref int cursor, int end, string name, Dictionary<string, object?> properties)
    {
        var value = ReadUInt32(data, fileOffsets, ref cursor, end, name + "Raw", properties);
        var color = $"&H{value:X8}&";
        properties[name] = color;
        properties[$"{name}Offset"] = properties[$"{name}RawOffset"];
        return color;
    }
}
