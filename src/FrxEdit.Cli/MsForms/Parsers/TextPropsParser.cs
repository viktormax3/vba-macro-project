internal static class TextPropsParser
{
    public static bool TryRead(
        byte[] data,
        int[] fileOffsets,
        int offset,
        Dictionary<string, object?> properties,
        out int endOffset)
    {
        endOffset = offset;
        if (offset < 0 ||
            offset + 8 > data.Length ||
            data[offset] != 0x00 ||
            data[offset + 1] != 0x02)
        {
            return false;
        }

        var cbTextProps = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        if (cbTextProps < 4 || offset + 4 + cbTextProps > data.Length)
        {
            return false;
        }

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
        const uint allowedTextPropsMask = 0x0000_00F7;
        if ((propMask & ~allowedTextPropsMask) != 0)
        {
            return false;
        }

        var cursor = offset + 8;
        var blockEnd = offset + 4 + cbTextProps;
        CountOfBytesWithCompressionFlag? fontNameCount = null;
        properties["textPropsParser"] = "msOFormsTextProps";
        properties["textPropsOffset"] = fileOffsets[offset];
        properties["textPropsLocalOffset"] = offset;
        properties["textPropsCb"] = cbTextProps;
        properties["textPropsPropMask"] = $"0x{propMask:X8}";
        properties["textPropsPropMaskOffset"] = fileOffsets[offset + 4];

        if (MsFormsBinary.HasBit(propMask, 0))
        {
            MsFormsBinary.Align(ref cursor, 4);
            if (cursor + 4 > blockEnd)
            {
                return false;
            }

            var raw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            fontNameCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(raw);
            properties["fontNameByteCount"] = fontNameCount.Value.Count;
            properties["fontNameCompressed"] = fontNameCount.Value.Compressed;
            properties["fontNameCountOffset"] = fileOffsets[cursor];
            cursor += 4;
        }

        if (MsFormsBinary.HasBit(propMask, 1))
        {
            MsFormsBinary.ReadAlignedUInt32(data, fileOffsets, ref cursor, 4, "fontEffects", properties);
            var effects = Convert.ToUInt32(properties["fontEffects"]);
            properties["fontEffectsHex"] = $"0x{effects:X8}";
            properties["fontItalic"] = MsFormsBinary.HasBit(effects, 1);
            properties["fontUnderline"] = MsFormsBinary.HasBit(effects, 2);
            properties["fontStrikethrough"] = MsFormsBinary.HasBit(effects, 3);
        }

        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var rawHeight = MsFormsBinary.ReadAlignedUInt32(data, fileOffsets, ref cursor, 4, "fontHeightRaw", properties);
            properties["fontSize"] = Math.Round(rawHeight / 20.0, 2);
            properties["fontSizeRaw"] = (int)rawHeight;
            properties["fontSizeOffset"] = properties["fontHeightRawOffset"];
        }

        if (MsFormsBinary.HasBit(propMask, 4))
        {
            properties["fontCharSet"] = MsFormsBinary.ReadByte(data, fileOffsets, ref cursor, "fontCharSet", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 5))
        {
            properties["fontPitchAndFamily"] = MsFormsBinary.ReadByte(data, fileOffsets, ref cursor, "fontPitchAndFamily", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 6))
        {
            properties["paragraphAlign"] = MsFormsBinary.ReadByte(data, fileOffsets, ref cursor, "paragraphAlign", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 7))
        {
            MsFormsBinary.ReadAlignedUInt16(data, fileOffsets, ref cursor, "fontWeight", properties);
        }

        MsFormsBinary.Align(ref cursor, 4);
        if (cursor > blockEnd)
        {
            return false;
        }

        if (fontNameCount is not null)
        {
            if (cursor + fontNameCount.Value.Count > data.Length)
            {
                return false;
            }

            properties["fontName"] = MsFormsBinary.ReadFmString(data, cursor, fontNameCount.Value);
            properties["fontNameOffset"] = fileOffsets[cursor];
            cursor += fontNameCount.Value.Count;
            MsFormsBinary.Align(ref cursor, 4);
        }

        if (cursor != blockEnd)
        {
            properties["textPropsParsedEndLocalOffset"] = cursor;
            properties["textPropsExpectedEndLocalOffset"] = blockEnd;
            properties["textPropsWarning"] = "Parsed TextProps length differs from cbTextProps.";
        }

        endOffset = blockEnd;
        return true;
    }

    public static void AddHeuristic(byte[] data, int[] fileOffsets, Dictionary<string, object?> properties)
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

    public static TextRun? FindFontNameRun(byte[] data, IReadOnlyList<TextRun> textRuns)
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

    public static FontSizeCandidate? FindFontSizeBefore(byte[] data, int fontNameOffset)
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
}
