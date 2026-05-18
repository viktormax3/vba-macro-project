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
        var value = request.Value ?? MsFormsFactoryBinary.GetString(request.Properties, "value");
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.StandardMask, request.Properties);
        metadata["parser"] = "msOFormsMorphData";
        metadata["controlType"] = "TextBox";
        metadata["sizeSource"] = "morphDataExtraDataBlock";
        metadata["propMask"] = string.IsNullOrEmpty(value)
            ? "0x0000000080000101"
            : "0x0000000080400101";
        metadata["variousPropertyBitsRaw"] = 0x2C80_481B;
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = true;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        if (!string.IsNullOrEmpty(value))
        {
            metadata["value"] = value;
        }

        return metadata;
    }
}

internal abstract class MorphButtonControlSchema : IGeneratedControlSchema
{
    private const ulong MorphButtonPropMask = 0x0000_0000_80C0_0146ul;
    private const uint DefaultBackColor = 0x8000_000F;
    private const uint DefaultForeColor = 0x8000_0012;

    public abstract string Type { get; }
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.
    protected abstract byte DisplayStyle { get; }
    protected virtual uint TextPropsMask => TextPropsFactory.StandardMask;

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var value = request.Value ?? MsFormsFactoryBinary.GetString(request.Properties, "value") ?? "0";
        var caption = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        var valueBytes = Encoding.Latin1.GetBytes(value);
        var captionBytes = Encoding.Latin1.GetBytes(caption);
        var backColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "backColor"), DefaultBackColor);
        var foreColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "foreColor"), DefaultForeColor);

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, backColor);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, foreColor);
        dataBlock.WriteByte(DisplayStyle);
        MsFormsFactoryBinary.WritePadding(dataBlock, 4);
        MsFormsFactoryBinary.WriteCount(dataBlock, valueBytes.Length);
        MsFormsFactoryBinary.WriteCount(dataBlock, captionBytes.Length);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);
        extra.Write(valueBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);
        extra.Write(captionBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);

        var control = MsFormsFactoryBinary.BuildVersionedMorphControl(MorphButtonPropMask, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var metadata = TextPropsFactory.BuildMetadata(TextPropsMask, request.Properties);
        metadata["parser"] = "msOFormsMorphData";
        metadata["controlType"] = Type;
        metadata["sizeSource"] = "morphDataExtraDataBlock";
        metadata["propMask"] = "0x0000000080C00146";
        metadata["backColor"] = MsFormsFactoryBinary.GetString(request.Properties, "backColor") ?? "&H8000000F&";
        metadata["foreColor"] = MsFormsFactoryBinary.GetString(request.Properties, "foreColor") ?? "&H80000012&";
        metadata["displayStyle"] = DisplayStyle;
        metadata["value"] = request.Value ?? MsFormsFactoryBinary.GetString(request.Properties, "value") ?? "0";
        metadata["caption"] = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = true;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        return metadata;
    }
}

internal sealed class CheckBoxControlSchema : MorphButtonControlSchema
{
    public override string Type => "CheckBox";
    protected override byte DisplayStyle => 4;
}

internal sealed class OptionButtonControlSchema : MorphButtonControlSchema
{
    public override string Type => "OptionButton";
    protected override byte DisplayStyle => 5;
}

internal sealed class ToggleButtonControlSchema : MorphButtonControlSchema
{
    public override string Type => "ToggleButton";
    protected override byte DisplayStyle => 6;
    protected override uint TextPropsMask => TextPropsFactory.CommandButtonMask;
}

internal sealed class ComboBoxControlSchema : IGeneratedControlSchema
{
    private const ulong PropMask = 0x0000_0000_8005_0141ul;
    private const uint VariousPropertyBits = 0x2C80_481B;

    public string Type => "ComboBox";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, VariousPropertyBits);
        dataBlock.WriteByte(3); // fmDisplayStyleDropDownCombo.
        dataBlock.WriteByte(1); // fmMatchEntryComplete.
        dataBlock.WriteByte(2); // fmShowDropButtonWhenFocus.
        MsFormsFactoryBinary.WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);

        var control = MsFormsFactoryBinary.BuildVersionedMorphControl(PropMask, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.StandardMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.StandardMask, request.Properties);
        metadata["parser"] = "msOFormsMorphData";
        metadata["controlType"] = Type;
        metadata["sizeSource"] = "morphDataExtraDataBlock";
        metadata["propMask"] = "0x0000000080050141";
        metadata["variousPropertyBitsRaw"] = VariousPropertyBits;
        metadata["displayStyle"] = 3;
        metadata["matchEntry"] = 1;
        metadata["showDropButtonWhen"] = 2;
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = true;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        return metadata;
    }
}

internal sealed class ListBoxControlSchema : IGeneratedControlSchema
{
    private const ulong PropMask = 0x0000_0000_8001_0160ul;

    public string Type => "ListBox";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        using var dataBlock = new MemoryStream();
        dataBlock.WriteByte(3); // fmScrollBarsBoth.
        dataBlock.WriteByte(2); // fmDisplayStyleList.
        dataBlock.WriteByte(0); // fmMatchEntryFirstLetter.
        MsFormsFactoryBinary.WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);

        var control = MsFormsFactoryBinary.BuildVersionedMorphControl(PropMask, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.StandardMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.StandardMask, request.Properties);
        metadata["parser"] = "msOFormsMorphData";
        metadata["controlType"] = Type;
        metadata["sizeSource"] = "morphDataExtraDataBlock";
        metadata["propMask"] = "0x0000000080010160";
        metadata["scrollBars"] = 3;
        metadata["displayStyle"] = 2;
        metadata["matchEntry"] = 0;
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = true;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        return metadata;
    }
}

internal abstract class SpinLikeControlSchema : IGeneratedControlSchema
{
    public abstract string Type { get; }
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.
    protected abstract uint PropMask { get; }
    protected abstract string Parser { get; }
    protected abstract string SizeSource { get; }
    protected virtual string PropMaskName => "propMask";
    protected virtual int DefaultOrientation => -1;

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var orientation = MsFormsFactoryBinary.GetInt32(request.Properties, "orientation") ?? DefaultOrientation;

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteInt32(dataBlock, orientation);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);

        return MsFormsFactoryBinary.BuildVersionedControl(0, 2, PropMask, dataBlock.ToArray(), extra.ToArray());
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var orientation = MsFormsFactoryBinary.GetInt32(request.Properties, "orientation") ?? DefaultOrientation;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = Parser,
            ["sizeSource"] = SizeSource,
            [PropMaskName] = $"0x{PropMask:X8}",
            ["orientation"] = orientation,
            ["objectStreamSize"] = objectPayloadSize,
            ["siteBitFlags"] = $"0x{SiteFlags:X8}",
            ["tabStop"] = true,
            ["visible"] = true,
            ["streamed"] = true
        };
    }
}

internal sealed class ScrollBarControlSchema : SpinLikeControlSchema
{
    public override string Type => "ScrollBar";
    protected override uint PropMask => 0x0000_2008;
    protected override string Parser => "msOFormsScrollBar";
    protected override string SizeSource => "scrollBarExtraDataBlock";
}

internal sealed class SpinButtonControlSchema : SpinLikeControlSchema
{
    public override string Type => "SpinButton";
    protected override uint PropMask => 0x0000_0808;
    protected override string Parser => "msOFormsSpinButton";
    protected override string SizeSource => "spinButtonExtraDataBlock";
}

internal sealed class ImageControlSchema : IGeneratedControlSchema
{
    private const uint PropMask = 0x0000_0200;

    public string Type => "Image";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);
        return MsFormsFactoryBinary.BuildVersionedControl(0, 2, PropMask, [], extra.ToArray());
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize) =>
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsImage",
            ["sizeSource"] = "imageExtraDataBlock",
            ["propMask"] = "0x00000200",
            ["objectStreamSize"] = objectPayloadSize,
            ["siteBitFlags"] = $"0x{SiteFlags:X8}",
            ["tabStop"] = true,
            ["visible"] = true,
            ["streamed"] = true
        };
}

internal sealed class TabStripControlSchema : IGeneratedControlSchema
{
    private const uint PropMask = 0x00FA_8031;

    public string Type => "TabStrip";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var captions = GetTabCaptions(request);
        var names = GetTabNames(request, captions);
        var emptyStrings = Enumerable.Repeat(string.Empty, captions.Count).ToArray();
        var captionBlock = BuildArrayStringBlock(captions);
        var tooltipBlock = BuildArrayStringBlock(emptyStrings);
        var nameBlock = BuildArrayStringBlock(names);
        var tagBlock = BuildArrayStringBlock(emptyStrings);
        var acceleratorBlock = BuildArrayStringBlock(emptyStrings);

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteInt32(dataBlock, 0);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)captionBlock.Length));
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)tooltipBlock.Length));
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)nameBlock.Length));
        MsFormsFactoryBinary.WriteInt32(dataBlock, captions.Count + 1);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)tagBlock.Length));
        MsFormsFactoryBinary.WriteInt32(dataBlock, captions.Count);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)acceleratorBlock.Length));

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);
        extra.Write(captionBlock);
        extra.Write(tooltipBlock);
        extra.Write(nameBlock);
        extra.Write(tagBlock);
        extra.Write(acceleratorBlock);

        var control = MsFormsFactoryBinary.BuildVersionedControl(0, 2, PropMask, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.StandardMask);

        using var tabFlags = new MemoryStream();
        for (var i = 0; i < captions.Count; i++)
        {
            MsFormsFactoryBinary.WriteUInt32(tabFlags, 0x0000_0003);
        }

        return [.. control, .. textProps, .. tabFlags.ToArray()];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var captions = GetTabCaptions(request);
        var names = GetTabNames(request, captions);
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.StandardMask, request.Properties);
        metadata["parser"] = "msOFormsTabStrip";
        metadata["sizeSource"] = "tabStripExtraDataBlock";
        metadata["propMask"] = $"0x{PropMask:X8}";
        metadata["listIndex"] = 0;
        metadata["tabsAllocated"] = captions.Count + 1;
        metadata["tabData"] = captions.Count;
        metadata["tabCaptions"] = captions;
        metadata["tabNames"] = names;
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = true;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        return metadata;
    }

    private static IReadOnlyList<string> GetTabCaptions(GeneratedControlRequest request)
    {
        var captions = MsFormsFactoryBinary.GetStringList(request.Properties, "tabCaptions");
        if (captions is { Count: > 0 })
        {
            return captions;
        }

        if (!string.IsNullOrWhiteSpace(request.Caption))
        {
            return [request.Caption!, "Tab2"];
        }

        return ["Tab1", "Tab2"];
    }

    private static IReadOnlyList<string> GetTabNames(GeneratedControlRequest request, IReadOnlyList<string> captions)
    {
        var names = MsFormsFactoryBinary.GetStringList(request.Properties, "tabNames");
        if (names is not null && names.Count == captions.Count)
        {
            return names;
        }

        return captions.Select((_, index) => $"Tab{index + 1}").ToArray();
    }

    private static byte[] BuildArrayStringBlock(IReadOnlyList<string> values)
    {
        using var block = new MemoryStream();
        foreach (var value in values)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            MsFormsFactoryBinary.WriteCount(block, bytes.Length, compressed: bytes.Length > 0);
            block.Write(bytes);
            MsFormsFactoryBinary.WritePadding(block, 4);
        }

        return block.ToArray();
    }
}
