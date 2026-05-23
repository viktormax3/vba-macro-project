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
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02) return null;

        int cursor = 0;
        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbLabel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var declaredBlockEnd = 4 + cbLabel;
        if (cbLabel < 4 || declaredBlockEnd > data.Length) return null;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsLabel",
            ["minorVersion"] = minorVersion,
            ["majorVersion"] = majorVersion,
            ["cbLabel"] = cbLabel,
            ["labelDeclaredEndLocalOffset"] = declaredBlockEnd,
            ["labelDeclaredEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, declaredBlockEnd),
            ["propMask"] = $"0x{propMask:X8}",
        };

        CountOfBytesWithCompressionFlag? captionCount = null;
        var captionCountLocalOffset = -1;

        // LabelDataBlock.  2-byte fields are aligned to 2; 4-byte fields to 4.  Do not treat
        // PicturePosition/BorderStyle/SpecialEffect as one-byte fields; doing so shifts StreamData.
        if (MsFormsBinary.HasBit(propMask, 0)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 1)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var various = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            MsFormsBinary.AddVariousPropertyBits(properties, various);
        }
        if (MsFormsBinary.HasBit(propMask, 3))
        {
            captionCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "captionCount", properties));
            captionCountLocalOffset = LocalOffsetOf(properties, "captionCount");
        }
        if (MsFormsBinary.HasBit(propMask, 4)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "picturePosition", properties);
        if (MsFormsBinary.HasBit(propMask, 6)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);
        if (MsFormsBinary.HasBit(propMask, 7)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "borderColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 8)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "borderStyle", properties);
        if (MsFormsBinary.HasBit(propMask, 9)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "specialEffect", properties);
        if (MsFormsBinary.HasBit(propMask, 10)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "pictureMarker", properties);
        if (MsFormsBinary.HasBit(propMask, 11)) properties["accelerator"] = (char)MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "acceleratorCode", properties);
        if (MsFormsBinary.HasBit(propMask, 12)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);

        MsFormsBinary.Align(ref cursor, 4);

        // LabelExtraDataBlock is included in cbLabel.
        if (captionCount is not null)
        {
            properties["caption"] = MsFormsBinary.ReadFmString(data, cursor, captionCount.Value);
            MsFormsBinary.AddStringSpan(properties, "caption", captionCount.Value, captionCountLocalOffset, cursor, stream.FileOffsets);
            cursor += MsFormsBinary.Align4(captionCount.Value.Count);
        }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 5)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            if (cursor + 8 > declaredBlockEnd) return null;
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "labelExtraDataBlock";
        }

        MsFormsBinary.Align(ref cursor, 4);
        if (cursor > declaredBlockEnd)
        {
            properties["labelBlockWarning"] = $"Parsed DataBlock/ExtraDataBlock cursor {cursor} exceeds declared end {declaredBlockEnd}.";
        }
        else
        {
            cursor = declaredBlockEnd;
        }

        // LabelControl order is DataBlock/ExtraDataBlock, then StreamData, then TextProps.
        if (MsFormsBinary.HasBit(propMask, 10) || MsFormsBinary.HasBit(propMask, 12))
        {
            properties["labelStreamDataLocalOffset"] = cursor;
            properties["labelStreamDataOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
        }
        if (MsFormsBinary.HasBit(propMask, 10))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "picture", properties))
                properties["pictureStreamWarning"] = $"Could not parse LabelStreamData.Picture at local offset {cursor}.";
        }
        if (MsFormsBinary.HasBit(propMask, 12))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "mouseIcon", properties))
                properties["mouseIconStreamWarning"] = $"Could not parse LabelStreamData.MouseIcon at local offset {cursor}.";
        }
        if (MsFormsBinary.HasBit(propMask, 10) || MsFormsBinary.HasBit(propMask, 12))
        {
            properties["labelStreamDataEndLocalOffset"] = cursor;
            properties["labelStreamDataEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
        }

        if (cursor < data.Length)
        {
            properties["textPropsExpectedLocalOffset"] = cursor;
            properties["textPropsExpectedOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
            if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out var textPropsEnd))
            {
                TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
            }
            else
            {
                cursor = textPropsEnd;
                properties["textPropsEndLocalOffset"] = cursor;
                properties["textPropsEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadImage(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02) return null;

        int cursor = 0;
        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbImage = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;

        if (cbImage < 4 || 4 + cbImage > data.Length) return null;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsImage",
            ["minorVersion"] = minorVersion,
            ["majorVersion"] = majorVersion,
            ["cbImage"] = cbImage,
            ["propMask"] = $"0x{propMask:X8}",
        };

        if (MsFormsBinary.HasBit(propMask, 2)) properties["autoSize"] = true;
        if (MsFormsBinary.HasBit(propMask, 3)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "borderColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 4)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);

        // ImageDataBlock stores the one-byte visual properties consecutively, then pads before
        // the next 2- or 4-byte property. Do not align after every byte; that shifts fSize into
        // StreamData and produces dimensions from the picture GUID.
        if (MsFormsBinary.HasBit(propMask, 5)) properties["borderStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "borderStyle", properties);
        if (MsFormsBinary.HasBit(propMask, 6)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);
        if (MsFormsBinary.HasBit(propMask, 7)) properties["pictureSizeMode"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "pictureSizeMode", properties);
        if (MsFormsBinary.HasBit(propMask, 8)) properties["specialEffect"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "specialEffect", properties);

        if (MsFormsBinary.HasBit(propMask, 10))
        {
            MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "pictureMarker", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 11))
        {
            properties["pictureAlignment"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "pictureAlignment", properties);
        }

        if (MsFormsBinary.HasBit(propMask, 12)) properties["pictureTiling"] = true;

        if (MsFormsBinary.HasBit(propMask, 13))
        {
            var various = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            MsFormsBinary.AddVariousPropertyBits(properties, various);
        }

        if (MsFormsBinary.HasBit(propMask, 14))
        {
            MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);
        }

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 9)) // fSize
        {
            var extraEnd = 4 + cbImage;
            var sizeOffset = extraEnd - 8;
            if (sizeOffset >= 8 && sizeOffset + 8 <= data.Length)
            {
                widthOffset = stream.FileOffsets[sizeOffset];
                width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sizeOffset, 4));
                heightOffset = stream.FileOffsets[sizeOffset + 4];
                height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sizeOffset + 4, 4));
                properties["sizeSource"] = "imageExtraDataBlock";
                cursor = Math.Max(cursor, extraEnd);
            }
        }

        var streamDataCursor = 4 + cbImage;
        properties["imageStreamDataLocalOffset"] = streamDataCursor;
        properties["imageStreamDataOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, streamDataCursor);

        if (MsFormsBinary.HasBit(propMask, 10))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref streamDataCursor, "picture", properties))
            {
                properties["pictureStreamWarning"] = $"Could not parse ImageStreamData.Picture at local offset {streamDataCursor}.";
            }
        }

        if (MsFormsBinary.HasBit(propMask, 14))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref streamDataCursor, "mouseIcon", properties))
            {
                properties["mouseIconStreamWarning"] = $"Could not parse ImageStreamData.MouseIcon at local offset {streamDataCursor}.";
            }
        }

        properties["imageStreamDataEndLocalOffset"] = streamDataCursor;
        properties["imageStreamDataEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, streamDataCursor);
        if (streamDataCursor != data.Length)
        {
            properties["imageStreamDataWarning"] = $"Parsed ImageStreamData ended at {streamDataCursor}, but object stream length is {data.Length}.";
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    internal static bool TryReadGuidAndPicture(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        string propertyPrefix,
        Dictionary<string, object?> properties)
    {
        var start = cursor;
        if (cursor + 24 > data.Length)
        {
            return false;
        }

        properties[$"{propertyPrefix}StreamLocalOffset"] = start;
        properties[$"{propertyPrefix}StreamOffset"] = MsFormsBinary.OffsetAt(fileOffsets, start);
        properties[$"{propertyPrefix}ClsidLocalOffset"] = cursor;
        properties[$"{propertyPrefix}ClsidOffset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
        properties[$"{propertyPrefix}Clsid"] = ReadGuidString(data, cursor);
        cursor += 16;

        var preambleLocalOffset = cursor;
        var preamble = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        properties[$"{propertyPrefix}Preamble"] = $"0x{preamble:X8}";
        properties[$"{propertyPrefix}PreambleLocalOffset"] = preambleLocalOffset;
        properties[$"{propertyPrefix}PreambleOffset"] = MsFormsBinary.OffsetAt(fileOffsets, preambleLocalOffset);

        var sizeLocalOffset = cursor;
        var pictureSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        properties[$"{propertyPrefix}DataByteCount"] = pictureSize;
        properties[$"{propertyPrefix}DataByteCountLocalOffset"] = sizeLocalOffset;
        properties[$"{propertyPrefix}DataByteCountOffset"] = MsFormsBinary.OffsetAt(fileOffsets, sizeLocalOffset);

        if (preamble != 0x0000_746Cu)
        {
            properties[$"{propertyPrefix}PreambleWarning"] = $"StdPicture preamble is 0x{preamble:X8}, expected 0x0000746C.";
        }

        if (pictureSize > int.MaxValue || cursor + (int)pictureSize > data.Length)
        {
            cursor = start;
            return false;
        }

        properties[$"{propertyPrefix}DataLocalOffset"] = cursor;
        properties[$"{propertyPrefix}DataOffset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
        properties[$"{propertyPrefix}Format"] = DetectPictureFormat(data.AsSpan(cursor, (int)pictureSize));
        cursor += (int)pictureSize;
        properties[$"{propertyPrefix}StreamEndLocalOffset"] = cursor;
        properties[$"{propertyPrefix}StreamEndOffset"] = MsFormsBinary.EndOffsetAt(fileOffsets, cursor);

        // Store the complete GuidAndPicture stream as a Base64 string so it can be exported to JSON patch.
        var pictureBytes = data.AsSpan(start, cursor - start).ToArray();
        properties[propertyPrefix] = $"base64:{Convert.ToBase64String(pictureBytes)}";

        return true;
    }

    internal static bool TrySkipGuidAndFont(byte[] data, ref int cursor)
    {
        if (cursor + 16 > data.Length) return false;

        var guid = ReadGuidString(data, cursor);
        cursor += 16;

        if (guid == "{0BE35203-8F91-11CE-9DE3-00AA004BB851}") // StdFont
        {
            if (cursor + 11 > data.Length) return false;
            var faceLen = data[cursor + 10];
            cursor += 11 + faceLen;
            return true;
        }
        
        // If it's TextProps, we don't have an easy skip method here, but UserForm usually uses StdFont.
        return false;
    }

    internal static string ReadGuidString(byte[] data, int offset)
    {
        var bytes = new byte[16];
        Array.Copy(data, offset, bytes, 0, 16);
        return new Guid(bytes).ToString("B").ToUpperInvariant();
    }

    internal static string DetectPictureFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "JPEG";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F') return "GIF";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G') return "PNG";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return "BMP";
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00) return "ICO";
        if (bytes.Length >= 4 && bytes[0] == 0xD7 && bytes[1] == 0xCD && bytes[2] == 0xC6 && bytes[3] == 0x9A) return "WMF";
        if (bytes.Length >= 44 && bytes[40] == (byte)'E' && bytes[41] == (byte)'M' && bytes[42] == (byte)'F') return "EMF";
        return "unknown";
    }

    private static ObjectStreamProperties? TryReadScrollBar(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02) return null;

        int cursor = 0;
        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbScroll = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var declaredBlockEnd = 4 + cbScroll;
        if (cbScroll < 4 || declaredBlockEnd > data.Length) return null;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsScrollBar",
            ["minorVersion"] = minorVersion,
            ["majorVersion"] = majorVersion,
            ["cbScrollBar"] = cbScroll,
            ["scrollBarDeclaredEndLocalOffset"] = declaredBlockEnd,
            ["scrollBarDeclaredEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, declaredBlockEnd),
            ["propMask"] = $"0x{propMask:X8}",
        };

        if (MsFormsBinary.HasBit(propMask, 0)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 1)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var various = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            MsFormsBinary.AddVariousPropertyBits(properties, various);
        }
        if (MsFormsBinary.HasBit(propMask, 4)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);
        if (MsFormsBinary.HasBit(propMask, 5)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "min", properties);
        if (MsFormsBinary.HasBit(propMask, 6)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "max", properties);
        if (MsFormsBinary.HasBit(propMask, 7)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "position", properties);
        if (MsFormsBinary.HasBit(propMask, 11)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "smallChange", properties);
        if (MsFormsBinary.HasBit(propMask, 12)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "largeChange", properties);
        if (MsFormsBinary.HasBit(propMask, 13)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "orientation", properties);
        if (MsFormsBinary.HasBit(propMask, 14)) properties["proportionalThumb"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "proportionalThumbRaw", properties) != 0;
        if (MsFormsBinary.HasBit(propMask, 15)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "delay", properties);
        if (MsFormsBinary.HasBit(propMask, 16)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 3)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            if (cursor + 8 > declaredBlockEnd) return null;
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "scrollBarExtraDataBlock";
        }

        MsFormsBinary.Align(ref cursor, 4);
        if (cursor > declaredBlockEnd)
        {
            properties["scrollBarBlockWarning"] = $"Parsed DataBlock/ExtraDataBlock cursor {cursor} exceeds declared end {declaredBlockEnd}.";
        }
        else
        {
            cursor = declaredBlockEnd;
        }

        if (MsFormsBinary.HasBit(propMask, 16))
        {
            properties["scrollBarStreamDataLocalOffset"] = cursor;
            properties["scrollBarStreamDataOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "mouseIcon", properties))
                properties["mouseIconStreamWarning"] = $"Could not parse ScrollBarStreamData.MouseIcon at local offset {cursor}.";
            properties["scrollBarStreamDataEndLocalOffset"] = cursor;
            properties["scrollBarStreamDataEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
        }
        if (cursor != data.Length)
        {
            properties["scrollBarStreamWarning"] = $"Parsed ScrollBar ended at {cursor}, but object stream length is {data.Length}.";
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static ObjectStreamProperties? TryReadSpinButton(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02) return null;

        int cursor = 0;
        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbSpin = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var declaredBlockEnd = 4 + cbSpin;
        if (cbSpin < 4 || declaredBlockEnd > data.Length) return null;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsSpinButton",
            ["minorVersion"] = minorVersion,
            ["majorVersion"] = majorVersion,
            ["cbSpinButton"] = cbSpin,
            ["spinButtonDeclaredEndLocalOffset"] = declaredBlockEnd,
            ["spinButtonDeclaredEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, declaredBlockEnd),
            ["propMask"] = $"0x{propMask:X8}",
        };

        if (MsFormsBinary.HasBit(propMask, 0)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 1)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 2))
        {
            var various = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            MsFormsBinary.AddVariousPropertyBits(properties, various);
        }
        if (MsFormsBinary.HasBit(propMask, 5)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "min", properties);
        if (MsFormsBinary.HasBit(propMask, 6)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "max", properties);
        if (MsFormsBinary.HasBit(propMask, 7)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "position", properties);
        if (MsFormsBinary.HasBit(propMask, 10)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "smallChange", properties);
        if (MsFormsBinary.HasBit(propMask, 11)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "orientation", properties);
        if (MsFormsBinary.HasBit(propMask, 12)) ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "delay", properties);
        if (MsFormsBinary.HasBit(propMask, 13)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);
        if (MsFormsBinary.HasBit(propMask, 14)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        if (MsFormsBinary.HasBit(propMask, 3)) // fSize
        {
            MsFormsBinary.Align(ref cursor, 4);
            if (cursor + 8 > declaredBlockEnd) return null;
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "spinButtonExtraDataBlock";
        }

        MsFormsBinary.Align(ref cursor, 4);
        if (cursor > declaredBlockEnd)
        {
            properties["spinButtonBlockWarning"] = $"Parsed DataBlock/ExtraDataBlock cursor {cursor} exceeds declared end {declaredBlockEnd}.";
        }
        else
        {
            cursor = declaredBlockEnd;
        }

        if (MsFormsBinary.HasBit(propMask, 13))
        {
            properties["spinButtonStreamDataLocalOffset"] = cursor;
            properties["spinButtonStreamDataOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "mouseIcon", properties))
                properties["mouseIconStreamWarning"] = $"Could not parse SpinButtonStreamData.MouseIcon at local offset {cursor}.";
            properties["spinButtonStreamDataEndLocalOffset"] = cursor;
            properties["spinButtonStreamDataEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
        }
        if (cursor != data.Length)
        {
            properties["spinButtonStreamWarning"] = $"Parsed SpinButton ended at {cursor}, but object stream length is {data.Length}.";
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    internal static ObjectStreamProperties? TryReadTabStrip(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02) return null;

        int cursor = 0;
        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbTab = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var declaredEnd = 4 + cbTab;
        if (cbTab < 4 || declaredEnd > data.Length) return null;

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["minorVersion"] = minorVersion,
            ["majorVersion"] = majorVersion,
            ["cbTabStrip"] = cbTab,
            ["tabStripDeclaredEndLocalOffset"] = declaredEnd,
            ["tabStripDeclaredEndOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, declaredEnd),
            ["parser"] = "msOFormsTabStrip",
            ["propMask"] = $"0x{propMask:X8}",
        };

        uint itemsSize = 0;
        uint tipStringsSize = 0;
        uint namesSize = 0;
        uint tagsSize = 0;
        uint acceleratorsSize = 0;
        int tabDataCount = 0;

        // TabStripDataBlock. Important: fNewVersion is a flag-only bit and stores no data.
        if (MsFormsBinary.HasBit(propMask, 0)) properties["listIndex"] = ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "listIndex", properties);
        if (MsFormsBinary.HasBit(propMask, 1)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 2)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, formatColor: true);
        if (MsFormsBinary.HasBit(propMask, 5)) itemsSize = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "itemsSize", properties);
        if (MsFormsBinary.HasBit(propMask, 6)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);
        if (MsFormsBinary.HasBit(propMask, 8)) properties["tabOrientation"] = ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "tabOrientation", properties);
        if (MsFormsBinary.HasBit(propMask, 9)) properties["tabStyle"] = ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "tabStyle", properties);
        if (MsFormsBinary.HasBit(propMask, 10)) properties["multiRow"] = true;
        if (MsFormsBinary.HasBit(propMask, 11)) properties["tabFixedWidth"] = ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "tabFixedWidth", properties);
        if (MsFormsBinary.HasBit(propMask, 12)) properties["tabFixedHeight"] = ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "tabFixedHeight", properties);
        if (MsFormsBinary.HasBit(propMask, 13)) properties["tooltips"] = true;
        if (MsFormsBinary.HasBit(propMask, 15)) tipStringsSize = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "tipStringsSize", properties);
        if (MsFormsBinary.HasBit(propMask, 17)) namesSize = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "namesSize", properties);
        if (MsFormsBinary.HasBit(propMask, 18))
        {
            var various = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            MsFormsBinary.AddVariousPropertyBits(properties, various);
        }
        if (MsFormsBinary.HasBit(propMask, 19)) properties["newVersion"] = true;
        if (MsFormsBinary.HasBit(propMask, 20)) properties["tabsAllocated"] = ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "tabsAllocated", properties);
        if (MsFormsBinary.HasBit(propMask, 21)) tagsSize = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "tagsSize", properties);
        if (MsFormsBinary.HasBit(propMask, 22)) tabDataCount = ReadAlignedInt32(data, stream.FileOffsets, ref cursor, "tabData", properties);
        if (MsFormsBinary.HasBit(propMask, 23)) acceleratorsSize = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "acceleratorsSize", properties);
        if (MsFormsBinary.HasBit(propMask, 24)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);
        MsFormsBinary.Align(ref cursor, 4);
        properties["tabStripDataBlockEndLocalOffset"] = cursor;
        properties["tabStripDataBlockEndOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);

        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        // TabStripExtraDataBlock.
        if (MsFormsBinary.HasBit(propMask, 4)) // fSize, MUST be set.
        {
            MsFormsBinary.Align(ref cursor, 4);
            if (cursor + 8 > declaredEnd) return null;
            widthOffset = stream.FileOffsets[cursor];
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            heightOffset = stream.FileOffsets[cursor];
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            properties["sizeSource"] = "tabStripExtraDataBlock";
        }

        if (!ReadArrayStringBlock(data, stream.FileOffsets, ref cursor, declaredEnd, itemsSize, "tabCaptions", properties)) return null;
        if (!ReadArrayStringBlock(data, stream.FileOffsets, ref cursor, declaredEnd, tipStringsSize, "tabTooltips", properties)) return null;
        if (!ReadArrayStringBlock(data, stream.FileOffsets, ref cursor, declaredEnd, namesSize, "tabNames", properties)) return null;
        if (!ReadArrayStringBlock(data, stream.FileOffsets, ref cursor, declaredEnd, tagsSize, "tabTags", properties)) return null;
        if (!ReadArrayStringBlock(data, stream.FileOffsets, ref cursor, declaredEnd, acceleratorsSize, "tabAccelerators", properties)) return null;

        properties["tabStripExtraDataEndLocalOffset"] = cursor;
        properties["tabStripExtraDataEndOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
        if (cursor != declaredEnd)
        {
            properties["tabStripCursorWarning"] = $"Parsed DataBlock/ExtraDataBlock ended at {cursor}, but cbTabStrip declares {declaredEnd}.";
            cursor = declaredEnd;
        }

        if (MsFormsBinary.HasBit(propMask, 24))
        {
            properties["tabStripStreamDataLocalOffset"] = cursor;
            properties["tabStripStreamDataOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "mouseIcon", properties))
                properties["mouseIconStreamWarning"] = $"Could not parse TabStripStreamData.MouseIcon at local offset {cursor}.";
            properties["tabStripStreamDataEndLocalOffset"] = cursor;
            properties["tabStripStreamDataEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
        }

        if (cursor < data.Length)
        {
            properties["textPropsExpectedLocalOffset"] = cursor;
            properties["textPropsExpectedOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
            if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out var textPropsEnd))
            {
                TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
            }
            else
            {
                cursor = textPropsEnd;
                properties["textPropsEndLocalOffset"] = cursor;
                properties["textPropsEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
            }
        }

        if (MsFormsBinary.HasBit(propMask, 22) && tabDataCount > 0)
        {
            var tabFlags = new List<Dictionary<string, object?>>(tabDataCount);
            var tabFlagsStart = cursor;
            for (var i = 0; i < tabDataCount; i++)
            {
                if (cursor + 4 > data.Length)
                {
                    properties["tabFlagsWarning"] = $"Could not read TabStripTabFlag {i} at local offset {cursor}.";
                    break;
                }

                var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
                tabFlags.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["index"] = i,
                    ["raw"] = flags,
                    ["rawHex"] = $"0x{flags:X8}",
                    ["visible"] = MsFormsBinary.HasBit(flags, 0),
                    ["enabled"] = MsFormsBinary.HasBit(flags, 1),
                    ["localOffset"] = cursor,
                    ["offset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor)
                });
                cursor += 4;
            }

            properties["tabFlags"] = tabFlags;
            properties["tabFlagsLocalOffset"] = tabFlagsStart;
            properties["tabFlagsOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, tabFlagsStart);
            properties["tabFlagsEndLocalOffset"] = cursor;
            properties["tabFlagsEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static int ReadAlignedInt32(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        string property,
        Dictionary<string, object?> properties)
    {
        MsFormsBinary.Align(ref cursor, 4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
        properties[property] = value;
        properties[$"{property}LocalOffset"] = cursor;
        properties[$"{property}Offset"] = MsFormsBinary.OffsetAt(fileOffsets, cursor);
        cursor += 4;
        return value;
    }

    private static bool ReadArrayStringBlock(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int declaredEnd,
        uint size,
        string property,
        Dictionary<string, object?> properties)
    {
        if (size == 0)
        {
            return true;
        }

        if (size > int.MaxValue || cursor + (int)size > declaredEnd || cursor + (int)size > data.Length)
        {
            properties[$"{property}Warning"] = $"Declared array size {size} at local offset {cursor} exceeds TabStrip ExtraDataBlock bounds.";
            return false;
        }

        var start = cursor;
        var end = cursor + (int)size;
        var values = new List<string>();
        var entries = new List<Dictionary<string, object?>>();
        var index = 0;

        while (cursor < end)
        {
            if (cursor + 4 > end)
            {
                properties[$"{property}Warning"] = $"ArrayString count at local offset {cursor} exceeds declared block end {end}.";
                cursor = end;
                break;
            }

            var countLocalOffset = cursor;
            var rawCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            var count = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(rawCount);
            cursor += 4;
            if (cursor + count.Count > end)
            {
                properties[$"{property}Warning"] = $"ArrayString value at local offset {cursor} exceeds declared block end {end}.";
                cursor = end;
                break;
            }

            var valueLocalOffset = cursor;
            var value = MsFormsBinary.ReadFmString(data, valueLocalOffset, count);
            values.Add(value);
            entries.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["index"] = index,
                ["value"] = value,
                ["countRaw"] = rawCount,
                ["countLocalOffset"] = countLocalOffset,
                ["countOffset"] = MsFormsBinary.OffsetAt(fileOffsets, countLocalOffset),
                ["dataLocalOffset"] = valueLocalOffset,
                ["dataOffset"] = MsFormsBinary.OffsetAt(fileOffsets, valueLocalOffset),
                ["byteCount"] = count.Count,
                ["paddedByteCount"] = MsFormsBinary.Align4(count.Count),
                ["compressed"] = count.Compressed
            });
            cursor += MsFormsBinary.Align4(count.Count);
            index++;
        }

        if (cursor != end)
        {
            properties[$"{property}Warning"] = $"ArrayString block ended at {cursor}, expected {end}.";
            cursor = end;
        }

        properties[property] = values;
        properties[$"{property}Entries"] = entries;
        properties[$"{property}LocalOffset"] = start;
        properties[$"{property}Offset"] = MsFormsBinary.OffsetAt(fileOffsets, start);
        properties[$"{property}ByteCount"] = size;
        properties[$"{property}EndLocalOffset"] = end;
        properties[$"{property}EndOffset"] = MsFormsBinary.EndOffsetAt(fileOffsets, end);
        return true;
    }

    private static ObjectStreamProperties? TryReadMorphData(StorageEntryDump stream, string controlType)
    {
        var data = stream.Data;
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x02) return null;

        var cbMorphData = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        if (cbMorphData < 8 || 4 + cbMorphData > data.Length) return null;

        var propMask = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4, 8));
        var cursor = 12;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsMorphData",
            ["controlType"] = controlType,
            ["cbMorphData"] = cbMorphData,
            ["propMask"] = $"0x{propMask:X16}",
        };

        const ulong morphDataUnusedHighBits = 0xFFFF_FFFE_0000_0000ul;
        if ((propMask & morphDataUnusedHighBits) != 0)
        {
            properties["morphDataPropMaskWarning"] =
                $"MorphData PropMask reserved high bits must be zero; found 0x{propMask & morphDataUnusedHighBits:X16}.";
        }

        if (MsFormsBinary.HasBit64(propMask, 0))
        {
            var value = MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "variousPropertyBitsRaw", properties);
            AddVariousPropertyBits(properties, value, controlType);
        }

        if (MsFormsBinary.HasBit64(propMask, 1)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "backColor", properties, true);
        if (MsFormsBinary.HasBit64(propMask, 2)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "foreColor", properties, true);
        if (MsFormsBinary.HasBit64(propMask, 3)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "maxLength", properties);

        // The one-byte MorphData fields form compact groups. Alignment is applied before the
        // next 2- or 4-byte property, not after every byte. This is important for ComboBox.
        if (MsFormsBinary.HasBit64(propMask, 4)) properties["borderStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "borderStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 5)) properties["scrollBars"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "scrollBars", properties);
        if (MsFormsBinary.HasBit64(propMask, 6)) properties["displayStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "displayStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 7)) properties["mousePointer"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "mousePointer", properties);

        if (MsFormsBinary.HasBit64(propMask, 9))
        {
            MsFormsBinary.Align(ref cursor, 2);
            properties["passwordChar"] = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            properties["passwordCharOffset"] = stream.FileOffsets[cursor];
            cursor += 2;
        }

        if (MsFormsBinary.HasBit64(propMask, 10)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "listWidth", properties);
        if (MsFormsBinary.HasBit64(propMask, 11)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "boundColumn", properties);
        if (MsFormsBinary.HasBit64(propMask, 12))
        {
            MsFormsBinary.Align(ref cursor, 2);
            var value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(cursor, 2));
            properties["textColumn"] = value;
            properties["textColumnOffset"] = stream.FileOffsets[cursor];
            cursor += 2;
        }
        if (MsFormsBinary.HasBit64(propMask, 13))
        {
            MsFormsBinary.Align(ref cursor, 2);
            var value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(cursor, 2));
            properties["columnCount"] = value;
            properties["columnCountOffset"] = stream.FileOffsets[cursor];
            cursor += 2;
        }
        if (MsFormsBinary.HasBit64(propMask, 14)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "listRows", properties);
        if (MsFormsBinary.HasBit64(propMask, 15)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "cColumnInfo", properties);

        if (MsFormsBinary.HasBit64(propMask, 16)) properties["matchEntry"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "matchEntry", properties);
        if (MsFormsBinary.HasBit64(propMask, 17)) properties["listStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "listStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 18)) properties["showDropButtonWhen"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "showDropButtonWhen", properties);
        if (MsFormsBinary.HasBit64(propMask, 20)) properties["dropButtonStyle"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "dropButtonStyle", properties);
        if (MsFormsBinary.HasBit64(propMask, 21)) properties["multiSelect"] = MsFormsBinary.ReadByte(data, stream.FileOffsets, ref cursor, "multiSelect", properties);

        CountOfBytesWithCompressionFlag? valueCount = null;
        CountOfBytesWithCompressionFlag? captionCount = null;
        CountOfBytesWithCompressionFlag? groupNameCount = null;
        int valueCountLocalOffset = -1;
        int captionCountLocalOffset = -1;
        int groupNameCountLocalOffset = -1;

        if (MsFormsBinary.HasBit64(propMask, 22))
        {
            valueCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "valueCount", properties));
            valueCountLocalOffset = LocalOffsetOf(properties, "valueCount");
        }
        if (MsFormsBinary.HasBit64(propMask, 23))
        {
            captionCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "captionCount", properties));
            captionCountLocalOffset = LocalOffsetOf(properties, "captionCount");
        }
        if (MsFormsBinary.HasBit64(propMask, 24)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "picturePosition", properties);
        if (MsFormsBinary.HasBit64(propMask, 25)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "borderColor", properties, true);
        if (MsFormsBinary.HasBit64(propMask, 26)) MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "specialEffect", properties);
        if (MsFormsBinary.HasBit64(propMask, 27)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "mouseIconMarker", properties);
        if (MsFormsBinary.HasBit64(propMask, 28)) MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "pictureMarker", properties);
        if (MsFormsBinary.HasBit64(propMask, 29)) properties["accelerator"] = (char)MsFormsBinary.ReadAlignedUInt16(data, stream.FileOffsets, ref cursor, "acceleratorCode", properties);
        if (MsFormsBinary.HasBit64(propMask, 32))
        {
            groupNameCount = MsFormsBinary.DecodeCountOfBytesWithCompressionFlag(MsFormsBinary.ReadAlignedUInt32(data, stream.FileOffsets, ref cursor, 4, "groupNameCount", properties));
            groupNameCountLocalOffset = LocalOffsetOf(properties, "groupNameCount");
        }
        int? width = null;
        int? height = null;
        int? widthOffset = null;
        int? heightOffset = null;

        // ExtraDataBlock is part of cbMorphData and ends immediately before TextProps/StreamData.
        // Compute the start from cbMorphData and the declared fmString sizes instead of trusting
        // the DataBlock cursor. This protects us from optional 1-byte/2-byte padding variations.
        var extraEnd = 4 + cbMorphData;
        var valueBytes = valueCount is null ? 0 : Align4(valueCount.Value.Count);
        var captionBytes = captionCount is null ? 0 : Align4(captionCount.Value.Count);
        var groupNameBytes = groupNameCount is null ? 0 : Align4(groupNameCount.Value.Count);
        var sizeBytes = MsFormsBinary.HasBit64(propMask, 8) ? 8 : 0;
        var extraStart = extraEnd - sizeBytes - valueBytes - captionBytes - groupNameBytes;

        if (extraStart < 12 || extraStart > extraEnd || extraEnd > data.Length)
        {
            extraStart = Math.Max(12, Math.Min(extraEnd, cursor));
            MsFormsBinary.Align(ref extraStart, 4);
        }

        var extraCursor = extraStart;

        if (MsFormsBinary.HasBit64(propMask, 8)) // fSize
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor, 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(extraCursor + 4, 4));
            widthOffset = stream.FileOffsets[extraCursor];
            heightOffset = stream.FileOffsets[extraCursor + 4];
            properties["sizeSource"] = "morphDataExtraDataBlock";
            extraCursor += 8;
        }

        if (valueCount != null)
        {
            properties["value"] = MsFormsBinary.ReadFmString(data, extraCursor, valueCount.Value);
            MsFormsBinary.AddStringSpan(properties, "value", valueCount.Value, valueCountLocalOffset, extraCursor, stream.FileOffsets);
            extraCursor += Align4(valueCount.Value.Count);
        }

        if (captionCount != null)
        {
            properties["caption"] = MsFormsBinary.ReadFmString(data, extraCursor, captionCount.Value);
            MsFormsBinary.AddStringSpan(properties, "caption", captionCount.Value, captionCountLocalOffset, extraCursor, stream.FileOffsets);
            extraCursor += Align4(captionCount.Value.Count);
        }

        if (groupNameCount != null)
        {
            properties["groupName"] = MsFormsBinary.ReadFmString(data, extraCursor, groupNameCount.Value);
            MsFormsBinary.AddStringSpan(properties, "groupName", groupNameCount.Value, groupNameCountLocalOffset, extraCursor, stream.FileOffsets);
            extraCursor += Align4(groupNameCount.Value.Count);
        }

        if (extraCursor > extraEnd)
        {
            properties["morphDataBlockWarning"] = $"Parsed ExtraDataBlock cursor {extraCursor} exceeds declared end {extraEnd}.";
            cursor = extraCursor;
        }
        else
        {
            cursor = extraEnd;
        }

        // MorphDataControl order is DataBlock/ExtraDataBlock, StreamData, TextProps, rgColumnInfo.
        // StreamData order is MouseIcon, then Picture.
        if (MsFormsBinary.HasBit64(propMask, 27) || MsFormsBinary.HasBit64(propMask, 28))
        {
            properties["morphDataStreamDataLocalOffset"] = cursor;
            properties["morphDataStreamDataOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
        }
        if (MsFormsBinary.HasBit64(propMask, 27))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "mouseIcon", properties))
                properties["mouseIconStreamWarning"] = $"Could not parse MorphDataStreamData.MouseIcon at local offset {cursor}.";
        }
        if (MsFormsBinary.HasBit64(propMask, 28))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "picture", properties))
                properties["pictureStreamWarning"] = $"Could not parse MorphDataStreamData.Picture at local offset {cursor}.";
        }
        if (MsFormsBinary.HasBit64(propMask, 27) || MsFormsBinary.HasBit64(propMask, 28))
        {
            properties["morphDataStreamDataEndLocalOffset"] = cursor;
            properties["morphDataStreamDataEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
        }

        if (cursor < data.Length)
        {
            properties["textPropsExpectedLocalOffset"] = cursor;
            properties["textPropsExpectedOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
            if (!TextPropsParser.TryRead(data, stream.FileOffsets, cursor, properties, out var textPropsEnd))
            {
                TextPropsParser.AddHeuristic(data, stream.FileOffsets, properties);
            }
            else
            {
                cursor = textPropsEnd;
                properties["textPropsEndLocalOffset"] = cursor;
                properties["textPropsEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
            }
        }

        if ((controlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase) ||
             controlType.Equals("ListBox", StringComparison.OrdinalIgnoreCase)) &&
            properties.TryGetValue("cColumnInfo", out var columnInfoValue) &&
            TryConvertToInt(columnInfoValue, out var columnInfoCount) &&
            columnInfoCount > 0)
        {
            var columnInfos = new List<Dictionary<string, object?>>(columnInfoCount);
            var columnCursor = cursor;
            for (var i = 0; i < columnInfoCount; i++)
            {
                if (!TryReadMorphDataColumnInfo(data, stream.FileOffsets, ref columnCursor, i, out var columnInfo))
                {
                    properties["columnInfoParseWarning"] = $"Could not read MorphDataColumnInfo {i} at local offset {columnCursor}.";
                    break;
                }

                columnInfos.Add(columnInfo);
            }

            if (columnInfos.Count > 0)
            {
                properties["columnInfo"] = columnInfos;
                properties["columnInfoCountParsed"] = columnInfos.Count;
                properties["columnInfoLocalOffset"] = cursor;
                properties["columnInfoOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
                properties["columnInfoEndLocalOffset"] = columnCursor;
                properties["columnInfoEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, columnCursor);
                cursor = columnCursor;
            }
        }

        return new ObjectStreamProperties(properties, width, height, widthOffset, heightOffset);
    }

    private static bool TryReadMorphDataColumnInfo(
        byte[] data,
        int[] fileOffsets,
        ref int cursor,
        int index,
        out Dictionary<string, object?> columnInfo)
    {
        columnInfo = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var start = cursor;
        if (start + 8 > data.Length)
        {
            return false;
        }

        var minorVersion = data[cursor++];
        var majorVersion = data[cursor++];
        var cbColumnInfo = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var end = start + 4 + cbColumnInfo;
        if (minorVersion != 0 || majorVersion != 2 || cbColumnInfo < 4 || end > data.Length)
        {
            cursor = start;
            return false;
        }

        var propMaskOffset = cursor;
        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        columnInfo["index"] = index;
        columnInfo["minorVersion"] = minorVersion;
        columnInfo["majorVersion"] = majorVersion;
        columnInfo["cbColumnInfo"] = cbColumnInfo;
        columnInfo["localOffset"] = start;
        columnInfo["offset"] = MsFormsBinary.OffsetAt(fileOffsets, start);
        columnInfo["propMask"] = $"0x{propMask:X8}";
        columnInfo["propMaskLocalOffset"] = propMaskOffset;
        columnInfo["propMaskOffset"] = MsFormsBinary.OffsetAt(fileOffsets, propMaskOffset);

        if (MsFormsBinary.HasBit(propMask, 0))
        {
            if (cursor + 4 > end)
            {
                cursor = start;
                return false;
            }

            var widthOffset = cursor;
            var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            cursor += 4;
            columnInfo["width"] = width;
            columnInfo["widthLocalOffset"] = widthOffset;
            columnInfo["widthOffset"] = MsFormsBinary.OffsetAt(fileOffsets, widthOffset);
            columnInfo["widthPt"] = Math.Round(width / (2540.0 / 72.0), 2);
        }

        cursor = end;
        return true;
    }

    private static bool TryConvertToInt(object? value, out int result)
    {
        switch (value)
        {
            case byte b:
                result = b;
                return true;
            case ushort us:
                result = us;
                return true;
            case short s:
                result = s;
                return true;
            case int i:
                result = i;
                return true;
            case uint ui when ui <= int.MaxValue:
                result = (int)ui;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static ObjectStreamProperties? TryReadCommandButton(StorageEntryDump stream)
    {
        var data = stream.Data;
        if (data.Length < 8 || data[0] != 0x00 || data[1] != 0x02)
        {
            return null;
        }

        var cbCommandButton = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        if (cbCommandButton < 4 || 4 + cbCommandButton > data.Length)
        {
            return null;
        }

        var propMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        var cursor = 8;
        var declaredBlockEnd = 4 + cbCommandButton;
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectStreamSize"] = stream.Size,
            ["parser"] = "msOFormsCommandButton",
            ["minorVersion"] = data[0],
            ["majorVersion"] = data[1],
            ["cbCommandButton"] = cbCommandButton,
            ["commandButtonPropMask"] = $"0x{propMask:X8}",
            ["commandButtonPropMaskOffset"] = stream.FileOffsets[4],
            ["commandButtonDeclaredEndLocalOffset"] = declaredBlockEnd,
            ["commandButtonDeclaredEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, declaredBlockEnd),
        };

        CountOfBytesWithCompressionFlag? captionCount = null;
        var captionCountLocalOffset = -1;
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
            captionCountLocalOffset = LocalOffsetOf(properties, "captionCount");
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
            MsFormsBinary.AddStringSpan(properties, "caption", captionCount.Value, captionCountLocalOffset, cursor, stream.FileOffsets);
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
        if (cursor > declaredBlockEnd)
        {
            properties["commandButtonBlockWarning"] = $"Parsed DataBlock/ExtraDataBlock cursor {cursor} exceeds declared end {declaredBlockEnd}.";
        }
        else
        {
            // cbCommandButton is the documented boundary between DataBlock/ExtraDataBlock and
            // StreamData.  This also accounts for optional bit-only properties such as
            // TakeFocusOnClick, which change the mask but do not add bytes to the DataBlock.
            cursor = declaredBlockEnd;
        }

        // CommandButtonControl order is:
        // Header + PropMask/DataBlock/ExtraDataBlock (cbCommandButton)
        // CommandButtonStreamData (Picture, then MouseIcon when present)
        // TextProps.
        // Large picture buttons can have kilobytes of StreamData before TextProps; do not
        // jump directly from ExtraDataBlock to TextProps or strict mode will correctly fail.
        var streamDataStart = cursor;
        if (MsFormsBinary.HasBit(propMask, 7) || MsFormsBinary.HasBit(propMask, 10))
        {
            properties["commandButtonStreamDataLocalOffset"] = streamDataStart;
            properties["commandButtonStreamDataOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, streamDataStart);
        }

        if (MsFormsBinary.HasBit(propMask, 7))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "picture", properties))
            {
                properties["pictureStreamWarning"] = $"Could not parse CommandButtonStreamData.Picture at local offset {cursor}.";
            }
        }

        if (MsFormsBinary.HasBit(propMask, 10))
        {
            if (!TryReadGuidAndPicture(data, stream.FileOffsets, ref cursor, "mouseIcon", properties))
            {
                properties["mouseIconStreamWarning"] = $"Could not parse CommandButtonStreamData.MouseIcon at local offset {cursor}.";
            }
        }

        if (MsFormsBinary.HasBit(propMask, 7) || MsFormsBinary.HasBit(propMask, 10))
        {
            properties["commandButtonStreamDataEndLocalOffset"] = cursor;
            properties["commandButtonStreamDataEndOffset"] = MsFormsBinary.EndOffsetAt(stream.FileOffsets, cursor);
        }

        if (cursor < data.Length)
        {
            properties["textPropsExpectedLocalOffset"] = cursor;
            properties["textPropsExpectedOffset"] = MsFormsBinary.OffsetAt(stream.FileOffsets, cursor);
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

    private static int LocalOffsetOf(Dictionary<string, object?> properties, string propertyName)
    {
        return properties.TryGetValue($"{propertyName}LocalOffset", out var value) && value is int offset
            ? offset
            : -1;
    }

    private static void AddVariousPropertyBits(Dictionary<string, object?> properties, uint value, string? controlType = null)
    {
        MsFormsBinary.AddVariousPropertyBits(properties, value);
        if (controlType is null)
        {
            return;
        }

        if (controlType.Equals("CheckBox", StringComparison.OrdinalIgnoreCase) ||
            controlType.Equals("OptionButton", StringComparison.OrdinalIgnoreCase))
        {
            RemoveVariousProperties(properties,
                "columnHeads",
                "matchRequired",
                "editable",
                "dragBehavior",
                "enterKeyBehavior",
                "enterFieldBehavior",
                "tabKeyBehavior",
                "autoTab",
                "multiLine");
        }
    }

    private static void RemoveVariousProperties(Dictionary<string, object?> properties, params string[] names)
    {
        foreach (var name in names)
        {
            properties.Remove(name);
        }
    }

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
