internal interface IGeneratedControlSchema
{
    string Type { get; }
    uint SiteFlags { get; }
    byte[] BuildObjectPayload(GeneratedControlRequest request);
    IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize);
}

internal sealed class CommandButtonControlSchema : IGeneratedControlSchema
{
    public string Type => "CommandButton";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var caption = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        var captionBytes = Encoding.Latin1.GetBytes(caption);

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteCount(dataBlock, captionBytes.Length);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);

        var control = MsFormsFactoryBinary.BuildVersionedControl(0, 2, 0x0000_0028, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.CommandButtonMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.CommandButtonMask, request.Properties);
        metadata["caption"] = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        metadata["sizeSource"] = "commandButtonExtraDataBlock";
        metadata["parser"] = "msOFormsCommandButton";
        metadata["commandButtonPropMask"] = "0x00000028";
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = true;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        return metadata;
    }
}

internal sealed class LabelControlSchema : IGeneratedControlSchema
{
    public string Type => "Label";
    public uint SiteFlags => 0x0000_0032; // visible + streamed + autoSize, no tabStop.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var caption = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        var captionBytes = Encoding.Latin1.GetBytes(caption);

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteCount(dataBlock, captionBytes.Length);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);

        var control = MsFormsFactoryBinary.BuildVersionedControl(0, 2, 0x0000_0028, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.StandardMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.StandardMask, request.Properties);
        metadata["caption"] = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        metadata["sizeSource"] = "labelExtraDataBlock";
        metadata["parser"] = "msOFormsLabel";
        metadata["propMask"] = "0x00000028";
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = false;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        metadata["siteAutoSize"] = true;
        return metadata;
    }
}

internal sealed class TextBoxControlSchema : IGeneratedControlSchema
{
    public string Type => "TextBox";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var value = request.Value ?? MsFormsFactoryBinary.GetString(request.Properties, "value");
        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 0x2C80_481B); // default editable TextBox various bits observed in VBA/Corel fixtures.

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);
        if (!string.IsNullOrEmpty(value))
        {
            var valueBytes = Encoding.Latin1.GetBytes(value);
            MsFormsFactoryBinary.WriteCount(dataBlock, valueBytes.Length);
            extra.Write(valueBytes);
            MsFormsFactoryBinary.WritePadding(extra, 4);
        }

        var propMask = string.IsNullOrEmpty(value)
            ? 0x0000_0000_8000_0101ul
            : 0x0000_0000_8040_0101ul;
        var control = MsFormsFactoryBinary.BuildVersionedMorphControl(propMask, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.StandardMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.StandardMask, request.Properties);
        metadata["parser"] = "msOFormsMorphData";
        metadata["controlType"] = "TextBox";
        metadata["sizeSource"] = "morphDataExtraDataBlock";
        metadata["propMask"] = string.IsNullOrEmpty(request.Value)
            ? "0x0000000080000101"
            : "0x0000000080400101";
        metadata["variousPropertyBitsRaw"] = 0x2C80_481B;
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = true;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        if (!string.IsNullOrEmpty(request.Value))
        {
            metadata["value"] = request.Value;
        }

        return metadata;
    }
}
