internal static class ObjectPayloadSerializer
{
    public static byte[] SerializeFixedLength(ControlInfo control, ReadOnlySpan<byte> original)
    {
        // Pass 27 intentionally keeps each object payload the same length so FormSiteData and
        // ObjectStreamSize do not need to be regenerated yet.  The value is still written through
        // control-specific serializers, which lets us validate local offsets and prepare the code
        // path that future passes will use for variable-length serializers.
        var output = original.ToArray();
        var props = control.Properties;
        if (props is null)
        {
            return output;
        }

        if (!TryGetString(props, "parser", out var parser) || parser.Equals("heuristic", StringComparison.OrdinalIgnoreCase))
        {
            return output;
        }

        switch (control.Type)
        {
            case "CommandButton":
                SerializeCommandButton(control, props, output);
                break;
            case "Label":
                SerializeTextualControl(control, props, output, ["caption", "fontName"]);
                RewriteCommonColorsAndFont(props, output);
                break;
            case "TextBox":
            case "ComboBox":
            case "ListBox":
            case "CheckBox":
            case "OptionButton":
            case "ToggleButton":
                SerializeTextualControl(control, props, output, ["value", "caption", "groupName", "fontName"]);
                RewriteCommonColorsAndFont(props, output);
                RewriteUInt32(props, output, "variousPropertyBitsRaw");
                break;
            case "TabStrip":
                SerializeTextualControl(control, props, output, ["fontName"]);
                RewriteCommonColorsAndFont(props, output);
                break;
            case "Image":
            case "ScrollBar":
            case "SpinButton":
                RewriteCommonColorsAndFont(props, output);
                break;
        }

        return output;
    }

    private static void SerializeCommandButton(ControlInfo control, Dictionary<string, object?> props, byte[] output)
    {
        RewriteByte(props, output, "minorVersion", 0);
        RewriteByte(props, output, "majorVersion", 1);
        RewriteUInt16(props, output, "cbCommandButton", 2);
        RewriteHexUInt32(props, output, "commandButtonPropMask", 4);
        RewriteCommonColorsAndFont(props, output);
        SerializeTextualControl(control, props, output, ["caption", "fontName"]);
    }

    private static void SerializeTextualControl(ControlInfo control, Dictionary<string, object?> props, byte[] output, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (!props.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            if (!TryGetStringSpan(props, name, out var span))
            {
                continue;
            }

            WriteStringSpanFixedLength(control.Name, name, value.ToString() ?? string.Empty, span, output);
        }
    }

    private static void RewriteCommonColorsAndFont(Dictionary<string, object?> props, byte[] output)
    {
        RewriteColor(props, output, "foreColor");
        RewriteColor(props, output, "backColor");
        RewriteColor(props, output, "borderColor");
        RewriteUInt32(props, output, "fontHeightRaw");
    }

    private static void WriteStringSpanFixedLength(string controlName, string propertyName, string value, StringSpanInfo span, byte[] output)
    {
        if (span.DataLocalOffset < 0 || span.DataLocalOffset > output.Length)
        {
            throw new CliException($"Cannot serialize {controlName}.{propertyName}: data offset {span.DataLocalOffset} is outside the object payload.");
        }

        if (span.ByteCount < 0 || span.PaddedByteCount < 0 || span.DataLocalOffset + span.PaddedByteCount > output.Length)
        {
            throw new CliException($"Cannot serialize {controlName}.{propertyName}: span exceeds the object payload.");
        }

        var encoded = span.Compressed
            ? Encoding.Latin1.GetBytes(value)
            : Encoding.Unicode.GetBytes(value);

        // Fixed-length object serialization must preserve the original CountOfBytes value and its
        // aligned allocation.  This keeps every object slice exactly the same size while proving the
        // serializer can rewrite strings through the documented Count + fmString layout.
        if (encoded.Length > span.ByteCount)
        {
            throw new CliException($"Cannot fixed-length serialize {controlName}.{propertyName}: '{value}' needs {encoded.Length} bytes but the current counted span is {span.ByteCount} bytes. Use a future variable-length stream rebuild for this change.");
        }

        if (span.CountLocalOffset is int countOffset)
        {
            if (countOffset < 0 || countOffset + 4 > output.Length)
            {
                throw new CliException($"Cannot serialize {controlName}.{propertyName}: count offset {countOffset} is outside the object payload.");
            }

            var count = (uint)span.ByteCount;
            if (span.Compressed)
            {
                count |= 0x8000_0000u;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(countOffset, 4), count);
        }

        output.AsSpan(span.DataLocalOffset, span.PaddedByteCount).Clear();
        encoded.CopyTo(output.AsSpan(span.DataLocalOffset));
    }

    private static void RewriteColor(Dictionary<string, object?> props, byte[] output, string propertyName)
    {
        if (!TryGetString(props, propertyName, out var value) || !TryGetInt(props, $"{propertyName}LocalOffset", out var offset))
        {
            return;
        }

        if (offset < 0 || offset + 4 > output.Length)
        {
            return;
        }

        if (!TryParseVbaColor(value, out var raw))
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), raw);
    }

    private static void RewriteUInt32(Dictionary<string, object?> props, byte[] output, string propertyName)
    {
        if (!TryGetInt(props, propertyName, out var value) || !TryGetInt(props, $"{propertyName}LocalOffset", out var offset))
        {
            return;
        }

        if (offset < 0 || offset + 4 > output.Length)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), unchecked((uint)value));
    }

    private static void RewriteUInt16(Dictionary<string, object?> props, byte[] output, string propertyName, int? fixedLocalOffset = null)
    {
        if (!TryGetInt(props, propertyName, out var value))
        {
            return;
        }

        var offset = fixedLocalOffset ?? (TryGetInt(props, $"{propertyName}LocalOffset", out var local) ? local : -1);
        if (offset < 0 || offset + 2 > output.Length)
        {
            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset, 2), unchecked((ushort)value));
    }

    private static void RewriteByte(Dictionary<string, object?> props, byte[] output, string propertyName, int fixedLocalOffset)
    {
        if (!TryGetInt(props, propertyName, out var value))
        {
            return;
        }

        if (fixedLocalOffset < 0 || fixedLocalOffset >= output.Length)
        {
            return;
        }

        output[fixedLocalOffset] = unchecked((byte)value);
    }

    private static void RewriteHexUInt32(Dictionary<string, object?> props, byte[] output, string propertyName, int fixedLocalOffset)
    {
        if (!TryGetString(props, propertyName, out var text) || !TryParseHexUInt32(text, out var value))
        {
            return;
        }

        if (fixedLocalOffset < 0 || fixedLocalOffset + 4 > output.Length)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(fixedLocalOffset, 4), value);
    }

    private static bool TryParseVbaColor(string text, out uint value)
    {
        value = 0;
        text = text.Trim();
        if (text.StartsWith("&H", StringComparison.OrdinalIgnoreCase) && text.EndsWith('&'))
        {
            return uint.TryParse(text[2..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseHexUInt32(string text, out uint value)
    {
        value = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetString(Dictionary<string, object?> props, string key, out string value)
    {
        value = string.Empty;
        if (!props.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        value = raw.ToString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt(Dictionary<string, object?> props, string key, out int value)
    {
        value = 0;
        if (!props.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                value = (int)l;
                return true;
            case uint u when u <= int.MaxValue:
                value = (int)u;
                return true;
            case ushort us:
                value = us;
                return true;
            case byte b:
                value = b;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed):
                value = parsed;
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetBool(Dictionary<string, object?> props, string key, out bool value)
    {
        value = false;
        if (!props.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = element.GetBoolean();
                return true;
            case string text when bool.TryParse(text, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetStringSpan(Dictionary<string, object?> props, string propertyName, out StringSpanInfo span)
    {
        span = default;
        if (!props.TryGetValue($"{propertyName}Span", out var raw) || raw is null)
        {
            return false;
        }

        if (raw is Dictionary<string, object?> dict)
        {
            return TryGetStringSpan(dict, out span);
        }

        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            var spanDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                spanDict[property.Name] = property.Value;
            }
            return TryGetStringSpan(spanDict, out span);
        }

        return false;
    }

    private static bool TryGetStringSpan(Dictionary<string, object?> dict, out StringSpanInfo span)
    {
        span = default;
        if (!TryGetInt(dict, "dataLocalOffset", out var dataLocalOffset) ||
            !TryGetInt(dict, "byteCount", out var byteCount) ||
            !TryGetInt(dict, "paddedByteCount", out var paddedByteCount) ||
            !TryGetBool(dict, "compressed", out var compressed))
        {
            return false;
        }

        int? countLocalOffset = null;
        if (TryGetInt(dict, "countLocalOffset", out var countOffset))
        {
            countLocalOffset = countOffset;
        }

        span = new StringSpanInfo(dataLocalOffset, byteCount, paddedByteCount, compressed, countLocalOffset);
        return true;
    }

    private readonly record struct StringSpanInfo(
        int DataLocalOffset,
        int ByteCount,
        int PaddedByteCount,
        bool Compressed,
        int? CountLocalOffset);
}
