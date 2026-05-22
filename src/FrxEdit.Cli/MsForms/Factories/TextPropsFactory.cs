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

        using var dataBlock = new MemoryStream();
        if (HasBit(propMask, 0))
        {
            MsFormsFactoryBinary.WriteCount(dataBlock, fontNameBytes.Length);
        }

        if (HasBit(propMask, 1))
        {
            MsFormsFactoryBinary.WriteUInt32(dataBlock, 0);
        }

        if (HasBit(propMask, 2))
        {
            MsFormsFactoryBinary.WriteUInt32(dataBlock, fontHeight);
        }

        if (HasBit(propMask, 4))
        {
            dataBlock.WriteByte(0);
        }

        if (HasBit(propMask, 5))
        {
            dataBlock.WriteByte(2);
        }

        if (HasBit(propMask, 6))
        {
            dataBlock.WriteByte((byte)paragraphAlign);
        }

        if (HasBit(propMask, 7))
        {
            MsFormsFactoryBinary.WritePadding(dataBlock, 2);
            MsFormsFactoryBinary.WriteUInt16(dataBlock, 400);
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
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["fontName"] = MsFormsFactoryBinary.GetString(properties, "fontName") ?? "Tahoma",
            ["fontSize"] = fontSize,
            ["fontSizeRaw"] = checked((int)Math.Round(fontSize * 20.0, MidpointRounding.AwayFromZero)),
            ["fontCharSet"] = 0,
            ["fontPitchAndFamily"] = 2,
            ["textPropsPropMask"] = $"0x{propMask:X8}"
        };

        if (HasBit(propMask, 6))
        {
            var paragraphAlign = GetParagraphAlign(properties) ?? 3;
            metadata["paragraphAlign"] = paragraphAlign;
            metadata["textAlign"] = ParagraphAlignToTextAlign(paragraphAlign);
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
