internal static class ObjectStreamRoundTripRewriter
{
    public static CompoundStorageDump RewriteObjectStreams(CompoundStorageDump dump, LayoutInspection layout, bool activeSerialize = false)
    {
        var slicesByObjectStreamPath = BuildSlicesByObjectStreamPath(layout);
        if (slicesByObjectStreamPath.Count == 0)
        {
            return dump;
        }

        var streams = dump.Streams.Select(stream =>
        {
            if (!stream.Kind.Equals("Stream", StringComparison.OrdinalIgnoreCase) ||
                !stream.Name.Equals("o", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(stream.Path) ||
                !slicesByObjectStreamPath.TryGetValue(stream.Path, out var slices))
            {
                return stream;
            }

            var rewritten = RewriteObjectStream(stream.Path, stream.Data, slices, activeSerialize);
            return stream with
            {
                Data = rewritten,
                Size = (ulong)rewritten.Length,
                SampleHex = Convert.ToHexString(rewritten.AsSpan(0, Math.Min(32, rewritten.Length))),
                DataHex = rewritten.Length <= 512 ? Convert.ToHexString(rewritten) : null
            };
        }).ToList();

        return dump with { Streams = streams };
    }

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

    private static byte[] RewriteObjectStream(string path, byte[] original, IReadOnlyList<ObjectStreamSlice> slices, bool activeSerialize)
    {
        using var output = new MemoryStream(original.Length);
        var cursor = 0;
        foreach (var slice in slices)
        {
            if (slice.Start < cursor)
            {
                throw new CliException($"Cannot logical-roundtrip object stream '{path}': slice for '{slice.Control.Name}' overlaps a previous slice.");
            }

            if (slice.Start + slice.Size > original.Length)
            {
                throw new CliException($"Cannot logical-roundtrip object stream '{path}': slice for '{slice.Control.Name}' exceeds stream length.");
            }

            if (slice.Start > cursor)
            {
                output.Write(original, cursor, slice.Start - cursor);
            }

            var payload = activeSerialize
                ? ObjectPayloadSerializer.SerializeFixedLength(slice.Control, original.AsSpan(slice.Start, slice.Size))
                : original.AsSpan(slice.Start, slice.Size).ToArray();

            if (payload.Length != slice.Size)
            {
                throw new CliException($"Cannot fixed-length serialize object stream '{path}': serializer for '{slice.Control.Name}' returned {payload.Length} bytes but the site declares {slice.Size} bytes.");
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
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed):
                value = parsed;
                return true;
            case string text when int.TryParse(text, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private sealed record ObjectStreamSlice(ControlInfo Control, int Start, int Size);
}
