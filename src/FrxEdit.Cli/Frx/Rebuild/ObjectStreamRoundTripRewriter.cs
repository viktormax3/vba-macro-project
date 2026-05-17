internal static class ObjectStreamRoundTripRewriter
{
    public static CompoundStorageDump RewriteObjectStreams(
        CompoundStorageDump dump,
        LayoutInspection layout,
        ObjectStreamRewriteMode mode = ObjectStreamRewriteMode.RoundTrip)
    {
        var slicesByObjectStreamPath = BuildSlicesByObjectStreamPath(layout);
        if (slicesByObjectStreamPath.Count == 0)
        {
            if (mode != ObjectStreamRewriteMode.RoundTrip && LayoutHasObjectPayloads(layout))
            {
                throw new CliException($"Object stream rebuild mode '{mode}' found no object slices even though the parsed layout contains object payload metadata. This usually means a numeric metadata type was not recognized by TryGetInt.");
            }

            return dump;
        }

        var rewrittenObjectStreams = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var sizeUpdates = new List<ObjectStreamSizeUpdate>();
        foreach (var stream in dump.Streams)
        {
            if (!stream.Kind.Equals("Stream", StringComparison.OrdinalIgnoreCase) ||
                !stream.Name.Equals("o", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(stream.Path) ||
                !slicesByObjectStreamPath.TryGetValue(stream.Path, out var slices))
            {
                continue;
            }

            var rewritten = RewriteObjectStream(stream.Path, stream.Data, slices, mode, sizeUpdates);
            rewrittenObjectStreams[stream.Path] = rewritten;
        }

        if (rewrittenObjectStreams.Count == 0)
        {
            if (mode != ObjectStreamRewriteMode.RoundTrip)
            {
                throw new CliException($"Object stream rebuild mode '{mode}' found object slices but did not rewrite any 'o' streams. Check storagePath/object stream path matching.");
            }

            return dump;
        }

        var updatesByFormStreamPath = sizeUpdates
            .GroupBy(update => update.FormStreamPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var controlsByFormStreamPath = mode == ObjectStreamRewriteMode.FormAndObjectPatch
            ? BuildControlsByFormStreamPath(layout)
            : new Dictionary<string, List<ControlInfo>>(StringComparer.OrdinalIgnoreCase);

        var streams = dump.Streams.Select(stream =>
        {
            if (!string.IsNullOrWhiteSpace(stream.Path) && rewrittenObjectStreams.TryGetValue(stream.Path, out var rewrittenObject))
            {
                return WithNewData(stream, rewrittenObject);
            }

            if (mode is ObjectStreamRewriteMode.NormalizeStrings or ObjectStreamRewriteMode.PatchProperties or ObjectStreamRewriteMode.FormAndObjectPatch &&
                !string.IsNullOrWhiteSpace(stream.Path) &&
                stream.Kind.Equals("Stream", StringComparison.OrdinalIgnoreCase) &&
                stream.Name.Equals("f", StringComparison.OrdinalIgnoreCase))
            {
                var patched = stream.Data;
                var changed = false;

                if (updatesByFormStreamPath.TryGetValue(stream.Path, out var updates))
                {
                    patched = PatchObjectStreamSizes(stream with { Data = patched }, updates);
                    changed = true;
                }

                if (mode == ObjectStreamRewriteMode.FormAndObjectPatch &&
                    controlsByFormStreamPath.TryGetValue(stream.Path, out var controls))
                {
                    patched = PatchFormSiteScalars(stream with { Data = patched }, controls);
                    changed = true;
                }

                if (changed)
                {
                    return WithNewData(stream, patched);
                }
            }

            return stream;
        }).ToList();

        return dump with { Streams = streams };
    }

    private static Dictionary<string, List<ControlInfo>> BuildControlsByFormStreamPath(LayoutInspection layout)
    {
        var result = new Dictionary<string, List<ControlInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in layout.Controls)
        {
            if (control.Properties is null || !TryGetString(control.Properties, "streamPath", out var streamPath) || string.IsNullOrWhiteSpace(streamPath))
            {
                continue;
            }

            if (!result.TryGetValue(streamPath, out var controls))
            {
                controls = [];
                result[streamPath] = controls;
            }

            controls.Add(control);
        }

        return result;
    }

    private static byte[] PatchFormSiteScalars(StorageEntryDump formStream, IReadOnlyList<ControlInfo> controls)
    {
        var output = formStream.Data.ToArray();
        foreach (var control in controls)
        {
            if (control.Left is int left && control.LeftOffset is int leftFileOffset)
            {
                WriteInt32ByFileOffset(formStream, output, leftFileOffset, left, $"{control.Name}.left");
            }

            if (control.Top is int top && control.TopOffset is int topFileOffset)
            {
                WriteInt32ByFileOffset(formStream, output, topFileOffset, top, $"{control.Name}.top");
            }

            if (control.Properties is not null &&
                TryGetInt(control.Properties, "tabIndex", out var tabIndex) &&
                TryGetInt(control.Properties, "tabIndexOffset", out var tabIndexFileOffset))
            {
                WriteUInt16ByFileOffset(formStream, output, tabIndexFileOffset, tabIndex, $"{control.Name}.tabIndex");
            }
        }

        return output;
    }

    private static void WriteInt32ByFileOffset(StorageEntryDump stream, byte[] output, int fileOffset, int value, string context)
    {
        if (!TryMapFileOffsetToLocal(stream.FileOffsets, fileOffset, out var localOffset) || localOffset < 0 || localOffset + 4 > output.Length)
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': file offset {fileOffset} is not part of the form stream.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(localOffset, 4), value);
    }

    private static void WriteUInt16ByFileOffset(StorageEntryDump stream, byte[] output, int fileOffset, int value, string context)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new CliException($"Cannot patch {context}: value {value} is outside UInt16 range.");
        }

        if (!TryMapFileOffsetToLocal(stream.FileOffsets, fileOffset, out var localOffset) || localOffset < 0 || localOffset + 2 > output.Length)
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': file offset {fileOffset} is not part of the form stream.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(localOffset, 2), unchecked((ushort)value));
    }

    private static StorageEntryDump WithNewData(StorageEntryDump stream, byte[] data) =>
        stream with
        {
            Data = data,
            Size = (ulong)data.Length,
            SampleHex = Convert.ToHexString(data.AsSpan(0, Math.Min(32, data.Length))),
            DataHex = data.Length <= 512 ? Convert.ToHexString(data) : null
        };

    private static byte[] PatchObjectStreamSizes(StorageEntryDump formStream, IReadOnlyList<ObjectStreamSizeUpdate> updates)
    {
        var output = formStream.Data.ToArray();
        foreach (var update in updates)
        {
            if (!TryMapFileOffsetToLocal(formStream.FileOffsets, update.SiteObjectStreamSizeFileOffset, out var localOffset))
            {
                throw new CliException($"Cannot update ObjectStreamSize for '{update.ControlName}' in '{formStream.Path}': file offset {update.SiteObjectStreamSizeFileOffset} is not part of the form stream.");
            }

            if (localOffset < 0 || localOffset + 4 > output.Length)
            {
                throw new CliException($"Cannot update ObjectStreamSize for '{update.ControlName}' in '{formStream.Path}': local offset {localOffset} is outside the form stream.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(localOffset, 4), unchecked((uint)update.NewSize));
        }

        return output;
    }

    private static bool TryMapFileOffsetToLocal(int[] fileOffsets, int fileOffset, out int localOffset)
    {
        for (var i = 0; i < fileOffsets.Length; i++)
        {
            if (fileOffsets[i] == fileOffset)
            {
                localOffset = i;
                return true;
            }
        }

        localOffset = -1;
        return false;
    }


    private static bool LayoutHasObjectPayloads(LayoutInspection layout) =>
        layout.Controls.Any(control => control.Properties is not null &&
            TryGetString(control.Properties, "storagePath", out _) &&
            TryGetInt(control.Properties, "objectStreamLocalOffset", out _) &&
            TryGetInt(control.Properties, "objectStreamSize", out var size) &&
            size > 0);

    private static Dictionary<string, List<ObjectStreamSlice>> BuildSlicesByObjectStreamPath(LayoutInspection layout)
    {
        var result = new Dictionary<string, List<ObjectStreamSlice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in layout.Controls)
        {
            var props = control.Properties;
            if (props is null)
            {
                continue;
            }

            if (!TryGetString(props, "storagePath", out var storagePath) || string.IsNullOrWhiteSpace(storagePath))
            {
                continue;
            }

            if (!TryGetInt(props, "objectStreamLocalOffset", out var start) ||
                !TryGetInt(props, "objectStreamSize", out var size) ||
                size <= 0)
            {
                continue;
            }

            var objectStreamPath = $"{storagePath}/o";
            if (!result.TryGetValue(objectStreamPath, out var slices))
            {
                slices = [];
                result[objectStreamPath] = slices;
            }

            slices.Add(new ObjectStreamSlice(control, start, size));
        }

        foreach (var pair in result)
        {
            pair.Value.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return result;
    }

    private static byte[] RewriteObjectStream(
        string path,
        byte[] original,
        IReadOnlyList<ObjectStreamSlice> slices,
        ObjectStreamRewriteMode mode,
        List<ObjectStreamSizeUpdate> sizeUpdates)
    {
        using var output = new MemoryStream(original.Length);
        var cursor = 0;
        foreach (var slice in slices)
        {
            if (slice.Start < cursor)
            {
                throw new CliException($"Cannot rebuild object stream '{path}': slice for '{slice.Control.Name}' overlaps a previous slice.");
            }

            if (slice.Start + slice.Size > original.Length)
            {
                throw new CliException($"Cannot rebuild object stream '{path}': slice for '{slice.Control.Name}' exceeds stream length.");
            }

            if (slice.Start > cursor)
            {
                output.Write(original, cursor, slice.Start - cursor);
            }

            var originalPayload = original.AsSpan(slice.Start, slice.Size);
            var payload = mode switch
            {
                ObjectStreamRewriteMode.ActiveSerializeFixed => ObjectPayloadSerializer.SerializeFixedLength(slice.Control, originalPayload),
                ObjectStreamRewriteMode.NormalizeStrings => ObjectPayloadSerializer.SerializeNormalizedStrings(slice.Control, originalPayload),
                ObjectStreamRewriteMode.PatchProperties or ObjectStreamRewriteMode.FormAndObjectPatch => ObjectPayloadSerializer.SerializePatchedProperties(slice.Control, originalPayload),
                _ => originalPayload.ToArray()
            };

            if (mode == ObjectStreamRewriteMode.ActiveSerializeFixed && payload.Length != slice.Size)
            {
                throw new CliException($"Cannot fixed-length serialize object stream '{path}': serializer for '{slice.Control.Name}' returned {payload.Length} bytes but the site declares {slice.Size} bytes.");
            }

            if (mode is ObjectStreamRewriteMode.NormalizeStrings or ObjectStreamRewriteMode.PatchProperties or ObjectStreamRewriteMode.FormAndObjectPatch && payload.Length != slice.Size)
            {
                AddSizeUpdate(slice.Control, payload.Length, sizeUpdates);
            }

            output.Write(payload, 0, payload.Length);
            cursor = slice.Start + slice.Size;
        }

        if (cursor < original.Length)
        {
            output.Write(original, cursor, original.Length - cursor);
        }

        return output.ToArray();
    }

    private static void AddSizeUpdate(ControlInfo control, int newSize, List<ObjectStreamSizeUpdate> sizeUpdates)
    {
        var props = control.Properties;
        if (props is null ||
            !TryGetString(props, "streamPath", out var formStreamPath) ||
            !TryGetInt(props, "siteObjectStreamSizeOffset", out var siteObjectStreamSizeOffset))
        {
            throw new CliException($"Cannot normalize object payload for '{control.Name}': missing streamPath/siteObjectStreamSizeOffset metadata needed to update FormSiteData.");
        }

        sizeUpdates.Add(new ObjectStreamSizeUpdate(control.Name, formStreamPath, siteObjectStreamSizeOffset, newSize));
    }

    private static bool TryGetString(Dictionary<string, object?> props, string key, out string value)
    {
        value = string.Empty;
        if (!props.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is string text)
        {
            value = text;
            return true;
        }

        value = raw.ToString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
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

    private sealed record ObjectStreamSlice(ControlInfo Control, int Start, int Size);
    private sealed record ObjectStreamSizeUpdate(string ControlName, string FormStreamPath, int SiteObjectStreamSizeFileOffset, int NewSize);
}

internal enum ObjectStreamRewriteMode
{
    RoundTrip,
    ActiveSerializeFixed,
    NormalizeStrings,
    PatchProperties,
    FormAndObjectPatch
}
