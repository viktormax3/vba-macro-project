internal static class TextPropsFactory
{
    public const uint StandardMask = 0x0000_0035; // FontName, FontHeight, FontCharSet, FontPitchAndFamily.
    public const uint CommandButtonMask = 0x0000_0075; // Standard + ParagraphAlign.
    public const uint ParagraphAlignMask = 0x0000_0040;

    public static byte[] Build(Dictionary<string, object?> properties, uint propMask)
    {
        var fontName = MsFormsFactoryBinary.GetString(properties, "fontName") ?? "Tahoma";
        var fontNameBytes = Encoding.Latin1.GetBytes(fontName);
        var fontSize = MsFormsFactoryBinary.GetDouble(properties, "fontSize") ?? 8.25;
        var paragraphAlign = GetParagraphAlign(properties) ?? 3;
        var fontHeight = checked((uint)Math.Round(fontSize * 20.0, MidpointRounding.AwayFromZero));

        var fontItalic = MsFormsFactoryBinary.GetBool(properties, "fontItalic") ?? false;
        var fontUnderline = MsFormsFactoryBinary.GetBool(properties, "fontUnderline") ?? false;
        var fontStrikethrough = MsFormsFactoryBinary.GetBool(properties, "fontStrikethrough") ?? false;
        var isBold = MsFormsFactoryBinary.GetBool(properties, "fontBold") ?? false;
        var fontWeight = MsFormsFactoryBinary.GetInt32(properties, "fontWeight") ?? (isBold ? 700 : 400);
        var fontCharSet = MsFormsFactoryBinary.GetInt32(properties, "fontCharSet") ?? 0;
        var fontPitchAndFamily = MsFormsFactoryBinary.GetInt32(properties, "fontPitchAndFamily") ?? 2;

        if (fontItalic || fontUnderline || fontStrikethrough)
        {
            propMask |= 1u << 1;
        }
        if (fontWeight != 400)
        {
            propMask |= 1u << 7;
        }

        uint fontEffects = 0;
        if (fontItalic) fontEffects |= 1u << 1;
        if (fontUnderline) fontEffects |= 1u << 2;
        if (fontStrikethrough) fontEffects |= 1u << 3;

        using var dataBlock = new MemoryStream();
        if (HasBit(propMask, 0))
        {
            MsFormsFactoryBinary.WriteCount(dataBlock, fontNameBytes.Length);
        }

        if (HasBit(propMask, 1))
        {
            MsFormsFactoryBinary.WriteUInt32(dataBlock, fontEffects);
        }

        if (HasBit(propMask, 2))
        {
            MsFormsFactoryBinary.WriteUInt32(dataBlock, fontHeight);
        }

        if (HasBit(propMask, 4))
        {
            dataBlock.WriteByte((byte)fontCharSet);
        }

        if (HasBit(propMask, 5))
        {
            dataBlock.WriteByte((byte)fontPitchAndFamily);
        }

        if (HasBit(propMask, 6))
        {
            dataBlock.WriteByte((byte)paragraphAlign);
        }

        if (HasBit(propMask, 7))
        {
            MsFormsFactoryBinary.WritePadding(dataBlock, 2);
            MsFormsFactoryBinary.WriteUInt16(dataBlock, (ushort)fontWeight);
        }

        MsFormsFactoryBinary.WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        if (HasBit(propMask, 0))
        {
            extra.Write(fontNameBytes);
            MsFormsFactoryBinary.WritePadding(extra, 4);
        }

        return MsFormsFactoryBinary.BuildVersionedControl(0, 2, propMask, dataBlock.ToArray(), extra.ToArray());
    }

    public static Dictionary<string, object?> BuildMetadata(uint propMask, Dictionary<string, object?> properties)
    {
        var fontSize = MsFormsFactoryBinary.GetDouble(properties, "fontSize") ?? 8.25;
        var fontItalic = MsFormsFactoryBinary.GetBool(properties, "fontItalic") ?? false;
        var fontUnderline = MsFormsFactoryBinary.GetBool(properties, "fontUnderline") ?? false;
        var fontStrikethrough = MsFormsFactoryBinary.GetBool(properties, "fontStrikethrough") ?? false;
        var isBold = MsFormsFactoryBinary.GetBool(properties, "fontBold") ?? false;
        var fontWeight = MsFormsFactoryBinary.GetInt32(properties, "fontWeight") ?? (isBold ? 700 : 400);
        var fontCharSet = MsFormsFactoryBinary.GetInt32(properties, "fontCharSet") ?? 0;
        var fontPitchAndFamily = MsFormsFactoryBinary.GetInt32(properties, "fontPitchAndFamily") ?? 2;

        if (fontItalic || fontUnderline || fontStrikethrough)
        {
            propMask |= 1u << 1;
        }
        if (fontWeight != 400)
        {
            propMask |= 1u << 7;
        }

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["fontName"] = MsFormsFactoryBinary.GetString(properties, "fontName") ?? "Tahoma",
            ["fontSize"] = fontSize,
            ["fontSizeRaw"] = checked((int)Math.Round(fontSize * 20.0, MidpointRounding.AwayFromZero)),
            ["fontCharSet"] = fontCharSet,
            ["fontPitchAndFamily"] = fontPitchAndFamily,
            ["textPropsPropMask"] = $"0x{propMask:X8}"
        };

        if (HasBit(propMask, 1))
        {
            uint fontEffects = 0;
            if (fontItalic) fontEffects |= 1u << 1;
            if (fontUnderline) fontEffects |= 1u << 2;
            if (fontStrikethrough) fontEffects |= 1u << 3;

            metadata["fontEffectsHex"] = $"0x{fontEffects:X8}";
            metadata["fontItalic"] = fontItalic;
            metadata["fontUnderline"] = fontUnderline;
            metadata["fontStrikethrough"] = fontStrikethrough;
        }

        if (HasBit(propMask, 6))
        {
            var paragraphAlign = GetParagraphAlign(properties) ?? 3;
            metadata["paragraphAlign"] = paragraphAlign;
            metadata["textAlign"] = ParagraphAlignToTextAlign(paragraphAlign);
        }

        if (HasBit(propMask, 7))
        {
            metadata["fontWeight"] = fontWeight;
            metadata["fontBold"] = fontWeight >= 700;
        }

        return metadata;
    }

    private static bool HasBit(uint value, int bit) => (value & (1u << bit)) != 0;

    public static uint WithParagraphAlignIfNeeded(uint propMask, Dictionary<string, object?> properties)
    {
        var paragraphAlign = GetParagraphAlign(properties);
        return paragraphAlign is null || paragraphAlign.Value == 1
            ? propMask
            : propMask | ParagraphAlignMask;
    }

    public static int? GetParagraphAlign(Dictionary<string, object?> properties)
    {
        if (MsFormsFactoryBinary.GetInt32(properties, "paragraphAlign") is int paragraphAlign)
        {
            return paragraphAlign;
        }

        if (MsFormsFactoryBinary.GetString(properties, "textAlign") is { } textAlignText &&
            TryParseTextAlign(textAlignText, out var parsedTextAlign))
        {
            return TextAlignToParagraphAlign(parsedTextAlign);
        }

        return null;
    }

    public static bool TryParseTextAlign(string value, out int textAlign)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "left":
                textAlign = 1;
                return true;
            case "center":
            case "centre":
                textAlign = 2;
                return true;
            case "right":
                textAlign = 3;
                return true;
            default:
                return int.TryParse(value, CultureInfo.InvariantCulture, out textAlign);
        }
    }

    public static string TextAlignName(int textAlign) =>
        textAlign switch
        {
            1 => "left",
            2 => "center",
            3 => "right",
            _ => textAlign.ToString(CultureInfo.InvariantCulture)
        };

    public static int TextAlignToParagraphAlign(int textAlign) =>
        textAlign switch
        {
            1 => 1,
            2 => 3,
            3 => 2,
            _ => textAlign
        };

    public static int ParagraphAlignToTextAlign(int paragraphAlign) =>
        paragraphAlign switch
        {
            1 => 1,
            2 => 3,
            3 => 2,
            _ => paragraphAlign
        };
}
