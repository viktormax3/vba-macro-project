internal static class StreamRecordInspector
{
    private static readonly HashSet<byte> KnownTypeCodes = new([0x0C, 0x0E, 0x10, 0x11, 0x12, 0x15, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x2F, 0x39]);

    public static IReadOnlyList<StreamRecordDump> Inspect(IReadOnlyList<StorageEntryDump> streams)
    {
        return streams
            .Where(s => s.Kind == "Stream" && s.Name is "f" or "o")
            .Select(s => new StreamRecordDump(
                s.Index,
                s.Name,
                s.Size,
                s.Name == "f" ? ScanStructuralRecords(s.Data) : [],
                ScanAsciiRuns(s.Data)))
            .ToList();
    }

    private static IReadOnlyList<StructuralRecordCandidate> ScanStructuralRecords(byte[] data)
    {
        var records = new List<StructuralRecordCandidate>();

        for (var offset = 0; offset <= data.Length - 4; offset++)
        {
            var tabIndex = data[offset];
            var typeCode = data[offset + 2];
            if (data[offset + 1] != 0 || data[offset + 3] != 0 || tabIndex > 127 || !KnownTypeCodes.Contains(typeCode))
            {
                continue;
            }

            var name = FindFirstAsciiRun(data, offset + 4, 80);
            var intsAfterName = name is null
                ? []
                : ReadFollowingInt32Values(data, name.Offset + name.Length, 4);

            records.Add(new StructuralRecordCandidate(
                offset,
                tabIndex,
                typeCode,
                GuessTypeName(typeCode),
                name?.Offset,
                name?.Text,
                intsAfterName,
                Convert.ToHexString(data.AsSpan(Math.Max(0, offset - 8), Math.Min(48, data.Length - Math.Max(0, offset - 8))))));
        }

        return records;
    }

    private static IReadOnlyList<AsciiRunCandidate> ScanAsciiRuns(byte[] data)
    {
        var runs = new List<AsciiRunCandidate>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (!IsPrintableAscii(data[offset]))
            {
                offset++;
                continue;
            }

            var start = offset;
            while (offset < data.Length && IsPrintableAscii(data[offset]))
            {
                offset++;
            }

            var length = offset - start;
            if (length >= 3)
            {
                runs.Add(new AsciiRunCandidate(
                    start,
                    Encoding.Latin1.GetString(data, start, length),
                    length,
                    ReadFollowingInt32Values(data, offset, 4),
                    FindPlausibleInt32Pairs(data, offset, 48),
                    Convert.ToHexString(data.AsSpan(Math.Max(0, start - 8), Math.Min(48, data.Length - Math.Max(0, start - 8))))));
            }
        }

        return runs;
    }

    private static AsciiRunCandidate? FindFirstAsciiRun(byte[] data, int start, int maxDistance)
    {
        var limit = Math.Min(data.Length, start + maxDistance);
        for (var offset = start; offset < limit; offset++)
        {
            if (!IsPrintableAscii(data[offset]))
            {
                continue;
            }

            var runStart = offset;
            while (offset < limit && IsPrintableAscii(data[offset]))
            {
                offset++;
            }

            var length = offset - runStart;
            if (length >= 3)
            {
                return new AsciiRunCandidate(
                    runStart,
                    Encoding.Latin1.GetString(data, runStart, length),
                    length,
                    ReadFollowingInt32Values(data, offset, 4),
                    FindPlausibleInt32Pairs(data, offset, 48),
                    Convert.ToHexString(data.AsSpan(Math.Max(0, runStart - 8), Math.Min(48, data.Length - Math.Max(0, runStart - 8)))));
            }
        }

        return null;
    }

    private static IReadOnlyList<int> ReadFollowingInt32Values(byte[] data, int offset, int count)
    {
        var values = new List<int>();
        while (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }

        for (var i = 0; i < count && offset + 4 <= data.Length; i++, offset += 4)
        {
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)));
        }

        return values;
    }

    private static IReadOnlyList<Int32PairCandidate> FindPlausibleInt32Pairs(byte[] data, int offset, int maxDistance)
    {
        var pairs = new List<Int32PairCandidate>();
        var limit = Math.Min(data.Length - 8, offset + maxDistance);
        for (var cursor = offset; cursor <= limit; cursor += 2)
        {
            var first = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
            var second = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4, 4));
            if (IsPlausibleTwipValue(first) && IsPlausibleTwipValue(second))
            {
                pairs.Add(new Int32PairCandidate(cursor, first, second));
            }
        }

        return pairs;
    }

    private static bool IsPlausibleTwipValue(int value) => value is >= 0 and <= 100000;

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

    private static string GuessTypeName(byte typeCode) => typeCode switch
    {
        0x0C => "Image",
        0x0E => "Frame",
        0x10 => "SpinButton",
        0x11 => "CommandButton",
        0x12 => "TabStrip",
        0x15 => "Label",
        0x17 => "TextBox",
        0x18 => "ListBox",
        0x19 => "ComboBox",
        0x1A => "CheckBox",
        0x1B => "OptionButton",
        0x1C => "ToggleButton",
        0x2F => "ScrollBar",
        0x39 => "MultiPage",
        _ => $"0x{typeCode:X2}",
    };
}

internal sealed record StreamRecordDump(
    int StreamIndex,
    string StreamName,
    ulong Size,
    IReadOnlyList<StructuralRecordCandidate> StructuralRecords,
    IReadOnlyList<AsciiRunCandidate> TextRuns);

internal sealed record StructuralRecordCandidate(
    int MarkerOffset,
    byte TabIndex,
    byte TypeCode,
    string TypeGuess,
    int? FirstTextOffset,
    string? FirstText,
    IReadOnlyList<int> Int32AfterFirstText,
    string ContextHex);

internal sealed record AsciiRunCandidate(
    int Offset,
    string Text,
    int Length,
    IReadOnlyList<int> FollowingInt32,
    IReadOnlyList<Int32PairCandidate> PlausibleInt32Pairs,
    string ContextHex);

internal sealed record Int32PairCandidate(
    int Offset,
    int First,
    int Second);

internal sealed record ResourceHit(
    string Kind,
    int Offset,
    string SampleHex,
    string ContextHex,
    int? ResourceLength,
    FrxImageHeaderGuess? FrxImageHeader,
    MsFormsPictureHeaderGuess? MsFormsPictureHeader);

internal sealed record FrxImageHeaderGuess(
    int RecordOffset,
    int PayloadOffset,
    int HeaderLength,
    int DeclaredLength,
    int ContentLength);

internal sealed record MsFormsPictureHeaderGuess(
    int HeaderOffset,
    int ClsidOffset,
    int DeclaredLength,
    string ClsidHex);

internal sealed record StorageDirectoryEntry(
    int Index,
    string Name,
    byte Type,
    int LeftSiblingId,
    int RightSiblingId,
    int ChildId,
    int StartSector,
    ulong Size);

internal sealed record DirectoryPathInfo(string Path, string? ParentPath);
