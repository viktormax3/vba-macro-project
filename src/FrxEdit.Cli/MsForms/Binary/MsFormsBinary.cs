internal static class MsFormsBinary
{
    public static bool HasBit(uint value, int bit) => (value & (1u << bit)) != 0;

    public static void Align(ref int cursor, int alignment)
    {
        var remainder = cursor % alignment;
        if (remainder != 0)
        {
            cursor += alignment - remainder;
        }
    }

    public static uint ReadAlignedUInt32(
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

    public static ushort ReadAlignedUInt16(
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

    public static byte ReadByte(
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

    public static CountOfBytesWithCompressionFlag DecodeCountOfBytesWithCompressionFlag(uint value) =>
        new((int)(value & 0x7FFF_FFFF), (value & 0x8000_0000) != 0);

    public static string ReadFmString(byte[] data, int offset, CountOfBytesWithCompressionFlag count)
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

    public static string TrimLikelyPropertySuffix(string value)
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

    public static string? GetPrintableFlagSuffix(string rawValue, int valueLength)
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

    public static string TrimTrailingBinaryChars(string value)
    {
        var end = value.Length;
        while (end > 0 && (value[end - 1] == '\0' || char.IsControl(value[end - 1])))
        {
            end--;
        }

        return end == value.Length ? value : value[..end];
    }

    public static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;
}
