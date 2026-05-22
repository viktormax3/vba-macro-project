internal interface IGeneratedControlSchema
{
    string Type { get; }
    uint SiteFlags { get; }
    byte[] BuildObjectPayload(GeneratedControlRequest request);
    IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize);
}

internal sealed class CommandButtonControlSchema : IGeneratedControlSchema
{
    private const uint DefaultForeColor = 0x8000_0012;
    private const uint DefaultBackColor = 0x8000_000F;
    private const uint DefaultVariousPropertyBits = 0x0000_001B;
    private const uint DefaultPicturePosition = 0x0007_0001;

    public string Type => "CommandButton";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var caption = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        var captionBytes = Encoding.Latin1.GetBytes(caption);
        var foreColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "foreColor"), DefaultForeColor);
        var backColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "backColor"), DefaultBackColor);
        var variousBits = BuildVariousPropertyBits(request.Properties);
        var picturePosition = MsFormsFactoryBinary.GetInt32(request.Properties, "picturePosition");
        var mousePointer = MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer");
        var accelerator = MsFormsFactoryBinary.GetString(request.Properties, "accelerator");
        var takeFocusOnClick = MsFormsFactoryBinary.GetBool(request.Properties, "takeFocusOnClick");

        uint propMask = 0x0000_0028; // Caption + Size.
        if (foreColor != DefaultForeColor) propMask |= 1u << 0;
        if (backColor != DefaultBackColor) propMask |= 1u << 1;
        if (variousBits != DefaultVariousPropertyBits) propMask |= 1u << 2;
        if (picturePosition is not null && unchecked((uint)picturePosition.Value) != DefaultPicturePosition) propMask |= 1u << 4;
        if (mousePointer is not null && mousePointer.Value != 0) propMask |= 1u << 6;
        if (!string.IsNullOrEmpty(accelerator)) propMask |= 1u << 8;
        if (takeFocusOnClick is false) propMask |= 1u << 9;

        using var dataBlock = new MemoryStream();
        if ((propMask & (1u << 0)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, foreColor);
        if ((propMask & (1u << 1)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, backColor);
        if ((propMask & (1u << 2)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, variousBits);
        MsFormsFactoryBinary.WriteCount(dataBlock, captionBytes.Length);
        if ((propMask & (1u << 4)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, unchecked((uint)picturePosition!.Value));
        if ((propMask & (1u << 6)) != 0)
        {
            dataBlock.WriteByte(checked((byte)mousePointer!.Value));
            MsFormsFactoryBinary.WritePadding(dataBlock, 2);
        }

        if ((propMask & (1u << 8)) != 0)
        {
            var code = string.IsNullOrEmpty(accelerator) ? 0 : accelerator![0];
            MsFormsFactoryBinary.WriteUInt16(dataBlock, code);
        }

        MsFormsFactoryBinary.WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);

        var control = MsFormsFactoryBinary.BuildVersionedControl(0, 2, propMask, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.CommandButtonMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.CommandButtonMask, request.Properties);
        var foreColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "foreColor"), DefaultForeColor);
        var backColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "backColor"), DefaultBackColor);
        var variousBits = BuildVariousPropertyBits(request.Properties);
        var propMask = 0x0000_0028u;
        if (foreColor != DefaultForeColor) propMask |= 1u << 0;
        if (backColor != DefaultBackColor) propMask |= 1u << 1;
        if (variousBits != DefaultVariousPropertyBits) propMask |= 1u << 2;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "picturePosition") is int picturePosition && unchecked((uint)picturePosition) != DefaultPicturePosition) propMask |= 1u << 4;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer") is int mousePointer && mousePointer != 0) propMask |= 1u << 6;
        if (!string.IsNullOrEmpty(MsFormsFactoryBinary.GetString(request.Properties, "accelerator"))) propMask |= 1u << 8;
        if (MsFormsFactoryBinary.GetBool(request.Properties, "takeFocusOnClick") is false) propMask |= 1u << 9;

        metadata["caption"] = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        metadata["sizeSource"] = "commandButtonExtraDataBlock";
        metadata["parser"] = "msOFormsCommandButton";
        metadata["commandButtonPropMask"] = $"0x{propMask:X8}";
        metadata["variousPropertyBitsRaw"] = unchecked((int)variousBits);
        metadata["enabled"] = (variousBits & (1u << 1)) != 0;
        metadata["locked"] = (variousBits & (1u << 2)) != 0;
        metadata["backStyle"] = (variousBits & (1u << 3)) != 0 ? 1 : 0;
        metadata["wordWrap"] = (variousBits & (1u << 23)) != 0;
        metadata["autoSize"] = (variousBits & (1u << 28)) != 0;
        metadata["foreColor"] = $"&H{foreColor:X8}&";
        metadata["backColor"] = $"&H{backColor:X8}&";
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "picturePosition") is int picturePositionValue) metadata["picturePosition"] = picturePositionValue;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer") is int mousePointerValue) metadata["mousePointer"] = mousePointerValue;
        if (MsFormsFactoryBinary.GetString(request.Properties, "accelerator") is { Length: > 0 } accelerator) metadata["accelerator"] = accelerator[0].ToString();
        metadata["takeFocusOnClick"] = MsFormsFactoryBinary.GetBool(request.Properties, "takeFocusOnClick") ?? true;
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{BuildSiteFlags(request.Properties):X8}";
        metadata["tabStop"] = MsFormsFactoryBinary.GetBool(request.Properties, "tabStop") ?? true;
        metadata["visible"] = MsFormsFactoryBinary.GetBool(request.Properties, "visible") ?? true;
        metadata["default"] = MsFormsFactoryBinary.GetBool(request.Properties, "default") ?? false;
        metadata["cancel"] = MsFormsFactoryBinary.GetBool(request.Properties, "cancel") ?? false;
        metadata["streamed"] = true;
        return metadata;
    }

    private static uint BuildVariousPropertyBits(Dictionary<string, object?> properties)
    {
        var bits = DefaultVariousPropertyBits;
        SetBit(ref bits, 1, MsFormsFactoryBinary.GetBool(properties, "enabled"));
        SetBit(ref bits, 2, MsFormsFactoryBinary.GetBool(properties, "locked"));
        if (MsFormsFactoryBinary.GetInt32(properties, "backStyle") is int backStyle)
        {
            SetBit(ref bits, 3, backStyle != 0);
        }
        SetBit(ref bits, 23, MsFormsFactoryBinary.GetBool(properties, "wordWrap"));
        SetBit(ref bits, 28, MsFormsFactoryBinary.GetBool(properties, "autoSize"));
        if (MsFormsFactoryBinary.GetInt32(properties, "imeMode") is int imeMode)
        {
            bits &= ~(0xFu << 15);
            bits |= ((uint)imeMode & 0xFu) << 15;
        }

        return bits;
    }

    private static uint BuildSiteFlags(Dictionary<string, object?> properties)
    {
        var flags = 0x0000_0013u;
        SetBit(ref flags, 0, MsFormsFactoryBinary.GetBool(properties, "tabStop"));
        SetBit(ref flags, 1, MsFormsFactoryBinary.GetBool(properties, "visible"));
        SetBit(ref flags, 2, MsFormsFactoryBinary.GetBool(properties, "default"));
        SetBit(ref flags, 3, MsFormsFactoryBinary.GetBool(properties, "cancel"));
        return flags;
    }

    private static void SetBit(ref uint bits, int bit, bool? value)
    {
        if (value is null) return;
        var mask = 1u << bit;
        bits = value.Value ? bits | mask : bits & ~mask;
    }
}

internal sealed class LabelControlSchema : IGeneratedControlSchema
{
    private const uint DefaultForeColor = 0x8000_0012;
    private const uint DefaultBackColor = 0x8000_000F;
    private const uint DefaultBorderColor = 0x8000_0006;
    private const uint DefaultVariousPropertyBits = 0x0080_0013;
    private const uint DefaultPicturePosition = 0x0007_0001;

    public string Type => "Label";
    public uint SiteFlags => 0x0000_0032; // visible + streamed + autoSize, no tabStop.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var caption = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        var captionBytes = Encoding.Latin1.GetBytes(caption);
        var foreColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "foreColor"), DefaultForeColor);
        var backColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "backColor"), DefaultBackColor);
        var borderColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "borderColor"), DefaultBorderColor);
        var variousBits = BuildVariousPropertyBits(request.Properties);
        var picturePosition = MsFormsFactoryBinary.GetInt32(request.Properties, "picturePosition");
        var mousePointer = MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer");
        var borderStyle = MsFormsFactoryBinary.GetInt32(request.Properties, "borderStyle");
        var specialEffect = MsFormsFactoryBinary.GetInt32(request.Properties, "specialEffect");
        var accelerator = MsFormsFactoryBinary.GetString(request.Properties, "accelerator");

        uint propMask = 0x0000_0028; // Caption + Size.
        if (foreColor != DefaultForeColor) propMask |= 1u << 0;
        if (backColor != DefaultBackColor) propMask |= 1u << 1;
        if (variousBits != DefaultVariousPropertyBits) propMask |= 1u << 2;
        if (picturePosition is not null && unchecked((uint)picturePosition.Value) != DefaultPicturePosition) propMask |= 1u << 4;
        if (mousePointer is not null && mousePointer.Value != 0) propMask |= 1u << 6;
        if (borderColor != DefaultBorderColor) propMask |= 1u << 7;
        if (borderStyle is not null && borderStyle.Value != 0) propMask |= 1u << 8;
        if (specialEffect is not null && specialEffect.Value != 0) propMask |= 1u << 9;
        if (!string.IsNullOrEmpty(accelerator)) propMask |= 1u << 11;

        using var dataBlock = new MemoryStream();
        if ((propMask & (1u << 0)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, foreColor);
        if ((propMask & (1u << 1)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, backColor);
        if ((propMask & (1u << 2)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, variousBits);
        MsFormsFactoryBinary.WriteCount(dataBlock, captionBytes.Length);
        if ((propMask & (1u << 4)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, unchecked((uint)picturePosition!.Value));
        if ((propMask & (1u << 6)) != 0)
        {
            dataBlock.WriteByte(checked((byte)mousePointer!.Value));
            MsFormsFactoryBinary.WritePadding(dataBlock, 4);
        }
        if ((propMask & (1u << 7)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, borderColor);
        if ((propMask & (1u << 8)) != 0) MsFormsFactoryBinary.WriteUInt16(dataBlock, borderStyle!.Value);
        if ((propMask & (1u << 9)) != 0) MsFormsFactoryBinary.WriteUInt16(dataBlock, specialEffect!.Value);
        if ((propMask & (1u << 11)) != 0) MsFormsFactoryBinary.WriteUInt16(dataBlock, accelerator![0]);
        MsFormsFactoryBinary.WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);

        var control = MsFormsFactoryBinary.BuildVersionedControl(0, 2, propMask, dataBlock.ToArray(), extra.ToArray());
        var textPropsMask = TextPropsFactory.WithParagraphAlignIfNeeded(TextPropsFactory.StandardMask, request.Properties);
        var textProps = TextPropsFactory.Build(request.Properties, textPropsMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var textPropsMask = TextPropsFactory.WithParagraphAlignIfNeeded(TextPropsFactory.StandardMask, request.Properties);
        var metadata = TextPropsFactory.BuildMetadata(textPropsMask, request.Properties);
        var foreColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "foreColor"), DefaultForeColor);
        var backColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "backColor"), DefaultBackColor);
        var borderColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "borderColor"), DefaultBorderColor);
        var variousBits = BuildVariousPropertyBits(request.Properties);
        uint propMask = 0x0000_0028;
        if (foreColor != DefaultForeColor) propMask |= 1u << 0;
        if (backColor != DefaultBackColor) propMask |= 1u << 1;
        if (variousBits != DefaultVariousPropertyBits) propMask |= 1u << 2;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "picturePosition") is int picturePosition && unchecked((uint)picturePosition) != DefaultPicturePosition) propMask |= 1u << 4;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer") is int mousePointer && mousePointer != 0) propMask |= 1u << 6;
        if (borderColor != DefaultBorderColor) propMask |= 1u << 7;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "borderStyle") is int borderStyle && borderStyle != 0) propMask |= 1u << 8;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "specialEffect") is int specialEffect && specialEffect != 0) propMask |= 1u << 9;
        if (!string.IsNullOrEmpty(MsFormsFactoryBinary.GetString(request.Properties, "accelerator"))) propMask |= 1u << 11;

        metadata["caption"] = request.Caption ?? MsFormsFactoryBinary.GetString(request.Properties, "caption") ?? request.Name;
        metadata["sizeSource"] = "labelExtraDataBlock";
        metadata["parser"] = "msOFormsLabel";
        metadata["propMask"] = $"0x{propMask:X8}";
        metadata["variousPropertyBitsRaw"] = unchecked((int)variousBits);
        metadata["enabled"] = (variousBits & (1u << 1)) != 0;
        metadata["backStyle"] = (variousBits & (1u << 3)) != 0 ? 1 : 0;
        metadata["wordWrap"] = (variousBits & (1u << 23)) != 0;
        metadata["autoSize"] = (variousBits & (1u << 28)) != 0;
        metadata["foreColor"] = $"&H{foreColor:X8}&";
        metadata["backColor"] = $"&H{backColor:X8}&";
        metadata["borderColor"] = $"&H{borderColor:X8}&";
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "borderStyle") is int borderStyleValue) metadata["borderStyle"] = borderStyleValue;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "specialEffect") is int specialEffectValue) metadata["specialEffect"] = specialEffectValue;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "picturePosition") is int picturePositionValue) metadata["picturePosition"] = picturePositionValue;
        if (MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer") is int mousePointerValue) metadata["mousePointer"] = mousePointerValue;
        if (MsFormsFactoryBinary.GetString(request.Properties, "accelerator") is { Length: > 0 } accelerator) metadata["accelerator"] = accelerator[0].ToString();
        metadata["objectStreamSize"] = objectPayloadSize;
        metadata["siteBitFlags"] = $"0x{SiteFlags:X8}";
        metadata["tabStop"] = false;
        metadata["visible"] = true;
        metadata["streamed"] = true;
        metadata["siteAutoSize"] = true;
        return metadata;
    }

    private static uint BuildVariousPropertyBits(Dictionary<string, object?> properties)
    {
        var bits = DefaultVariousPropertyBits;
        SetBit(ref bits, 1, MsFormsFactoryBinary.GetBool(properties, "enabled"));
        if (MsFormsFactoryBinary.GetInt32(properties, "backStyle") is int backStyle)
        {
            SetBit(ref bits, 3, backStyle != 0);
        }
        SetBit(ref bits, 23, MsFormsFactoryBinary.GetBool(properties, "wordWrap"));
        SetBit(ref bits, 28, MsFormsFactoryBinary.GetBool(properties, "autoSize"));
        if (MsFormsFactoryBinary.GetInt32(properties, "imeMode") is int imeMode)
        {
            bits &= ~(0xFu << 15);
            bits |= ((uint)imeMode & 0xFu) << 15;
        }

        return bits;
    }

    private static void SetBit(ref uint bits, int bit, bool? value)
    {
        if (value is null) return;
        var mask = 1u << bit;
        bits = value.Value ? bits | mask : bits & ~mask;
    }
}

internal sealed class TextBoxControlSchema : IGeneratedControlSchema
{
    private const uint DefaultBackColor = 0x8000_0005;
    private const uint DefaultForeColor = 0x8000_0008;
    private const uint DefaultBorderColor = 0x8000_0006;
    private const uint DefaultVariousPropertyBits = 0x2C80_481B;
    private const int DefaultBorderStyle = 1;
    private const int DefaultSpecialEffect = 2;
    private const int DefaultTextAlign = 1;

    public string Type => "TextBox";
    public uint SiteFlags => 0x0000_0013; // tabStop + visible + streamed.

    public byte[] BuildObjectPayload(GeneratedControlRequest request)
    {
        var value = request.Value ?? MsFormsFactoryBinary.GetString(request.Properties, "value");
        var valueBytes = string.IsNullOrEmpty(value) ? [] : Encoding.Latin1.GetBytes(value);
        var backColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "backColor"), DefaultBackColor);
        var foreColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "foreColor"), DefaultForeColor);
        var borderColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "borderColor"), DefaultBorderColor);
        var variousBits = BuildVariousPropertyBits(request.Properties);
        var maxLength = MsFormsFactoryBinary.GetInt32(request.Properties, "maxLength") ?? 0;
        var borderStyle = MsFormsFactoryBinary.GetInt32(request.Properties, "borderStyle") ?? DefaultBorderStyle;
        var scrollBars = MsFormsFactoryBinary.GetInt32(request.Properties, "scrollBars") ?? 0;
        var mousePointer = MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer") ?? 0;
        var passwordChar = MsFormsFactoryBinary.GetString(request.Properties, "passwordChar");
        var specialEffect = MsFormsFactoryBinary.GetInt32(request.Properties, "specialEffect") ?? DefaultSpecialEffect;
        var textAlign = MsFormsFactoryBinary.GetInt32(request.Properties, "textAlignRaw") ??
            ParseTextAlign(request.Properties) ??
            DefaultTextAlign;

        ulong propMask = 0x0000_0000_8000_0101ul; // VariousPropertyBits + Size + reserved bit.
        if (backColor != DefaultBackColor) propMask |= 1ul << 1;
        if (foreColor != DefaultForeColor) propMask |= 1ul << 2;
        if (maxLength != 0) propMask |= 1ul << 3;
        if (borderStyle != DefaultBorderStyle) propMask |= 1ul << 4;
        if (scrollBars != 0) propMask |= 1ul << 5;
        if (mousePointer != 0) propMask |= 1ul << 7;
        if (!string.IsNullOrEmpty(passwordChar)) propMask |= 1ul << 9;
        if (valueBytes.Length > 0) propMask |= 1ul << 22;
        if (borderColor != DefaultBorderColor) propMask |= 1ul << 25;
        if (specialEffect != DefaultSpecialEffect) propMask |= 1ul << 26;
        if (textAlign != DefaultTextAlign) propMask |= 1ul << 33;

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, variousBits);
        if ((propMask & (1ul << 1)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, backColor);
        if ((propMask & (1ul << 2)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, foreColor);
        if ((propMask & (1ul << 3)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, unchecked((uint)maxLength));
        if ((propMask & (1ul << 4)) != 0) dataBlock.WriteByte(checked((byte)borderStyle));
        if ((propMask & (1ul << 5)) != 0) dataBlock.WriteByte(checked((byte)scrollBars));
        if ((propMask & (1ul << 7)) != 0) dataBlock.WriteByte(checked((byte)mousePointer));
        if ((propMask & (1ul << 9)) != 0)
        {
            MsFormsFactoryBinary.WritePadding(dataBlock, 2);
            MsFormsFactoryBinary.WriteUInt16(dataBlock, passwordChar![0]);
        }
        if ((propMask & (1ul << 22)) != 0)
        {
            MsFormsFactoryBinary.WritePadding(dataBlock, 4);
            MsFormsFactoryBinary.WriteCount(dataBlock, valueBytes.Length);
        }
        if ((propMask & (1ul << 25)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, borderColor);
        if ((propMask & (1ul << 26)) != 0) MsFormsFactoryBinary.WriteUInt32(dataBlock, unchecked((uint)specialEffect));
        if ((propMask & (1ul << 33)) != 0) dataBlock.WriteByte(checked((byte)textAlign));
        MsFormsFactoryBinary.WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, request.Width, request.Height);
        if (valueBytes.Length > 0)
        {
            extra.Write(valueBytes);
            MsFormsFactoryBinary.WritePadding(extra, 4);
        }

        var control = MsFormsFactoryBinary.BuildVersionedMorphControl(propMask, dataBlock.ToArray(), extra.ToArray());
        var textProps = TextPropsFactory.Build(request.Properties, TextPropsFactory.StandardMask);
        return [.. control, .. textProps];
    }

    public IReadOnlyDictionary<string, object?> BuildMetadata(GeneratedControlRequest request, int objectPayloadSize)
    {
        var value = request.Value ?? MsFormsFactoryBinary.GetString(request.Properties, "value");
        var valueBytes = string.IsNullOrEmpty(value) ? [] : Encoding.Latin1.GetBytes(value);
        var backColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "backColor"), DefaultBackColor);
        var foreColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "foreColor"), DefaultForeColor);
        var borderColor = MsFormsFactoryBinary.ParseColor(MsFormsFactoryBinary.GetString(request.Properties, "borderColor"), DefaultBorderColor);
        var variousBits = BuildVariousPropertyBits(request.Properties);
        var maxLength = MsFormsFactoryBinary.GetInt32(request.Properties, "maxLength") ?? 0;
        var borderStyle = MsFormsFactoryBinary.GetInt32(request.Properties, "borderStyle") ?? DefaultBorderStyle;
        var scrollBars = MsFormsFactoryBinary.GetInt32(request.Properties, "scrollBars") ?? 0;
        var mousePointer = MsFormsFactoryBinary.GetInt32(request.Properties, "mousePointer") ?? 0;
        var passwordChar = MsFormsFactoryBinary.GetString(request.Properties, "passwordChar");
        var specialEffect = MsFormsFactoryBinary.GetInt32(request.Properties, "specialEffect") ?? DefaultSpecialEffect;
        var textAlign = MsFormsFactoryBinary.GetInt32(request.Properties, "textAlignRaw") ??
            ParseTextAlign(request.Properties) ??
            DefaultTextAlign;
        ulong propMask = 0x0000_0000_8000_0101ul;
        if (backColor != DefaultBackColor) propMask |= 1ul << 1;
        if (foreColor != DefaultForeColor) propMask |= 1ul << 2;
        if (maxLength != 0) propMask |= 1ul << 3;
        if (borderStyle != DefaultBorderStyle) propMask |= 1ul << 4;
        if (scrollBars != 0) propMask |= 1ul << 5;
        if (mousePointer != 0) propMask |= 1ul << 7;
        if (!string.IsNullOrEmpty(passwordChar)) propMask |= 1ul << 9;
        if (valueBytes.Length > 0) propMask |= 1ul << 22;
        if (borderColor != DefaultBorderColor) propMask |= 1ul << 25;
        if (specialEffect != DefaultSpecialEffect) propMask |= 1ul << 26;
        if (textAlign != DefaultTextAlign) propMask |= 1ul << 33;

        var metadata = TextPropsFactory.BuildMetadata(TextPropsFactory.StandardMask, request.Properties);
        metadata["parser"] = "msOFormsMorphData";
        metadata["controlType"] = "TextBox";
        metadata["sizeSource"] = "morphDataExtraDataBlock";
        metadata["propMask"] = $"0x{propMask:X16}";
        metadata["variousPropertyBitsRaw"] = unchecked((int)variousBits);
        metadata["backColor"] = $"&H{backColor:X8}&";
        metadata["foreColor"] = $"&H{foreColor:X8}&";
        metadata["borderColor"] = $"&H{borderColor:X8}&";
        metadata["enabled"] = (variousBits & (1u << 1)) != 0;
        metadata["locked"] = (variousBits & (1u << 2)) != 0;
        metadata["backStyle"] = (variousBits & (1u << 3)) != 0 ? 1 : 0;
        metadata["integralHeight"] = (variousBits & (1u << 11)) != 0;
        metadata["dragBehavior"] = (variousBits & (1u << 19)) != 0 ? 1 : 0;
        metadata["enterKeyBehavior"] = (variousBits & (1u << 20)) != 0;
        metadata["enterFieldBehavior"] = (variousBits & (1u << 21)) != 0 ? 1 : 0;
        metadata["tabKeyBehavior"] = (variousBits & (1u << 22)) != 0;
        metadata["wordWrap"] = (variousBits & (1u << 23)) != 0;
        metadata["selectionMargin"] = (variousBits & (1u << 26)) != 0;
        metadata["autoWordSelect"] = (variousBits & (1u << 27)) != 0;
        metadata["autoSize"] = (variousBits & (1u << 28)) != 0;
        metadata["hideSelection"] = (variousBits & (1u << 29)) != 0;
        metadata["autoTab"] = (variousBits & (1u << 30)) != 0;
        metadata["multiLine"] = (variousBits & (1u << 31)) != 0;
        metadata["borderStyle"] = borderStyle;
        metadata["scrollBars"] = scrollBars;
        metadata["maxLength"] = maxLength;
        metadata["specialEffect"] = specialEffect;
        metadata["textAlign"] = TextPropsFactory.TextAlignName(textAlign);
        if (mousePointer != 0) metadata["mousePointer"] = mousePointer;
        if (!string.IsNullOrEmpty(passwordChar)) metadata["passwordChar"] = passwordChar[0].ToString();
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

    private static uint BuildVariousPropertyBits(Dictionary<string, object?> properties)
    {
        var bits = DefaultVariousPropertyBits;
        SetBit(ref bits, 1, MsFormsFactoryBinary.GetBool(properties, "enabled"));
        SetBit(ref bits, 2, MsFormsFactoryBinary.GetBool(properties, "locked"));
        if (MsFormsFactoryBinary.GetInt32(properties, "backStyle") is int backStyle) SetBit(ref bits, 3, backStyle != 0);
        SetBit(ref bits, 11, MsFormsFactoryBinary.GetBool(properties, "integralHeight"));
        if (MsFormsFactoryBinary.GetInt32(properties, "dragBehavior") is int dragBehavior) SetBit(ref bits, 19, dragBehavior != 0);
        SetBit(ref bits, 20, MsFormsFactoryBinary.GetBool(properties, "enterKeyBehavior"));
        if (MsFormsFactoryBinary.GetInt32(properties, "enterFieldBehavior") is int enterFieldBehavior) SetBit(ref bits, 21, enterFieldBehavior != 0);
        SetBit(ref bits, 22, MsFormsFactoryBinary.GetBool(properties, "tabKeyBehavior"));
        SetBit(ref bits, 23, MsFormsFactoryBinary.GetBool(properties, "wordWrap"));
        SetBit(ref bits, 26, MsFormsFactoryBinary.GetBool(properties, "selectionMargin"));
        SetBit(ref bits, 27, MsFormsFactoryBinary.GetBool(properties, "autoWordSelect"));
        SetBit(ref bits, 28, MsFormsFactoryBinary.GetBool(properties, "autoSize"));
        SetBit(ref bits, 29, MsFormsFactoryBinary.GetBool(properties, "hideSelection"));
        SetBit(ref bits, 30, MsFormsFactoryBinary.GetBool(properties, "autoTab"));
        SetBit(ref bits, 31, MsFormsFactoryBinary.GetBool(properties, "multiLine"));
        if (MsFormsFactoryBinary.GetInt32(properties, "imeMode") is int imeMode)
        {
            bits &= ~(0xFu << 15);
            bits |= ((uint)imeMode & 0xFu) << 15;
        }

        return bits;
    }

    private static int? ParseTextAlign(Dictionary<string, object?> properties) =>
        MsFormsFactoryBinary.GetString(properties, "textAlign") is { } textAlign &&
        TextPropsFactory.TryParseTextAlign(textAlign, out var parsed)
            ? parsed
            : null;

    private static void SetBit(ref uint bits, int bit, bool? value)
    {
        if (value is null) return;
        var mask = 1u << bit;
        bits = value.Value ? bits | mask : bits & ~mask;
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
