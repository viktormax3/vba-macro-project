internal static class MsFormsFactoryBinary
{
    public static byte[] BuildVersionedControl(byte minor, byte major, uint propMask, byte[] dataBlock, byte[] extraBlock)
    {
        using var output = new MemoryStream();
        output.WriteByte(minor);
        output.WriteByte(major);
        WriteUInt16(output, checked((ushort)(4 + dataBlock.Length + extraBlock.Length)));
        WriteUInt32(output, propMask);
        output.Write(dataBlock);
        output.Write(extraBlock);
        return output.ToArray();
    }

    public static byte[] BuildVersionedMorphControl(ulong propMask, byte[] dataBlock, byte[] extraBlock)
    {
        using var output = new MemoryStream();
        output.WriteByte(0);
        output.WriteByte(2);
        WriteUInt16(output, checked((ushort)(8 + dataBlock.Length + extraBlock.Length)));
        Span<byte> mask = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(mask, propMask);
        output.Write(mask);
        output.Write(dataBlock);
        output.Write(extraBlock);
        return output.ToArray();
    }

    public static void WriteFmString(Stream stream, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        stream.Write(bytes);
        WritePadding(stream, 4);
    }

    public static void WriteCount(Stream stream, int count, bool compressed = true)
    {
        var raw = checked((uint)count);
        if (compressed)
        {
            raw |= 0x8000_0000;
        }

        WriteUInt32(stream, raw);
    }

    public static void WriteSize(Stream stream, int width, int height)
    {
        WriteInt32(stream, width);
        WriteInt32(stream, height);
    }

    public static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, checked((ushort)value));
        stream.Write(buffer);
    }

    public static void WritePadding(Stream stream, int alignment)
    {
        while (stream.Length % alignment != 0)
        {
            stream.WriteByte(0);
        }
    }

    public static string? GetString(Dictionary<string, object?> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value?.ToString() : null;

    public static double? GetDouble(Dictionary<string, object?> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            int i => i,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d) => d,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    public static uint ParseColor(string? text, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        text = text.Trim();
        if (text.StartsWith("&H", StringComparison.OrdinalIgnoreCase) &&
            text.EndsWith('&') &&
            uint.TryParse(text[2..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vbaColor))
        {
            return vbaColor;
        }

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) ? raw : fallback;
    }
}
