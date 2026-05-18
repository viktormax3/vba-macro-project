internal static class GeneratedControlFactory
{
    private const int DefaultWidth = 72 * 2540 / 72;
    private const int DefaultHeight = 18 * 2540 / 72;

    public static bool CanCreate(string type) =>
        type.Equals("CommandButton", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Label", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("TextBox", StringComparison.OrdinalIgnoreCase);

    public static GeneratedControlBytes Create(
        string type,
        string name,
        int siteId,
        int tabIndex,
        int left,
        int top,
        int? rawWidth,
        int? rawHeight,
        string? caption,
        string? value,
        Dictionary<string, object?> properties)
    {
        if (!ControlTypeSchema.TryGetMsFormsTypeCode(type, out var typeCode))
        {
            throw new CliException($"Cannot create '{name}': unsupported MSForms type '{type}'.");
        }

        var width = rawWidth ?? DefaultWidth;
        var height = rawHeight ?? DefaultHeight;
        byte[] objectPayload;
        if (type.Equals("CommandButton", StringComparison.OrdinalIgnoreCase))
        {
            objectPayload = BuildCommandButtonPayload(caption ?? GetString(properties, "caption") ?? name, width, height, properties);
        }
        else if (type.Equals("Label", StringComparison.OrdinalIgnoreCase))
        {
            objectPayload = BuildLabelPayload(caption ?? GetString(properties, "caption") ?? name, width, height, properties);
        }
        else if (type.Equals("TextBox", StringComparison.OrdinalIgnoreCase))
        {
            objectPayload = BuildTextBoxPayload(value ?? GetString(properties, "value") ?? string.Empty, width, height, properties);
        }
        else
        {
            throw new CliException($"Cannot create '{name}': type '{type}' does not have a document-backed factory yet.");
        }

        var sitePayload = BuildOleSiteConcrete(name, siteId, tabIndex, typeCode, left, top, objectPayload.Length);
        return new GeneratedControlBytes(sitePayload, objectPayload);
    }

    private static byte[] BuildOleSiteConcrete(
        string name,
        int siteId,
        int tabIndex,
        byte typeCode,
        int left,
        int top,
        int objectStreamSize)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        using var dataBlock = new MemoryStream();
        WriteCount(dataBlock, nameBytes.Length, compressed: true);
        WriteUInt32(dataBlock, checked((uint)siteId));
        WriteUInt32(dataBlock, 0x0000_0013); // tabStop + visible + streamed
        WriteUInt32(dataBlock, checked((uint)objectStreamSize));
        WriteUInt16(dataBlock, checked((ushort)tabIndex));
        WriteUInt16(dataBlock, typeCode);

        using var extra = new MemoryStream();
        extra.Write(nameBytes);
        WritePadding(extra, 4);
        WriteInt32(extra, left);
        WriteInt32(extra, top);

        const uint propMask = 0x0000_01F5; // name, id, bitFlags, ObjectStreamSize, tabIndex, clsid, position
        return BuildVersionedBlock(version: 0, propMask, dataBlock.ToArray(), extra.ToArray());
    }

    private static byte[] BuildCommandButtonPayload(string caption, int width, int height, Dictionary<string, object?> properties)
    {
        var captionBytes = Encoding.Latin1.GetBytes(caption);
        using var dataBlock = new MemoryStream();
        WriteUInt32(dataBlock, ParseColor(GetString(properties, "foreColor"), 0x80000012));
        WriteUInt32(dataBlock, ParseColor(GetString(properties, "backColor"), 0x8000000F));
        WriteUInt32(dataBlock, 0x0000_000B); // enabled + unlocked + opaque
        WriteCount(dataBlock, captionBytes.Length, compressed: true);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        WritePadding(extra, 4);
        WriteInt32(extra, width);
        WriteInt32(extra, height);

        var control = BuildVersionedControl(minor: 0, major: 2, propMask: 0x0000_002F, dataBlock.ToArray(), extra.ToArray());
        return AppendTextProps(control, properties);
    }

    private static byte[] BuildLabelPayload(string caption, int width, int height, Dictionary<string, object?> properties)
    {
        var captionBytes = Encoding.Latin1.GetBytes(caption);
        using var dataBlock = new MemoryStream();
        WriteUInt32(dataBlock, ParseColor(GetString(properties, "foreColor"), 0x80000012));
        WriteUInt32(dataBlock, ParseColor(GetString(properties, "backColor"), 0x8000000F));
        WriteUInt32(dataBlock, 0x0000_000B);
        WriteCount(dataBlock, captionBytes.Length, compressed: true);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        WritePadding(extra, 4);
        WriteInt32(extra, width);
        WriteInt32(extra, height);

        var control = BuildVersionedControl(minor: 0, major: 2, propMask: 0x0000_002F, dataBlock.ToArray(), extra.ToArray());
        return AppendTextProps(control, properties);
    }

    private static byte[] BuildTextBoxPayload(string value, int width, int height, Dictionary<string, object?> properties)
    {
        var valueBytes = Encoding.Latin1.GetBytes(value);
        using var dataBlock = new MemoryStream();
        WriteUInt32(dataBlock, 0x0020_000B); // enabled + unlocked + opaque + wordWrap
        WriteUInt32(dataBlock, ParseColor(GetString(properties, "backColor"), 0x80000005));
        WriteUInt32(dataBlock, ParseColor(GetString(properties, "foreColor"), 0x80000008));
        WriteCount(dataBlock, valueBytes.Length, compressed: true);

        using var extra = new MemoryStream();
        WriteInt32(extra, width);
        WriteInt32(extra, height);
        extra.Write(valueBytes);
        WritePadding(extra, 4);

        var control = BuildVersionedMorphControl(propMask: 0x0000_0000_0040_0107ul, dataBlock.ToArray(), extra.ToArray());
        return AppendTextProps(control, properties);
    }

    private static byte[] AppendTextProps(byte[] control, Dictionary<string, object?> properties)
    {
        var fontName = GetString(properties, "fontName") ?? "Tahoma";
        var fontNameBytes = Encoding.Latin1.GetBytes(fontName);
        var fontSize = GetDouble(properties, "fontSize") ?? 8.0;
        var fontHeight = checked((uint)Math.Round(fontSize * 20.0, MidpointRounding.AwayFromZero));

        using var dataBlock = new MemoryStream();
        WriteCount(dataBlock, fontNameBytes.Length, compressed: true);
        WriteUInt32(dataBlock, 0x0000_0000);
        WriteUInt32(dataBlock, fontHeight);
        dataBlock.WriteByte(0);
        dataBlock.WriteByte(2);
        WritePaddingRelative(dataBlock, 4);

        using var extra = new MemoryStream();
        extra.Write(fontNameBytes);
        WritePadding(extra, 4);

        var textProps = BuildVersionedControl(minor: 0, major: 2, propMask: 0x0000_0037, dataBlock.ToArray(), extra.ToArray());
        return [.. control, .. textProps];
    }

    private static byte[] BuildVersionedBlock(ushort version, uint propMask, byte[] dataBlock, byte[] extraBlock)
    {
        using var output = new MemoryStream();
        WriteUInt16(output, version);
        WriteUInt16(output, checked((ushort)(4 + dataBlock.Length + extraBlock.Length)));
        WriteUInt32(output, propMask);
        output.Write(dataBlock);
        output.Write(extraBlock);
        return output.ToArray();
    }

    private static byte[] BuildVersionedControl(byte minor, byte major, uint propMask, byte[] dataBlock, byte[] extraBlock)
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

    private static byte[] BuildVersionedMorphControl(ulong propMask, byte[] dataBlock, byte[] extraBlock)
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

    private static string? GetString(Dictionary<string, object?> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static double? GetDouble(Dictionary<string, object?> properties, string name)
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

    private static uint ParseColor(string? text, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        text = text.Trim();
        if (text.StartsWith("&H", StringComparison.OrdinalIgnoreCase) && text.EndsWith('&') &&
            uint.TryParse(text[2..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vbaColor))
        {
            return vbaColor;
        }

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) ? raw : fallback;
    }

    private static void WriteCount(Stream stream, int count, bool compressed)
    {
        var raw = checked((uint)count);
        if (compressed)
        {
            raw |= 0x8000_0000;
        }

        WriteUInt32(stream, raw);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, checked((ushort)value));
        stream.Write(buffer);
    }

    private static void WritePadding(Stream stream, int alignment)
    {
        while (stream.Length % alignment != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WritePaddingRelative(Stream stream, int alignment) => WritePadding(stream, alignment);
}

internal sealed record GeneratedControlBytes(byte[] SitePayload, byte[] ObjectPayload);
