internal static class TextPropsFactory
{
    public const uint StandardMask = 0x0000_0035; // FontName, FontHeight, FontCharSet, FontPitchAndFamily.
    public const uint CommandButtonMask = 0x0000_0075; // Standard + ParagraphAlign.

    public static byte[] Build(Dictionary<string, object?> properties, uint propMask)
    {
        var fontName = MsFormsFactoryBinary.GetString(properties, "fontName") ?? "Tahoma";
        var fontNameBytes = Encoding.Latin1.GetBytes(fontName);
        var fontSize = MsFormsFactoryBinary.GetDouble(properties, "fontSize") ?? 8.25;
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
            dataBlock.WriteByte((byte)(MsFormsFactoryBinary.GetDouble(properties, "paragraphAlign") ?? 3));
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
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["fontName"] = MsFormsFactoryBinary.GetString(properties, "fontName") ?? "Tahoma",
            ["fontSize"] = fontSize,
            ["fontSizeRaw"] = checked((int)Math.Round(fontSize * 20.0, MidpointRounding.AwayFromZero)),
            ["fontCharSet"] = 0,
            ["fontPitchAndFamily"] = 2,
            ["textPropsPropMask"] = $"0x{propMask:X8}"
        };
    }

    private static bool HasBit(uint value, int bit) => (value & (1u << bit)) != 0;
}
