
internal static class LegacyNameScanParser
{
    public static IReadOnlyList<StructuredControlRecord> Scan(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames)
    {
        var data = stream.Data;
        var candidates = new List<StructuredControlCandidate>();
        for (var textOffset = 4; textOffset < data.Length; textOffset++)
        {
            if (!MsFormsBinary.IsPrintableAscii(data[textOffset]) ||
                (textOffset > 0 && IsIdentifierPart(data[textOffset - 1])))
            {
                continue;
            }

            var marker = FindStructuredMarkerBefore(data, textOffset);
            if (marker is null || !ControlTypeSchema.TryGetMsFormsType(marker.TypeCode, out var type))
            {
                continue;
            }

            var rawName = ReadNullTerminatedAscii(data, textOffset, maxLength: 64);
            if (rawName is null || IsKnownNonControlIdentifier(rawName))
            {
                continue;
            }

            var name = NormalizeStructuredName(rawName, knownControlNames);
            var placement = TryReadStreamPlacement(data, stream.FileOffsets, textOffset + rawName.Length);
            if (placement is null)
            {
                continue;
            }

            candidates.Add(new StructuredControlCandidate(marker, textOffset, rawName, name, type, placement));
        }

        candidates = candidates
            .OrderBy(candidate => candidate.NameOffset)
            .ToList();

        var records = new List<StructuredControlRecord>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var endOffset = i + 1 < candidates.Count ? candidates[i + 1].Marker.Offset : data.Length;
            records.Add(new StructuredControlRecord(
                stream,
                candidate.Marker,
                candidate.Marker.Offset,
                endOffset,
                candidate.NameOffset,
                candidate.RawName.Length,
                candidate.RawName,
                candidate.Name,
                candidate.Type,
                candidate.Placement,
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["parser"] = "structuredStorageFStream",
                    ["legacyScanner"] = true
                },
                ObjectStream: null));
        }

        return records;
    }

    private static string? ReadNullTerminatedAscii(byte[] data, int offset, int maxLength)
    {
        if (offset >= data.Length || !IsIdentifierStart(data[offset]))
        {
            return null;
        }

        var end = offset;
        var limit = Math.Min(data.Length, offset + maxLength);
        while (end < limit && data[end] != 0)
        {
            if (!IsIdentifierPart(data[end]))
            {
                return end - offset < 3 ? null : Encoding.Latin1.GetString(data, offset, end - offset);
            }

            end++;
        }

        return end - offset < 3 ? null : Encoding.Latin1.GetString(data, offset, end - offset);
    }

    private static bool IsIdentifierStart(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ||
        value is >= (byte)'a' and <= (byte)'z' ||
        value == (byte)'_';

    private static bool IsIdentifierPart(byte value) =>
        IsIdentifierStart(value) ||
        value is >= (byte)'0' and <= (byte)'9';

    private static bool IsKnownNonControlIdentifier(string name) =>
        name.Equals("Tahoma", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Forms", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Object", StringComparison.OrdinalIgnoreCase);

    private static ControlTypeMarker? FindStructuredMarkerBefore(byte[] data, int textOffset)
    {
        var candidates = new List<(ControlTypeMarker Marker, int Gap)>();
        var start = Math.Max(0, textOffset - 16);
        for (var offset = textOffset - 4; offset >= start; offset--)
        {
            var tabIndex = data[offset];
            var typeCode = data[offset + 2];
            if (data[offset + 1] == 0 &&
                data[offset + 3] == 0 &&
                tabIndex <= 0x7F &&
                ControlTypeSchema.TryGetMsFormsType(typeCode, out _))
            {
                candidates.Add((new ControlTypeMarker(offset, tabIndex, typeCode), textOffset - offset - 4));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Gap % 4 == 0 ? 0 : 1)
            .ThenBy(candidate => candidate.Gap)
            .ThenBy(candidate => candidate.Marker.TabIndex)
            .Select(candidate => candidate.Marker)
            .FirstOrDefault();
    }

    private static Placement? TryReadStreamPlacement(byte[] data, int[] fileOffsets, int afterNameOffset)
    {
        var searchStart = Math.Min(data.Length, afterNameOffset);
        while (searchStart < data.Length && data[searchStart] == 0)
        {
            searchStart++;
        }

        var searchEnd = Math.Min(data.Length - 8, searchStart + 56);
        for (var offset = searchStart; offset <= searchEnd; offset += 2)
        {
            var left = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            var top = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (IsPlausiblePosition(left) && IsPlausiblePosition(top))
            {
                return new Placement(left, top, null, null, fileOffsets[offset], fileOffsets[offset + 4], null, null);
            }
        }

        return null;
    }

    private static bool IsPlausiblePosition(int value) => value is >= 0 and <= 40_000;

    private static string NormalizeStructuredName(string binaryName, IReadOnlySet<string>? knownControlNames)
    {
        if (knownControlNames is not null && knownControlNames.Contains(binaryName))
        {
            return binaryName;
        }

        foreach (var prefix in StandardNamePrefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (!binaryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digitOffset = prefix.Length;
            var offset = digitOffset;
            while (offset < binaryName.Length && char.IsDigit(binaryName[offset]))
            {
                offset++;
            }

            if (offset == digitOffset || offset == binaryName.Length)
            {
                continue;
            }

            var candidate = binaryName[..offset];
            if (knownControlNames?.Contains(candidate) == true)
            {
                return candidate;
            }

            var suffix = binaryName[offset..];
            if (suffix.Length <= 3 && suffix.All(char.IsLower))
            {
                return candidate;
            }
        }

        return binaryName;
    }

    private static readonly string[] StandardNamePrefixes =
    [
        "CommandButton", "TextBox", "CheckBox", "Frame", "Label", "ComboBox",
        "SpinButton", "OptionButton", "Image", "ToggleButton", "ScrollBar",
        "TabStrip", "MultiPage", "Page", "ListBox", "CustButton",
    ];
}
