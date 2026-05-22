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
                RewriteControlSize(control, props, output);
                break;
            case "Label":
                SerializeTextualControl(control, props, output, ["caption", "fontName"]);
                RewriteCommonColorsAndFont(props, output);
                RewriteControlSize(control, props, output);
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
                RewriteControlSize(control, props, output);
                break;
            case "TabStrip":
                SerializeTextualControl(control, props, output, ["fontName"]);
                RewriteCommonColorsAndFont(props, output);
                RewriteControlSize(control, props, output);
                break;
            case "Image":
            case "ScrollBar":
            case "SpinButton":
                RewriteCommonColorsAndFont(props, output);
                RewriteControlSize(control, props, output);
                break;
        }

        return output;
    }



    public static byte[] SerializeNormalizedStrings(ControlInfo control, ReadOnlySpan<byte> original) =>
        SerializeVariableStrings(control, original, allowGrowth: false);

    public static byte[] SerializePatchedProperties(ControlInfo control, ReadOnlySpan<byte> original)
    {
        if (control.Type.Equals("CommandButton", StringComparison.OrdinalIgnoreCase) &&
            control.Properties is not null &&
            TryGetString(control.Properties, "parser", out var parser) &&
            parser.Equals("msOFormsCommandButton", StringComparison.OrdinalIgnoreCase))
        {
            return SerializeCommandButtonRebuilt(control, original);
        }

        if (control.Type.Equals("Label", StringComparison.OrdinalIgnoreCase) &&
            control.Properties is not null &&
            TryGetString(control.Properties, "parser", out var labelParser) &&
            labelParser.Equals("msOFormsLabel", StringComparison.OrdinalIgnoreCase))
        {
            return SerializeLabelRebuilt(control, original);
        }

        if (control.Type.Equals("TextBox", StringComparison.OrdinalIgnoreCase) &&
            control.Properties is not null &&
            TryGetString(control.Properties, "parser", out var textBoxParser) &&
            textBoxParser.Equals("msOFormsMorphData", StringComparison.OrdinalIgnoreCase))
        {
            return SerializeTextBoxRebuilt(control, original);
        }

        if ((control.Type.Equals("ComboBox", StringComparison.OrdinalIgnoreCase) ||
             control.Type.Equals("ListBox", StringComparison.OrdinalIgnoreCase)) &&
            control.Properties is not null &&
            TryGetString(control.Properties, "parser", out var morphListParser) &&
            morphListParser.Equals("msOFormsMorphData", StringComparison.OrdinalIgnoreCase))
        {
            return SerializeMorphListRebuilt(control);
        }

        if ((control.Type.Equals("CheckBox", StringComparison.OrdinalIgnoreCase) ||
             control.Type.Equals("OptionButton", StringComparison.OrdinalIgnoreCase)) &&
            control.Properties is not null &&
            TryGetString(control.Properties, "parser", out var morphButtonParser) &&
            morphButtonParser.Equals("msOFormsMorphData", StringComparison.OrdinalIgnoreCase))
        {
            return SerializeMorphButtonRebuilt(control);
        }

        return SerializeVariableStrings(control, original, allowGrowth: true);
    }

    private static byte[] SerializeVariableStrings(ControlInfo control, ReadOnlySpan<byte> original, bool allowGrowth)
    {
        // Variable-length pass. First rewrite fixed fields, then rebuild counted strings in reverse
        // offset order.  In normalize mode strings may only compact or keep size; in object-patch
        // mode they may also grow, and the caller updates FormSiteData.ObjectStreamSize.
        var output = allowGrowth ? SerializeFixedFieldsOnly(control, original) : SerializeFixedLength(control, original);
        var props = control.Properties;
        if (props is null)
        {
            return output;
        }

        var segments = BuildNormalizableStringSegments(control, props);
        if (segments.Count == 0)
        {
            return output;
        }

        foreach (var segment in segments
            .OrderByDescending(segment => segment.Span.DataLocalOffset)
            .ThenByDescending(segment => segment.Span.CountLocalOffset ?? -1))
        {
            output = NormalizeStringSegment(control.Name, segment, output, allowGrowth);
        }

        return output;
    }


    private static byte[] SerializeFixedFieldsOnly(ControlInfo control, ReadOnlySpan<byte> original)
    {
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
                RewriteByte(props, output, "minorVersion", 0);
                RewriteByte(props, output, "majorVersion", 1);
                RewriteUInt16(props, output, "cbCommandButton", 2);
                RewriteHexUInt32(props, output, "commandButtonPropMask", 4);
                RewriteCommonColorsAndFont(props, output);
                RewriteControlSize(control, props, output);
                break;
            case "TextBox":
            case "ComboBox":
            case "ListBox":
            case "CheckBox":
            case "OptionButton":
            case "ToggleButton":
                RewriteCommonColorsAndFont(props, output);
                RewriteUInt32(props, output, "variousPropertyBitsRaw");
                RewriteControlSize(control, props, output);
                break;
            case "Label":
            case "TabStrip":
            case "Image":
            case "ScrollBar":
            case "SpinButton":
                RewriteCommonColorsAndFont(props, output);
                RewriteControlSize(control, props, output);
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

    private static byte[] SerializeCommandButtonRebuilt(ControlInfo control, ReadOnlySpan<byte> original)
    {
        var props = control.Properties!;
        var originalBytes = original.ToArray();
        var oldTextPropsStart = TryGetInt(props, "textPropsExpectedLocalOffset", out var textPropsStart)
            ? textPropsStart
            : originalBytes.Length;
        if (oldTextPropsStart < 0 || oldTextPropsStart > originalBytes.Length)
        {
            oldTextPropsStart = originalBytes.Length;
        }

        var textPropValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (TryGetString(props, "fontName", out var fontName)) textPropValues["fontName"] = fontName;
        if (props.TryGetValue("fontSize", out var fontSize) && fontSize is not null) textPropValues["fontSize"] = fontSize;
        if (props.TryGetValue("paragraphAlign", out var commandParagraphAlign) && commandParagraphAlign is not null) textPropValues["paragraphAlign"] = commandParagraphAlign;
        if (props.TryGetValue("textAlign", out var commandTextAlign) && commandTextAlign is not null) textPropValues["textAlign"] = commandTextAlign;
        var textProps = TextPropsFactory.Build(textPropValues, TextPropsFactory.CommandButtonMask);

        const uint defaultForeColor = 0x8000_0012;
        const uint defaultBackColor = 0x8000_000F;
        const uint defaultVarious = 0x0000_001B;
        const uint defaultPicturePosition = 0x0007_0001;

        var caption = TryGetString(props, "caption", out var captionValue) ? captionValue : control.Name;
        var captionBytes = Encoding.Latin1.GetBytes(caption);
        var foreColor = TryGetString(props, "foreColor", out var foreColorText) && TryParseVbaColor(foreColorText, out var parsedFore)
            ? parsedFore
            : defaultForeColor;
        var backColor = TryGetString(props, "backColor", out var backColorText) && TryParseVbaColor(backColorText, out var parsedBack)
            ? parsedBack
            : defaultBackColor;
        var various = TryGetInt(props, "variousPropertyBitsRaw", out var variousRaw)
            ? unchecked((uint)variousRaw)
            : defaultVarious;
        var picturePosition = TryGetInt(props, "picturePosition", out var picturePositionRaw)
            ? unchecked((uint)picturePositionRaw)
            : defaultPicturePosition;
        var mousePointer = TryGetInt(props, "mousePointer", out var mousePointerRaw)
            ? mousePointerRaw
            : 0;
        var accelerator = TryGetString(props, "accelerator", out var acceleratorText) ? acceleratorText : string.Empty;
        var takeFocusOnClick = !props.TryGetValue("takeFocusOnClick", out var rawTakeFocus) ||
            rawTakeFocus is null ||
            rawTakeFocus is bool b && b ||
            bool.TryParse(rawTakeFocus.ToString(), out var parsedTakeFocus) && parsedTakeFocus;

        uint propMask = 0x0000_0028;
        if (foreColor != defaultForeColor) propMask |= 1u << 0;
        if (backColor != defaultBackColor) propMask |= 1u << 1;
        if (various != defaultVarious) propMask |= 1u << 2;
        if (picturePosition != defaultPicturePosition) propMask |= 1u << 4;
        if (mousePointer != 0) propMask |= 1u << 6;
        if (!string.IsNullOrEmpty(accelerator)) propMask |= 1u << 8;
        if (!takeFocusOnClick) propMask |= 1u << 9;
        if (TryGetString(props, "commandButtonPropMask", out var existingMask) && TryParseHexUInt32(existingMask, out var existingMaskValue))
        {
            propMask |= existingMaskValue & ((1u << 7) | (1u << 10));
        }

        using var dataBlock = new MemoryStream();
        if ((propMask & (1u << 0)) != 0) WriteUInt32(dataBlock, foreColor);
        if ((propMask & (1u << 1)) != 0) WriteUInt32(dataBlock, backColor);
        if ((propMask & (1u << 2)) != 0) WriteUInt32(dataBlock, various);
        WriteCount(dataBlock, captionBytes.Length);
        if ((propMask & (1u << 4)) != 0) WriteUInt32(dataBlock, picturePosition);
        if ((propMask & (1u << 6)) != 0)
        {
            dataBlock.WriteByte(checked((byte)mousePointer));
            WritePadding(dataBlock, 2);
        }
        if ((propMask & (1u << 7)) != 0)
        {
            WriteUInt16(dataBlock, 0xFFFF);
            WritePadding(dataBlock, 4);
        }
        if ((propMask & (1u << 8)) != 0)
        {
            WriteUInt16(dataBlock, accelerator[0]);
        }
        if ((propMask & (1u << 10)) != 0)
        {
            WriteUInt16(dataBlock, 0xFFFF);
        }

        WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        WritePadding(extra, 4);
        WriteInt32(extra, control.RawWidth ?? 0);
        WriteInt32(extra, control.RawHeight ?? 0);

        var existingStreamData = Array.Empty<byte>();
        if (TryGetInt(props, "commandButtonDeclaredEndLocalOffset", out var oldDeclaredEnd))
        {
            if (oldDeclaredEnd >= 0 && oldDeclaredEnd <= oldTextPropsStart && oldTextPropsStart <= originalBytes.Length)
            {
                existingStreamData = originalBytes.AsSpan(oldDeclaredEnd, oldTextPropsStart - oldDeclaredEnd).ToArray();
            }
        }

        using var output = new MemoryStream();
        output.WriteByte(0);
        output.WriteByte(2);
        WriteUInt16(output, checked((int)(4 + dataBlock.Length + extra.Length)));
        WriteUInt32(output, propMask);
        output.Write(dataBlock.ToArray());
        output.Write(extra.ToArray());
        output.Write(existingStreamData);
        output.Write(textProps);
        return output.ToArray();
    }

    private static byte[] SerializeLabelRebuilt(ControlInfo control, ReadOnlySpan<byte> original)
    {
        var props = control.Properties!;
        var originalBytes = original.ToArray();
        var oldTextPropsStart = TryGetInt(props, "textPropsExpectedLocalOffset", out var textPropsStart)
            ? textPropsStart
            : originalBytes.Length;
        if (oldTextPropsStart < 0 || oldTextPropsStart > originalBytes.Length)
        {
            oldTextPropsStart = originalBytes.Length;
        }

        var textPropValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (TryGetString(props, "fontName", out var fontName)) textPropValues["fontName"] = fontName;
        if (props.TryGetValue("fontSize", out var fontSize) && fontSize is not null) textPropValues["fontSize"] = fontSize;
        if (props.TryGetValue("paragraphAlign", out var paragraphAlign) && paragraphAlign is not null) textPropValues["paragraphAlign"] = paragraphAlign;
        if (props.TryGetValue("textAlign", out var textAlign) && textAlign is not null) textPropValues["textAlign"] = textAlign;
        var textPropsMask = TextPropsFactory.WithParagraphAlignIfNeeded(TextPropsFactory.StandardMask, textPropValues);
        var textProps = TextPropsFactory.Build(textPropValues, textPropsMask);

        const uint defaultForeColor = 0x8000_0012;
        const uint defaultBackColor = 0x8000_000F;
        const uint defaultBorderColor = 0x8000_0006;
        const uint defaultVarious = 0x0080_0013;
        const uint defaultPicturePosition = 0x0007_0001;

        var caption = TryGetString(props, "caption", out var captionValue) ? captionValue : control.Name;
        var captionBytes = Encoding.Latin1.GetBytes(caption);
        var foreColor = TryGetString(props, "foreColor", out var foreColorText) && TryParseVbaColor(foreColorText, out var parsedFore)
            ? parsedFore
            : defaultForeColor;
        var backColor = TryGetString(props, "backColor", out var backColorText) && TryParseVbaColor(backColorText, out var parsedBack)
            ? parsedBack
            : defaultBackColor;
        var borderColor = TryGetString(props, "borderColor", out var borderColorText) && TryParseVbaColor(borderColorText, out var parsedBorder)
            ? parsedBorder
            : defaultBorderColor;
        var various = TryGetInt(props, "variousPropertyBitsRaw", out var variousRaw)
            ? unchecked((uint)variousRaw)
            : defaultVarious;
        var picturePosition = TryGetInt(props, "picturePosition", out var picturePositionRaw)
            ? unchecked((uint)picturePositionRaw)
            : defaultPicturePosition;
        var mousePointer = TryGetInt(props, "mousePointer", out var mousePointerRaw) ? mousePointerRaw : 0;
        var borderStyle = TryGetInt(props, "borderStyle", out var borderStyleRaw) ? borderStyleRaw : 0;
        var specialEffect = TryGetInt(props, "specialEffect", out var specialEffectRaw) ? specialEffectRaw : 0;
        var accelerator = TryGetString(props, "accelerator", out var acceleratorText) ? acceleratorText : string.Empty;

        uint propMask = 0x0000_0028;
        if (foreColor != defaultForeColor) propMask |= 1u << 0;
        if (backColor != defaultBackColor) propMask |= 1u << 1;
        if (various != defaultVarious) propMask |= 1u << 2;
        if (picturePosition != defaultPicturePosition) propMask |= 1u << 4;
        if (mousePointer != 0) propMask |= 1u << 6;
        if (borderColor != defaultBorderColor) propMask |= 1u << 7;
        if (borderStyle != 0) propMask |= 1u << 8;
        if (specialEffect != 0) propMask |= 1u << 9;
        if (!string.IsNullOrEmpty(accelerator)) propMask |= 1u << 11;
        if (TryGetString(props, "propMask", out var existingMask) && TryParseHexUInt32(existingMask, out var existingMaskValue))
        {
            propMask |= existingMaskValue & ((1u << 10) | (1u << 12));
        }

        using var dataBlock = new MemoryStream();
        if ((propMask & (1u << 0)) != 0) WriteUInt32(dataBlock, foreColor);
        if ((propMask & (1u << 1)) != 0) WriteUInt32(dataBlock, backColor);
        if ((propMask & (1u << 2)) != 0) WriteUInt32(dataBlock, various);
        WriteCount(dataBlock, captionBytes.Length);
        if ((propMask & (1u << 4)) != 0) WriteUInt32(dataBlock, picturePosition);
        if ((propMask & (1u << 6)) != 0)
        {
            dataBlock.WriteByte(checked((byte)mousePointer));
            WritePadding(dataBlock, 4);
        }
        if ((propMask & (1u << 7)) != 0) WriteUInt32(dataBlock, borderColor);
        if ((propMask & (1u << 8)) != 0) WriteUInt16(dataBlock, borderStyle);
        if ((propMask & (1u << 9)) != 0) WriteUInt16(dataBlock, specialEffect);
        if ((propMask & (1u << 10)) != 0)
        {
            WriteUInt16(dataBlock, 0xFFFF);
            WritePadding(dataBlock, 2);
        }
        if ((propMask & (1u << 11)) != 0) WriteUInt16(dataBlock, accelerator[0]);
        if ((propMask & (1u << 12)) != 0) WriteUInt16(dataBlock, 0xFFFF);
        WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        extra.Write(captionBytes);
        WritePadding(extra, 4);
        WriteInt32(extra, control.RawWidth ?? 0);
        WriteInt32(extra, control.RawHeight ?? 0);

        var existingStreamData = Array.Empty<byte>();
        if (TryGetInt(props, "labelDeclaredEndLocalOffset", out var oldDeclaredEnd) &&
            oldDeclaredEnd >= 0 &&
            oldDeclaredEnd <= oldTextPropsStart &&
            oldTextPropsStart <= originalBytes.Length)
        {
            existingStreamData = originalBytes.AsSpan(oldDeclaredEnd, oldTextPropsStart - oldDeclaredEnd).ToArray();
        }

        using var output = new MemoryStream();
        output.WriteByte(0);
        output.WriteByte(2);
        WriteUInt16(output, checked((int)(4 + dataBlock.Length + extra.Length)));
        WriteUInt32(output, propMask);
        output.Write(dataBlock.ToArray());
        output.Write(extra.ToArray());
        output.Write(existingStreamData);
        output.Write(textProps);
        return output.ToArray();
    }

    private static byte[] SerializeTextBoxRebuilt(ControlInfo control, ReadOnlySpan<byte> original)
    {
        var props = control.Properties!;
        var originalBytes = original.ToArray();
        var oldTextPropsStart = TryGetInt(props, "textPropsExpectedLocalOffset", out var textPropsStart)
            ? textPropsStart
            : originalBytes.Length;
        if (oldTextPropsStart < 0 || oldTextPropsStart > originalBytes.Length)
        {
            oldTextPropsStart = originalBytes.Length;
        }

        const uint defaultBackColor = 0x8000_0005;
        const uint defaultForeColor = 0x8000_0008;
        const uint defaultBorderColor = 0x8000_0006;
        const uint defaultVarious = 0x2C80_481B;
        const int defaultBorderStyle = 1;
        const int defaultSpecialEffect = 2;

        var value = TryGetString(props, "value", out var valueText) ? valueText : string.Empty;
        var valueBytes = Encoding.Latin1.GetBytes(value);
        var backColor = TryGetString(props, "backColor", out var backColorText) && TryParseVbaColor(backColorText, out var parsedBack)
            ? parsedBack
            : defaultBackColor;
        var foreColor = TryGetString(props, "foreColor", out var foreColorText) && TryParseVbaColor(foreColorText, out var parsedFore)
            ? parsedFore
            : defaultForeColor;
        var borderColor = TryGetString(props, "borderColor", out var borderColorText) && TryParseVbaColor(borderColorText, out var parsedBorder)
            ? parsedBorder
            : defaultBorderColor;
        var various = TryGetInt(props, "variousPropertyBitsRaw", out var variousRaw)
            ? unchecked((uint)variousRaw)
            : defaultVarious;
        var maxLength = TryGetInt(props, "maxLength", out var maxLengthRaw) ? maxLengthRaw : 0;
        var borderStyle = TryGetInt(props, "borderStyle", out var borderStyleRaw) ? borderStyleRaw : defaultBorderStyle;
        var scrollBars = TryGetInt(props, "scrollBars", out var scrollBarsRaw) ? scrollBarsRaw : 0;
        var mousePointer = TryGetInt(props, "mousePointer", out var mousePointerRaw) ? mousePointerRaw : 0;
        var passwordChar = TryGetString(props, "passwordChar", out var passwordCharText) ? passwordCharText : string.Empty;
        var specialEffect = TryGetInt(props, "specialEffect", out var specialEffectRaw) ? specialEffectRaw : defaultSpecialEffect;

        ulong propMask = 0x0000_0000_8000_0101ul;
        if (backColor != defaultBackColor) propMask |= 1ul << 1;
        if (foreColor != defaultForeColor) propMask |= 1ul << 2;
        if (maxLength != 0) propMask |= 1ul << 3;
        if (borderStyle != defaultBorderStyle) propMask |= 1ul << 4;
        if (scrollBars != 0) propMask |= 1ul << 5;
        if (mousePointer != 0) propMask |= 1ul << 7;
        if (!string.IsNullOrEmpty(passwordChar)) propMask |= 1ul << 9;
        if (valueBytes.Length > 0) propMask |= 1ul << 22;
        if (borderColor != defaultBorderColor) propMask |= 1ul << 25;
        if (specialEffect != defaultSpecialEffect) propMask |= 1ul << 26;

        if (TryGetString(props, "propMask", out var oldMaskText) && TryParseHexUInt64(oldMaskText, out var oldMask))
        {
            propMask |= oldMask & ((1ul << 27) | (1ul << 28));
        }

        using var dataBlock = new MemoryStream();
        WriteUInt32(dataBlock, various);
        if ((propMask & (1ul << 1)) != 0) WriteUInt32(dataBlock, backColor);
        if ((propMask & (1ul << 2)) != 0) WriteUInt32(dataBlock, foreColor);
        if ((propMask & (1ul << 3)) != 0) WriteUInt32(dataBlock, unchecked((uint)maxLength));
        if ((propMask & (1ul << 4)) != 0) dataBlock.WriteByte(checked((byte)borderStyle));
        if ((propMask & (1ul << 5)) != 0) dataBlock.WriteByte(checked((byte)scrollBars));
        if ((propMask & (1ul << 7)) != 0) dataBlock.WriteByte(checked((byte)mousePointer));
        if ((propMask & (1ul << 9)) != 0)
        {
            WritePadding(dataBlock, 2);
            WriteUInt16(dataBlock, passwordChar[0]);
        }
        if ((propMask & (1ul << 22)) != 0)
        {
            WritePadding(dataBlock, 4);
            WriteCount(dataBlock, valueBytes.Length);
        }
        if ((propMask & (1ul << 25)) != 0) WriteUInt32(dataBlock, borderColor);
        if ((propMask & (1ul << 26)) != 0) WriteUInt32(dataBlock, unchecked((uint)specialEffect));
        if ((propMask & (1ul << 27)) != 0) WriteUInt16(dataBlock, 0xFFFF);
        if ((propMask & (1ul << 28)) != 0) WriteUInt16(dataBlock, 0xFFFF);
        WritePadding(dataBlock, 4);

        using var extra = new MemoryStream();
        WriteInt32(extra, control.RawWidth ?? 0);
        WriteInt32(extra, control.RawHeight ?? 0);
        if ((propMask & (1ul << 22)) != 0)
        {
            extra.Write(valueBytes);
            WritePadding(extra, 4);
        }

        var textPropValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (TryGetString(props, "fontName", out var fontName)) textPropValues["fontName"] = fontName;
        if (props.TryGetValue("fontSize", out var fontSize) && fontSize is not null) textPropValues["fontSize"] = fontSize;
        if (props.TryGetValue("paragraphAlign", out var paragraphAlign) && paragraphAlign is not null) textPropValues["paragraphAlign"] = paragraphAlign;
        if (props.TryGetValue("textAlign", out var textAlign) && textAlign is not null) textPropValues["textAlign"] = textAlign;
        var textPropsMask = TextPropsFactory.WithParagraphAlignIfNeeded(TextPropsFactory.StandardMask, textPropValues);
        var textProps = TextPropsFactory.Build(textPropValues, textPropsMask);

        var existingStreamData = Array.Empty<byte>();
        if (TryGetInt(props, "morphDataStreamDataLocalOffset", out var streamDataStart) &&
            TryGetInt(props, "morphDataStreamDataEndLocalOffset", out var streamDataEnd) &&
            streamDataStart >= 0 &&
            streamDataEnd >= streamDataStart &&
            streamDataEnd <= oldTextPropsStart &&
            streamDataEnd <= originalBytes.Length)
        {
            existingStreamData = originalBytes.AsSpan(streamDataStart, streamDataEnd - streamDataStart).ToArray();
        }

        var controlBlock = MsFormsFactoryBinary.BuildVersionedMorphControl(propMask, dataBlock.ToArray(), extra.ToArray());
        using var output = new MemoryStream();
        output.Write(controlBlock);
        output.Write(existingStreamData);
        output.Write(textProps);
        return output.ToArray();
    }

    private static byte[] SerializeMorphListRebuilt(ControlInfo control)
    {
        var props = control.Properties!;
        IGeneratedControlSchema schema = control.Type.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)
            ? new ComboBoxControlSchema()
            : new ListBoxControlSchema();
        var request = new GeneratedControlRequest(
            control.Type,
            control.Name,
            SiteId: 0,
            TabIndex: TryGetInt(props, "tabIndex", out var tabIndex) ? tabIndex : 0,
            Left: control.Left ?? 0,
            Top: control.Top ?? 0,
            Width: control.RawWidth ?? 0,
            Height: control.RawHeight ?? 0,
            Caption: null,
            Value: TryGetString(props, "value", out var value) ? value : null,
            Properties: props);
        return schema.BuildObjectPayload(request);
    }

    private static byte[] SerializeMorphButtonRebuilt(ControlInfo control)
    {
        var props = control.Properties!;
        IGeneratedControlSchema schema = control.Type.Equals("CheckBox", StringComparison.OrdinalIgnoreCase)
            ? new CheckBoxControlSchema()
            : new OptionButtonControlSchema();
        var request = new GeneratedControlRequest(
            control.Type,
            control.Name,
            SiteId: 0,
            TabIndex: TryGetInt(props, "tabIndex", out var tabIndex) ? tabIndex : 0,
            Left: control.Left ?? 0,
            Top: control.Top ?? 0,
            Width: control.RawWidth ?? 0,
            Height: control.RawHeight ?? 0,
            Caption: TryGetString(props, "caption", out var caption) ? caption : control.Name,
            Value: TryGetString(props, "value", out var value) ? value : "0",
            Properties: props);
        return schema.BuildObjectPayload(request);
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

    private static void RewriteControlSize(ControlInfo control, Dictionary<string, object?> props, byte[] output)
    {
        if (!TryGetInt(props, "objectStreamFileOffset", out var objectStreamFileOffset))
        {
            return;
        }

        if (control.RawWidth is int rawWidth && control.WidthOffset is int widthFileOffset)
        {
            var localOffset = widthFileOffset - objectStreamFileOffset;
            if (localOffset >= 0 && localOffset + 4 <= output.Length)
            {
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(localOffset, 4), rawWidth);
            }
        }

        if (control.RawHeight is int rawHeight && control.HeightOffset is int heightFileOffset)
        {
            var localOffset = heightFileOffset - objectStreamFileOffset;
            if (localOffset >= 0 && localOffset + 4 <= output.Length)
            {
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(localOffset, 4), rawHeight);
            }
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



    private static List<StringSegment> BuildNormalizableStringSegments(ControlInfo control, Dictionary<string, object?> props)
    {
        var result = new List<StringSegment>();

        void Add(string name, int? blockSizeLocalOffset)
        {
            if (!props.TryGetValue(name, out var rawValue) || rawValue is null)
            {
                return;
            }

            if (!TryGetStringSpan(props, name, out var span) || span.CountLocalOffset is null)
            {
                return;
            }

            result.Add(new StringSegment(name, rawValue.ToString() ?? string.Empty, span, blockSizeLocalOffset));
        }

        switch (control.Type)
        {
            case "CommandButton":
                Add("caption", 2);
                Add("fontName", TryGetInt(props, "textPropsLocalOffset", out var commandButtonTextPropsStart) ? commandButtonTextPropsStart + 2 : null);
                break;
            case "Label":
                Add("caption", 2);
                Add("fontName", TryGetInt(props, "textPropsLocalOffset", out var labelTextPropsStart) ? labelTextPropsStart + 2 : null);
                break;
            case "TextBox":
            case "ComboBox":
            case "ListBox":
            case "CheckBox":
            case "OptionButton":
            case "ToggleButton":
                Add("value", 2);
                Add("caption", 2);
                Add("groupName", 2);
                Add("fontName", TryGetInt(props, "textPropsLocalOffset", out var morphTextPropsStart) ? morphTextPropsStart + 2 : null);
                break;
            case "TabStrip":
                Add("fontName", TryGetInt(props, "textPropsLocalOffset", out var tabStripTextPropsStart) ? tabStripTextPropsStart + 2 : null);
                break;
        }

        return result;
    }

    private static byte[] NormalizeStringSegment(string controlName, StringSegment segment, byte[] payload, bool allowGrowth)
    {
        var span = segment.Span;
        if (span.CountLocalOffset is not int countLocalOffset)
        {
            return payload;
        }

        if (countLocalOffset < 0 || countLocalOffset + 4 > payload.Length)
        {
            throw new CliException($"Cannot normalize {controlName}.{segment.PropertyName}: count offset {countLocalOffset} is outside the object payload.");
        }

        if (span.DataLocalOffset < 0 || span.DataLocalOffset > payload.Length ||
            span.ByteCount < 0 || span.PaddedByteCount < 0 ||
            span.DataLocalOffset + span.PaddedByteCount > payload.Length)
        {
            throw new CliException($"Cannot normalize {controlName}.{segment.PropertyName}: string span exceeds the object payload.");
        }

        var encoded = span.Compressed
            ? Encoding.Latin1.GetBytes(segment.Value)
            : Encoding.Unicode.GetBytes(segment.Value);

        if (!span.Compressed && encoded.Length % 2 != 0)
        {
            throw new CliException($"Cannot normalize {controlName}.{segment.PropertyName}: UTF-16 byte count must be even.");
        }

        var newPaddedByteCount = Align4(encoded.Length);
        if (!allowGrowth && newPaddedByteCount > span.PaddedByteCount)
        {
            throw new CliException($"Cannot normalize {controlName}.{segment.PropertyName}: decoded value needs {encoded.Length} bytes, exceeding current padded allocation {span.PaddedByteCount}. Use object-patch mode for full variable-length mutations.");
        }

        // No structural change needed; still write the real count and clear padding so the payload
        // is canonical even when size stays equal.
        var count = (uint)encoded.Length;
        if (span.Compressed)
        {
            count |= 0x8000_0000u;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(countLocalOffset, 4), count);

        var delta = newPaddedByteCount - span.PaddedByteCount;
        if (segment.BlockSizeLocalOffset is int blockSizeLocalOffset && delta != 0)
        {
            AdjustUInt16At(payload, blockSizeLocalOffset, delta, $"{controlName}.{segment.PropertyName}");
        }

        if (newPaddedByteCount == span.PaddedByteCount)
        {
            payload.AsSpan(span.DataLocalOffset, span.PaddedByteCount).Clear();
            encoded.CopyTo(payload.AsSpan(span.DataLocalOffset));
            return payload;
        }

        using var output = new MemoryStream(payload.Length + delta);
        output.Write(payload, 0, span.DataLocalOffset);
        output.Write(encoded, 0, encoded.Length);
        if (newPaddedByteCount > encoded.Length)
        {
            output.Write(new byte[newPaddedByteCount - encoded.Length]);
        }

        var originalTailStart = span.DataLocalOffset + span.PaddedByteCount;
        output.Write(payload, originalTailStart, payload.Length - originalTailStart);
        return output.ToArray();
    }

    private static void AdjustUInt16At(byte[] payload, int localOffset, int delta, string context)
    {
        if (localOffset < 0 || localOffset + 2 > payload.Length)
        {
            throw new CliException($"Cannot normalize {context}: declared block size offset {localOffset} is outside the object payload.");
        }

        var current = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(localOffset, 2));
        var next = current + delta;
        if (next is < 0 or > ushort.MaxValue)
        {
            throw new CliException($"Cannot normalize {context}: declared block size would become {next}.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(localOffset, 2), unchecked((ushort)next));
    }

    private static int Align4(int value) => (value + 3) & ~3;

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

    private static void WriteCount(Stream stream, int count, bool compressed = true)
    {
        var raw = checked((uint)count);
        if (compressed)
        {
            raw |= 0x8000_0000u;
        }

        WriteUInt32(stream, raw);
    }

    private static void WritePadding(Stream stream, int alignment)
    {
        while (stream.Length % alignment != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, checked((ushort)value));
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
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

    private static bool TryParseHexUInt64(string text, out ulong value)
    {
        value = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
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
            case ulong ul when ul <= int.MaxValue:
                value = (int)ul;
                return true;
            case short s:
                value = s;
                return true;
            case ushort us:
                value = us;
                return true;
            case byte b:
                value = b;
                return true;
            case sbyte sb:
                value = sb;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed):
                value = parsed;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed64) && parsed64 >= int.MinValue && parsed64 <= int.MaxValue:
                value = (int)parsed64;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var parsedU64) && parsedU64 <= int.MaxValue:
                value = (int)parsedU64;
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

    private sealed record StringSegment(string PropertyName, string Value, StringSpanInfo Span, int? BlockSizeLocalOffset);
}
