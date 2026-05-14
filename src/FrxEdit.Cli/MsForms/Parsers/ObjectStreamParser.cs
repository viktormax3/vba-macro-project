internal static class ObjectStreamParser
{
    public static ObjectStreamProperties Read(StorageEntryDump stream, string? controlType = null)
    {
        if (controlType?.Equals("CommandButton", StringComparison.OrdinalIgnoreCase) == true &&
            TryReadCommandButtonObjectStream(stream) is { } commandButton)
        {
            return commandButton;
        }

        return ReadHeuristic(stream);
    }

    private static ObjectStreamProperties ReadHeuristic(StorageEntryDump stream)
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
        var fontRun = TextPropsParser.FindFontNameRun(data, textRuns);

        if (fontRun is not null)
        {
            properties["fontName"] = fontRun.Value.Text;
            properties["fontNameOffset"] = stream.FileOffsets[fontRun.Value.Offset];
            var fontSize = TextPropsParser.FindFontSizeBefore(data, fontRun.Value.Offset);
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
                var caption = MsFormsBinary.TrimLikelyPropertySuffix(rawCaption);
                var trailingFlags = MsFormsBinary.GetPrintableFlagSuffix(rawCaption, caption.Length);
                properties["captionRaw"] = rawCaption;
                properties["caption"] = caption;
                properties["captionOffset"] = stream.FileOffsets[captionRun.Value.Offset];
                if (captionRun.Value.Offset >= 2 && MsFormsBinary.IsPrintableAscii(data[captionRun.Value.Offset - 2]))
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
        if (MsFormsBinary.HasBit(propMask, 0))
        {
            MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, formatColor: true);
        }

        if (MsFormsBinary.HasBit(propMask, 1))
        {
            MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);
        }

        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var value = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            properties["variousPropertyBits"] = $"0x{value:X8}";
            AddVariousPropertyBits(properties, value);
        }

        if (MsFormsBinary.HasBit(propMask, 3))
        {
            MsFormsBinary.Align(ref cursor, 4);
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            captionCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(raw);
            properties["captionByteCount"] = captionCount.Value.Count;
            properties["captionCompressed"] = captionCount.Value.Compressed;
            properties["captionCountOffset"] = stream.FileOffsets[cursor];
            cursor += 4;
        }

        if (MsFormsBinary.HasBit(propMask, 4))
        {
            MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "picturePosition", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 6))
        {
            properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 7))
        {
            MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "pictureMarker", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 8))
        {
            var accelerator = MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "acceleratorCodeUnit", properties);
            if (accelerator != 0)
            {
                properties["accelerator"] = char.ConvertFromUtf32(accelerator);
                properties["acceleratorOffset"] = properties["acceleratorCodeUnitOffset"];
            }
        }

        if (MsFormsBinary.HasBit(propMask, 9))
        {
            properties["takeFocusOnClick"] = true;
        }

        if (MsFormsBinary.HasBit(propMask, 10))
        {
            MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);
        }

        MsFormsBinary.Align(ref cursor, 4);

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;
        if (captionCount is not null)
        {
            var captionOffset = cursor;
            var caption = MsFormsBinary.ReadFmString(data, captionOffset, captionCount.Value);
            properties["caption"] = caption;
            properties["captionRaw"] = caption;
            properties["captionOffset"] = stream.FileOffsets[captionOffset];
            cursor += captionCount.Value.Count;
            MsFormsBinary.Align(ref cursor, 4);
        }

        if (MsFormsBinary.HasBit(propMask, 5))
        {
            MsFormsBinary.Align(ref cursor, 4);
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

        if (cursor < data.Length)
        {
            properties["textPropsCandidateLocalOffset"] = cursor;
            properties["textPropsCandidateOffset"] = stream.FileOffsets[cursor];
        }

        if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out _))
        {
            TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static void AddVariousPropertyBits(Dictionary<string, object?> properties, uint value)
    {
        properties["enabled"] = MsFormsBinary.HasBit(value, 1);
        properties["locked"] = MsFormsBinary.HasBit(value, 2);
        properties["backStyle"] = MsFormsBinary.HasBit(value, 3) ? 1 : 0;
        properties["wordWrap"] = MsFormsBinary.HasBit(value, 23);
        properties["autoSize"] = MsFormsBinary.HasBit(value, 28);
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

    private static IReadOnlyList<TextRun> FindTextRuns(byte[] data, int minLength)
    {
        var runs = new List<TextRun>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (!MsFormsBinary.IsPrintableAscii(data[offset]))
            {
                offset++;
                continue;
            }

            var start = offset;
            while (offset < data.Length && MsFormsBinary.IsPrintableAscii(data[offset]))
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

    private static bool IsPlausiblePosition(int value) => value is >= -100_000 and <= 100_000;
}
