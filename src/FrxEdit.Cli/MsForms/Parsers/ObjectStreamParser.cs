internal static class ObjectStreamParser
{
    public static ObjectStreamProperties Read(StorageEntryDump stream, string? controlType = null)
    {
        var props = controlType switch
        {
            "CommandButton" => TryReadCommandButton(stream),
            "Label" => TryReadLabel(stream),
            "Image" => TryReadImage(stream),
            "ScrollBar" => TryReadScrollBar(stream),
            "SpinButton" => TryReadSpinButton(stream),
            "TabStrip" => TryReadTabStrip(stream),
            "TextBox" or "ComboBox" or "ListBox" or "CheckBox" or "OptionButton" or "ToggleButton" => TryReadMorphData(stream, controlType),
            _ => null
        };

        if (props != null) return props;

        return ReadHeuristic(stream);
    }

    private static ObjectStreamProperties ReadHeuristic(StorageEntryDump stream)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "heuristic",
        };

        var data = stream.Data;
        var colors = FindSystemColors(data, stream.FileOffsets);
        if (colors.Count > 0)
        {
            properties["systemColors"] = colors;
            if (data.Length >= 16 && data[2] == 0x40)
            {
                var foreColor = colors.FirstOrDefault(c => c.StreamOffset == 8);
                var backColor = colors.FirstOrDefault(c => c.StreamOffset == 12);
                if (backColor is not null)
                {
                    properties["backColor"] = backColor.Value;
                    properties["backColorOffset"] = backColor.Offset;
                }

                if (foreColor is not null)
                {
                    properties["foreColor"] = foreColor.Value;
                    properties["foreColorOffset"] = foreColor.Offset;
                }
            }
        }

        var textRuns = FindTextRuns(data, minLength: 3);
        var fontRun = TextPropsParser.FindFontNameRun(data, textRuns);

        if (fontRun is not null)
        {
            properties["fontName"] = fontRun.Value.Text;
            properties["fontNameOffset"] = stream.FileOffsets[fontRun.Value.Offset];
            var fontSize = TextPropsParser.FindFontSizeBefore(data, fontRun.Value.Offset);
            if (fontSize is not null)
            {
                properties["fontSize"] = fontSize.Value.Size;
                properties["fontSizeRaw"] = fontSize.Value.Raw;
                properties["fontSizeOffset"] = stream.FileOffsets[fontSize.Value.Offset];
                if (fontSize.Value.Offset >= 4)
                {
                    properties["fontStyleRawHex"] = Convert.ToHexString(data.AsSpan(fontSize.Value.Offset - 4, 4));
                    properties["fontStyleRawOffset"] = stream.FileOffsets[fontSize.Value.Offset - 4];
                }
            }
        }

        TextRun? captionRun = null;
        foreach (var run in textRuns)
        {
            if (fontRun is not null && run.Offset == fontRun.Value.Offset) continue;
            if (run.Text.Equals("Tahoma", StringComparison.OrdinalIgnoreCase) ||
                run.Text.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase)) continue;

            captionRun = run;
            break;
        }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;
        if (captionRun is not null)
        {
            var dimensions = FindPlausiblePair(data, captionRun.Value.Offset + captionRun.Value.Text.Length, 12);
            if (dimensions is not null)
            {
                var rawCaption = Encoding.Latin1.GetString(data, captionRun.Value.Offset, dimensions.Value.Offset - captionRun.Value.Offset);
                var caption = MsFormsBinary.TrimLikelyPropertySuffix(rawCaption);
                properties["caption"] = caption;
                properties["captionOffset"] = stream.FileOffsets[captionRun.Value.Offset];

                width = dimensions.Value.First;
                height = dimensions.Value.Second;
                widthOffset = stream.FileOffsets[dimensions.Value.Offset];
                heightOffset = stream.FileOffsets[dimensions.Value.Offset + 4];
                properties["sizeSource"] = "heuristic";
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadLabel(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8) return null;

        int cursor = 0;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var cbLabel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;

        // LabelPropMask is 4 bytes
        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsLabel",
            ["propMask"] = $"0x{propMask:X8}",
        };

        // DataBlock (4 bytes or smaller)
        if (MsFormsBinary.HasBit(propMask, 0)) // fForeColor
        {
            properties["foreColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        }
        if (MsFormsBinary.HasBit(propMask, 1)) // fBackColor
        {
            properties["backColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        }
        if (MsFormsBinary.HasBit(propMask, 2)) // fVariousPropertyBits
        {
            var various = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["variousPropertyBitsRaw"] = various;
            MsFormsBinary.AddVariousPropertyBits(properties, various);
            cursor += 4;
        }
        if (MsFormsBinary.HasBit(propMask, 3)) // fCaption (ExtraData count)
        {
            properties["captionCount"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
        }
        if (MsFormsBinary.HasBit(propMask, 4)) // fPicturePosition
        {
            properties["picturePosition"] = data[cursor++];
            MsFormsBinary.Align(ref cursor, 4);
        }
        if (MsFormsBinary.HasBit(propMask, 5)) // fSize (ExtraData Width/Height)
        {
            // Just mark it, ExtraDataBlock reads it.
        }
        if (MsFormsBinary.HasBit(propMask, 6)) // fMousePointer
        {
            properties["mousePointer"] = data[cursor++];
            MsFormsBinary.Align(ref cursor, 4);
        }
        if (MsFormsBinary.HasBit(propMask, 7)) // fBorderColor
        {
            properties["borderColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        }
        if (MsFormsBinary.HasBit(propMask, 8)) // fBorderStyle
        {
            properties["borderStyle"] = data[cursor++];
            MsFormsBinary.Align(ref cursor, 4);
        }
        if (MsFormsBinary.HasBit(propMask, 9)) // fSpecialEffect
        {
            properties["specialEffect"] = data[cursor++];
            MsFormsBinary.Align(ref cursor, 4);
        }
        if (MsFormsBinary.HasBit(propMask, 10)) // fPicture
        {
            properties["picture"] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            MsFormsBinary.Align(ref cursor, 4);
        }
        if (MsFormsBinary.HasBit(propMask, 11)) // fAccelerator
        {
            properties["accelerator"] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            MsFormsBinary.Align(ref cursor, 4);
        }
        if (MsFormsBinary.HasBit(propMask, 12)) // fMouseIcon
        {
            properties["mouseIcon"] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            MsFormsBinary.Align(ref cursor, 4);
        }

        // ExtraDataBlock
        if (MsFormsBinary.HasBit(propMask, 3)) // fCaption
        {
            var count = (uint)properties["captionCount"]!;
            properties["caption"] = MsFormsBinary.ReadFmString(data, cursor, MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(count));
            cursor += MsFormsBinary.GetFmStringByteCount(count);
        }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 5)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "labelExtraDataBlock";
        }

        if (cursor < data.Length)
        {
            if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out var textPropsEnd))
            {
                TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadImage(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8) return null;

        int cursor = 0;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var cbImage = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsImage",
            ["propMask"] = $"0x{propMask:X8}",
        };

        if (MsFormsBinary.HasBit(propMask, 2)) properties["autoSize"] = true;
        if (MsFormsBinary.HasBit(propMask, 3)) properties["borderColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 4)) properties["backColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 5)) { properties["borderStyle"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 6)) { properties["mousePointer"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 7)) { properties["pictureSizeMode"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 8)) { properties["specialEffect"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 10)) { properties["picture"] = 0xFFFF; cursor += 2; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 11)) { properties["pictureAlignment"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 12)) properties["pictureTiling"] = true;
        if (MsFormsBinary.HasBit(propMask, 13))
        {
            var various = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["variousPropertyBitsRaw"] = various;
            MsFormsBinary.AddVariousPropertyBits(properties, various);
            cursor += 4;
        }
        if (MsFormsBinary.HasBit(propMask, 14)) { properties["mouseIcon"] = 0xFFFF; cursor += 2; MsFormsBinary.Align(ref cursor, 4); }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 9)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "imageExtraDataBlock";
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadScrollBar(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8) return null;

        int cursor = 0;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var cbScroll = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsScrollBar",
            ["propMask"] = $"0x{propMask:X8}",
        };

        if (MsFormsBinary.HasBit(propMask, 0)) properties["foreColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 1)) properties["backColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var various = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["variousPropertyBitsRaw"] = various;
            MsFormsBinary.AddVariousPropertyBits(properties, various);
            cursor += 4;
        }
        if (MsFormsBinary.HasBit(propMask, 4)) { properties["mousePointer"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 5)) { properties["min"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 6)) { properties["max"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 7)) { properties["position"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 11)) { properties["smallChange"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 12)) { properties["largeChange"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 13)) { properties["orientation"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 14)) { properties["proportionalThumb"] = data[cursor++] != 0; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 15)) { properties["mouseIcon"] = 0xFFFF; cursor += 2; MsFormsBinary.Align(ref cursor, 4); }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 3)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "scrollBarExtraDataBlock";
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadSpinButton(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8) return null;

        int cursor = 0;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var cbSpin = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsSpinButton",
            ["propMask"] = $"0x{propMask:X8}",
        };

        if (MsFormsBinary.HasBit(propMask, 0)) properties["foreColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 1)) properties["backColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var various = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["variousPropertyBitsRaw"] = various;
            MsFormsBinary.AddVariousPropertyBits(properties, various);
            cursor += 4;
        }
        if (MsFormsBinary.HasBit(propMask, 5)) { properties["min"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 6)) { properties["max"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 7)) { properties["position"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 10)) { properties["smallChange"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 11)) { properties["orientation"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 12)) { properties["delay"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 13)) { properties["mouseIcon"] = 0xFFFF; cursor += 2; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 14)) { properties["mousePointer"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 3)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "spinButtonExtraDataBlock";
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadTabStrip(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8) return null;

        int cursor = 0;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var cbTab = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsTabStrip",
            ["propMask"] = $"0x{propMask:X8}",
        };

        if (MsFormsBinary.HasBit(propMask, 0)) { properties["listIndex"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 1)) properties["backColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 2)) properties["foreColor"] = MsFormsBinary.ReadColor(data, ref cursor);
        if (MsFormsBinary.HasBit(propMask, 5)) { properties["itemsSize"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 6)) { properties["mousePointer"] = data[cursor++]; MsFormsBinary.Align(ref cursor, 4); }
        if (MsFormsBinary.HasBit(propMask, 8)) { properties["tabOrientation"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 9)) { properties["tabStyle"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 10)) properties["multiRow"] = true;
        if (MsFormsBinary.HasBit(propMask, 11)) { properties["tabFixedWidth"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 12)) { properties["tabFixedHeight"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 13)) properties["tooltips"] = true;
        if (MsFormsBinary.HasBit(propMask, 15)) { properties["tipStringsSize"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 17)) { properties["namesSize"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 18))
        {
            var various = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            properties["variousPropertyBitsRaw"] = various;
            MsFormsBinary.AddVariousPropertyBits(properties, various);
            cursor += 4;
        }
        if (MsFormsBinary.HasBit(propMask, 19)) { properties["tabsAllocated"] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 20)) { properties["tagsSize"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 21)) { properties["acceleratorsSize"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; }
        if (MsFormsBinary.HasBit(propMask, 22)) properties["helpContextIdsSize"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)); 
        if (MsFormsBinary.HasBit(propMask, 23)) { properties["mouseIcon"] = 0xFFFF; cursor += 2; MsFormsBinary.Align(ref cursor, 4); }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 4)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "tabStripExtraDataBlock";
        }

        if (cursor < data.Length)
        {
            if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out var textPropsEnd))
            {
                TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadMorphData(StorageEntryDump stream, string controlType)
    {
        var data = stream.Data;
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x02) return null;

        var cbMorphData = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        var propMask = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4, 8));
        var cursor = 12;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsMorphData",
            ["controlType"] = controlType,
            ["propMask"] = $"0x{propMask:X16}",
        };

        // DataBlock
        var dataBlockStart = 12;

        if (MsFormsBinary.HasBit64(propMask, 0))
        {
            var value = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            AddVariousPropertyBits(properties, value);
        }

        if (MsFormsBinary.HasBit64(propMask, 1)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, true);
        if (MsFormsBinary.HasBit64(propMask, 2)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, true);
        if (MsFormsBinary.HasBit64(propMask, 3)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "maxLength", properties);
        if (MsFormsBinary.HasBit64(propMask, 4)) properties["borderStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "borderStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 5)) properties["scrollBars"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "scrollBars", properties);
        if (MsFormsBinary.HasBit64(propMask, 6)) properties["displayStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "displayStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 7)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);

        if (MsFormsBinary.HasBit64(propMask, 9))
        {
            MsFormsBinary.Align(ref cursor, 2);
            properties["passwordChar"] = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
        }

        if (MsFormsBinary.HasBit64(propMask, 10)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "listWidth", properties);
        if (MsFormsBinary.HasBit64(propMask, 11)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "boundColumn", properties);
        if (MsFormsBinary.HasBit64(propMask, 12)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "textColumn", properties);
        if (MsFormsBinary.HasBit64(propMask, 13)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "columnCount", properties);
        if (MsFormsBinary.HasBit64(propMask, 14)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "listRows", properties);
        if (MsFormsBinary.HasBit64(propMask, 15)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "cColumnInfo", properties);
        if (MsFormsBinary.HasBit64(propMask, 16)) properties["matchEntry"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "matchEntry", properties);
        if (MsFormsBinary.HasBit64(propMask, 17)) properties["listStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "listStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 18)) properties["showDropButtonWhen"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "showDropButtonWhen", properties);
        if (MsFormsBinary.HasBit64(propMask, 20)) properties["dropButtonStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "dropButtonStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 21)) properties["multiSelect"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "multiSelect", properties);

        CountOfBytesWithCompressionFlag? valueCount = null;
        CountOfBytesWithCompressionFlag? captionCount = null;
        CountOfBytesWithCompressionFlag? groupNameCount = null;

        if (MsFormsBinary.HasBit64(propMask, 22)) valueCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "valueCount", properties));
        if (MsFormsBinary.HasBit64(propMask, 23)) captionCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "captionCount", properties));
        if (MsFormsBinary.HasBit64(propMask, 24)) properties["picturePosition"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "picturePosition", properties);
        if (MsFormsBinary.HasBit64(propMask, 25)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "borderColor", properties, true);
        if (MsFormsBinary.HasBit64(propMask, 26)) properties["specialEffect"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "specialEffect", properties);
        if (MsFormsBinary.HasBit64(propMask, 27)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);
        if (MsFormsBinary.HasBit64(propMask, 28)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "pictureMarker", properties);
        if (MsFormsBinary.HasBit64(propMask, 29)) properties["accelerator"] = (char)MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "acceleratorCode", properties);
        if (MsFormsBinary.HasBit64(propMask, 32)) groupNameCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "groupNameCount", properties));
        if (MsFormsBinary.HasBit64(propMask, 33)) properties["textAlign"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "textAlign", properties);
        if (MsFormsBinary.HasBit64(propMask, 34)) properties["dropEffect"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "dropEffect", properties);

        MsFormsBinary.Align(ref cursor, 4);

        // ExtraDataBlock
        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit64(propMask, 8)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4, 4));
            widthOffset = stream.FileOffsets[cursor];
            heightOffset = stream.FileOffsets[cursor + 4];
            properties["sizeSource"] = "morphDataExtraDataBlock";
            cursor += 8;
        }

        if (valueCount != null)
        {
            MsFormsBinary.Align(ref cursor, 4);
            properties["value"] = MsFormsBinary.ReadFmString(data, cursor, valueCount.Value);
            cursor += valueCount.Value.Count;
        }

        if (captionCount != null)
        {
            MsFormsBinary.Align(ref cursor, 4);
            properties["caption"] = MsFormsBinary.ReadFmString(data, cursor, captionCount.Value);
            cursor += captionCount.Value.Count;
        }

        if (groupNameCount != null)
        {
            MsFormsBinary.Align(ref cursor, 4);
            properties["groupName"] = MsFormsBinary.ReadFmString(data, cursor, groupNameCount.Value);
            cursor += groupNameCount.Value.Count;
        }

        MsFormsBinary.Align(ref cursor, 4);

        // TextProps
        if (cursor < data.Length)
        {
            if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out var textPropsEnd))
            {
                TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
            }
            else
            {
                cursor = textPropsEnd;
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadCommandButton(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02)
        {
            return null;
        }

        var cbCommandButton = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        var cursor = 8;
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsCommandButton",
            ["minorVersion"] = data[0],
            ["majorVersion"] = data[1],
            ["cbCommandButton"] = cbCommandButton,
            ["commandButtonPropMask"] = $"0x{propMask:X8}",
            ["commandButtonPropMaskOffset"] = stream.FileOffsets[4],
        };

        CountOfBytesWithCompressionFlag? captionCount = null;
        if (MsFormsBinary.HasBit(propMask, 0)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 1)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var value = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            AddVariousPropertyBits(properties, value);
        }

        if (MsFormsBinary.HasBit(propMask, 3))
        {
            captionCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "captionCount", properties));
        }

        if (MsFormsBinary.HasBit(propMask, 4)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "picturePosition", properties);
        if (MsFormsBinary.HasBit(propMask, 6)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);
        if (MsFormsBinary.HasBit(propMask, 7)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "pictureMarker", properties);
        if (MsFormsBinary.HasBit(propMask, 8)) properties["accelerator"] = (char)MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "acceleratorCode", properties);
        if (MsFormsBinary.HasBit(propMask, 10)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);

        MsFormsBinary.Align(ref cursor, 4);

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (captionCount is not null)
        {
            MsFormsBinary.Align(ref cursor, 4);
            properties["caption"] = MsFormsBinary.ReadFmString(data, cursor, captionCount.Value);
            cursor += captionCount.Value.Count;
        }

        if (MsFormsBinary.HasBit(propMask, 5))
        {
            MsFormsBinary.Align(ref cursor, 4);
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4, 4));
            widthOffset = stream.FileOffsets[cursor];
            heightOffset = stream.FileOffsets[cursor + 4];
            properties["sizeSource"] = "commandButtonExtraDataBlock";
            cursor += 8;
        }

        MsFormsBinary.Align(ref cursor, 4);

        if (cursor < data.Length)
        {
            if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out var textPropsEnd))
            {
                TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
            }
            else
            {
                cursor = textPropsEnd;
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static void AddVariousPropertyBits(Dictionary<string, object?> properties, uint value) => MsFormsBinary.AddVariousPropertyBits(properties, value);

    private static IReadOnlyList<SystemColorValue> FindSystemColors(byte[] data, int[] fileOffsets)
    {
        var colors = new List<SystemColorValue>();
        for (var offset = 0; offset + 4 <= data.Length; offset++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if ((value & 0xFF000000) == 0x80000000)
            {
                colors.Add(new SystemColorValue(offset, fileOffsets[offset], $"&H{value:X8}&"));
            }
        }

        return colors
            .GroupBy(color => color.Offset)
            .Select(group => group.First())
            .ToList();
    }

    private static IReadOnlyList<TextRun> FindTextRuns(byte[] data, int minLength)
    {
        var runs = new List<TextRun>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (!MsFormsBinary.IsPrintableAscii(data[offset]))
            {
                offset++;
                continue;
            }

            var start = offset;
            while (offset < data.Length && MsFormsBinary.IsPrintableAscii(data[offset]))
            {
                offset++;
            }

            var length = offset - start;
            if (length >= minLength)
            {
                runs.Add(new TextRun(start, Encoding.Latin1.GetString(data, start, length)));
            }
        }

        return runs;
    }

    private static PairCandidate? FindPlausiblePair(byte[] data, int start, int maxDistance)
    {
        var end = Math.Min(data.Length - 8, start + maxDistance);
        for (var offset = start; offset <= end; offset++)
        {
            var first = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            var second = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (IsPlausiblePosition(first) && IsPlausiblePosition(second))
            {
                return new PairCandidate(offset, first, second);
            }
        }

        return null;
    }

    private static bool IsPlausiblePosition(int value) => value is >= -100_000 and <= 100_000;
}
