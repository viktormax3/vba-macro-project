internal static class CompoundStorageInspector
{
    private const int HeaderSize = 512;
    private const int EndOfChain = unchecked((int)0xFFFFFFFE);
    private const int FreeSector = unchecked((int)0xFFFFFFFF);
    private const int FatSector = unchecked((int)0xFFFFFFFD);
    private const int DifatSector = unchecked((int)0xFFFFFFFC);

    public static CompoundStorageDump Inspect(byte[] bytes, int oleOffset)
    {
        if (bytes.Length < oleOffset + HeaderSize)
        {
            throw new CliException("OLE compound header is incomplete.");
        }

        var header = bytes.AsSpan(oleOffset, HeaderSize);
        var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(header[0x1E..]);
        var miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(header[0x20..]);
        var firstDirectorySector = ReadInt32(header, 0x30);
        var miniStreamCutoff = ReadInt32(header, 0x38);
        var firstMiniFatSector = ReadInt32(header, 0x3C);
        var miniFatSectorCount = ReadInt32(header, 0x40);
        var fatSectorIds = ReadDifat(header);
        var fat = ReadFat(bytes, oleOffset, sectorSize, fatSectorIds);
        var directoryBytes = ReadRegularStream(bytes, oleOffset, sectorSize, fat, firstDirectorySector);
        var entries = ReadDirectory(directoryBytes);
        var pathMap = BuildDirectoryPaths(entries);
        var root = entries.FirstOrDefault(e => e.Type == 5);
        var rootRead = root is null
            ? new StreamRead([], [])
            : TrimToSize(ReadRegularStreamWithOffsets(bytes, oleOffset, sectorSize, fat, root.StartSector), root.Size);
        var rootStream = rootRead.Data;
        var miniFat = ReadMiniFat(bytes, oleOffset, sectorSize, fat, firstMiniFatSector, miniFatSectorCount);

        var streamIndex = 0;
        var streams = entries
            .Where(e => e.Type is 1 or 2 or 5)
            .Select(e =>
            {
                var index = streamIndex++;
                var read = e.Type switch
                {
                    2 => ReadStreamData(bytes, oleOffset, sectorSize, miniSectorSize, fat, miniFat, rootRead, e, miniStreamCutoff),
                    5 => rootRead,
                    _ => new StreamRead([], [])
                };
                var data = read.Data;
                var sample = Convert.ToHexString(data.AsSpan(0, Math.Min(32, data.Length)));
                var kind = e.Type switch
                {
                    5 => "Root",
                    1 => "Storage",
                    _ => "Stream"
                };
                var dump = new StorageEntryDump(
                    index,
                    e.Name,
                    kind,
                    e.StartSector,
                    e.Size,
                    e.Type == 2 && e.Size < (ulong)miniStreamCutoff,
                    sample,
                    data.Length <= 512 ? Convert.ToHexString(data) : null,
                    DetectResourceKind(sample),
                    ScanResourceHits(data),
                    data,
                    read.FileOffsets,
                    e.Color,
                    e.ClsidHex,
                    e.StateBits,
                    e.CreationTimeHex,
                    e.ModifiedTimeHex);

                if (pathMap.TryGetValue(e.Index, out var pathInfo))
                {
                    dump = dump with
                    {
                        Path = pathInfo.Path,
                        ParentPath = pathInfo.ParentPath
                    };
                }

                return dump;
            })
            .OrderBy(e => e.Index)
            .ToList();

        return new CompoundStorageDump(sectorSize, miniSectorSize, miniStreamCutoff, fatSectorIds.Count, streams);
    }

    private static List<int> ReadDifat(ReadOnlySpan<byte> header)
    {
        var sectors = new List<int>();
        for (var offset = 0x4C; offset < 0x200; offset += 4)
        {
            var sector = ReadInt32(header, offset);
            if (sector != FreeSector && sector != EndOfChain && sector != FatSector && sector != DifatSector)
            {
                sectors.Add(sector);
            }
        }

        return sectors;
    }

    private static int[] ReadFat(byte[] bytes, int oleOffset, int sectorSize, IReadOnlyList<int> fatSectorIds)
    {
        var entries = new List<int>();
        foreach (var sectorId in fatSectorIds)
        {
            var sector = ReadSector(bytes, oleOffset, sectorSize, sectorId);
            for (var i = 0; i < sector.Length; i += 4)
            {
                entries.Add(BinaryPrimitives.ReadInt32LittleEndian(sector[i..(i + 4)]));
            }
        }

        return entries.ToArray();
    }

    private static int[] ReadMiniFat(byte[] bytes, int oleOffset, int sectorSize, int[] fat, int firstSector, int sectorCount)
    {
        if (firstSector < 0 || sectorCount <= 0)
        {
            return [];
        }

        var data = ReadRegularStream(bytes, oleOffset, sectorSize, fat, firstSector);
        var entries = new int[data.Length / 4];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4, 4));
        }

        return entries;
    }

    private static byte[] ReadRegularStream(byte[] bytes, int oleOffset, int sectorSize, int[] fat, int firstSector)
    {
        if (firstSector < 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        foreach (var sectorId in FollowChain(fat, firstSector))
        {
            output.Write(ReadSector(bytes, oleOffset, sectorSize, sectorId));
        }

        return output.ToArray();
    }

    private static StreamRead ReadRegularStreamWithOffsets(byte[] bytes, int oleOffset, int sectorSize, int[] fat, int firstSector)
    {
        if (firstSector < 0)
        {
            return new StreamRead([], []);
        }

        using var output = new MemoryStream();
        var offsets = new List<int>();
        foreach (var sectorId in FollowChain(fat, firstSector))
        {
            var sectorOffset = GetSectorOffset(bytes, oleOffset, sectorSize, sectorId);
            if (sectorOffset < 0)
            {
                continue;
            }

            output.Write(bytes.AsSpan(sectorOffset, sectorSize));
            for (var i = 0; i < sectorSize; i++)
            {
                offsets.Add(sectorOffset + i);
            }
        }

        return new StreamRead(output.ToArray(), offsets.ToArray());
    }

    private static byte[] ReadMiniStream(byte[] rootStream, int miniSectorSize, int[] miniFat, int firstMiniSector, ulong size)
    {
        if (firstMiniSector < 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        foreach (var sectorId in FollowChain(miniFat, firstMiniSector))
        {
            var offset = sectorId * miniSectorSize;
            if (offset < 0 || offset >= rootStream.Length)
            {
                break;
            }

            output.Write(rootStream.AsSpan(offset, Math.Min(miniSectorSize, rootStream.Length - offset)));
            if ((ulong)output.Length >= size)
            {
                break;
            }
        }

        var data = output.ToArray();
        return data.Length > (int)size ? data[..(int)size] : data;
    }

    private static StreamRead ReadMiniStream(StreamRead rootStream, int miniSectorSize, int[] miniFat, int firstMiniSector, ulong size)
    {
        if (firstMiniSector < 0)
        {
            return new StreamRead([], []);
        }

        using var output = new MemoryStream();
        var offsets = new List<int>();
        foreach (var sectorId in FollowChain(miniFat, firstMiniSector))
        {
            var offset = sectorId * miniSectorSize;
            if (offset < 0 || offset >= rootStream.Data.Length)
            {
                break;
            }

            var count = Math.Min(miniSectorSize, rootStream.Data.Length - offset);
            output.Write(rootStream.Data.AsSpan(offset, count));
            offsets.AddRange(rootStream.FileOffsets.Skip(offset).Take(count));
            if ((ulong)output.Length >= size)
            {
                break;
            }
        }

        return TrimToSize(new StreamRead(output.ToArray(), offsets.ToArray()), size);
    }

    private static IEnumerable<int> FollowChain(int[] fat, int firstSector)
    {
        var seen = new HashSet<int>();
        var current = firstSector;
        while (current >= 0 && current < fat.Length && seen.Add(current))
        {
            yield return current;
            current = fat[current];
            if (current == EndOfChain || current == FreeSector)
            {
                yield break;
            }
        }
    }

    private static byte[] ReadSector(byte[] bytes, int oleOffset, int sectorSize, int sectorId)
    {
        var offset = GetSectorOffset(bytes, oleOffset, sectorSize, sectorId);
        if (offset < 0)
        {
            return [];
        }

        return bytes.AsSpan(offset, sectorSize).ToArray();
    }

    private static int GetSectorOffset(byte[] bytes, int oleOffset, int sectorSize, int sectorId)
    {
        var offset = oleOffset + HeaderSize + sectorId * sectorSize;
        return sectorId < 0 || offset < 0 || offset + sectorSize > bytes.Length ? -1 : offset;
    }

    private static List<StorageDirectoryEntry> ReadDirectory(byte[] directoryBytes)
    {
        var entries = new List<StorageDirectoryEntry>();
        for (var offset = 0; offset + 128 <= directoryBytes.Length; offset += 128)
        {
            var entry = directoryBytes.AsSpan(offset, 128);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x40..]);
            var type = entry[0x42];
            if (type == 0 || nameLength < 2)
            {
                continue;
            }

            var name = Encoding.Unicode.GetString(entry[..Math.Min(nameLength - 2, 64)]).TrimEnd('\0');
            var color = entry[0x43];
            var leftSiblingId = ReadInt32(entry, 0x44);
            var rightSiblingId = ReadInt32(entry, 0x48);
            var childId = ReadInt32(entry, 0x4C);
            var clsidHex = Convert.ToHexString(entry.Slice(0x50, 16));
            var stateBits = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x60..]);
            var creationTimeHex = Convert.ToHexString(entry.Slice(0x64, 8));
            var modifiedTimeHex = Convert.ToHexString(entry.Slice(0x6C, 8));
            var startSector = ReadInt32(entry, 0x74);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x78..]);
            entries.Add(new StorageDirectoryEntry(offset / 128, name, type, color, leftSiblingId, rightSiblingId, childId, clsidHex, stateBits, creationTimeHex, modifiedTimeHex, startSector, size));
        }

        return entries;
    }

    private static Dictionary<int, DirectoryPathInfo> BuildDirectoryPaths(IReadOnlyList<StorageDirectoryEntry> entries)
    {
        var result = new Dictionary<int, DirectoryPathInfo>();
        var byIndex = entries.ToDictionary(e => e.Index);
        var root = entries.FirstOrDefault(e => e.Type == 5);
        if (root is null)
        {
            return result;
        }

        result[root.Index] = new DirectoryPathInfo(root.Name, null);
        VisitSiblingTree(root.ChildId, root.Name, byIndex, result);
        return result;
    }

    private static void VisitSiblingTree(
        int entryId,
        string parentPath,
        IReadOnlyDictionary<int, StorageDirectoryEntry> entries,
        Dictionary<int, DirectoryPathInfo> paths)
    {
        if (entryId < 0 || !entries.TryGetValue(entryId, out var entry))
        {
            return;
        }

        VisitSiblingTree(entry.LeftSiblingId, parentPath, entries, paths);

        var path = string.IsNullOrEmpty(parentPath) ? entry.Name : parentPath + "/" + entry.Name;
        paths[entry.Index] = new DirectoryPathInfo(path, parentPath);

        if (entry.Type is 1 or 5)
        {
            VisitSiblingTree(entry.ChildId, path, entries, paths);
        }

        VisitSiblingTree(entry.RightSiblingId, parentPath, entries, paths);
    }

    private static StreamRead ReadStreamData(
        byte[] bytes,
        int oleOffset,
        int sectorSize,
        int miniSectorSize,
        int[] fat,
        int[] miniFat,
        StreamRead rootStream,
        StorageDirectoryEntry entry,
        int miniStreamCutoff)
    =>
        entry.Size < (ulong)miniStreamCutoff
            ? ReadMiniStream(rootStream, miniSectorSize, miniFat, entry.StartSector, entry.Size)
            : TrimToSize(ReadRegularStreamWithOffsets(bytes, oleOffset, sectorSize, fat, entry.StartSector), entry.Size);

    private static byte[] TrimToSize(byte[] data, ulong size)
    {
        var targetSize = (int)Math.Min((ulong)data.Length, size);
        return data.Length == targetSize ? data : data[..targetSize];
    }

    private static StreamRead TrimToSize(StreamRead read, ulong size)
    {
        var targetSize = (int)Math.Min((ulong)read.Data.Length, size);
        return read.Data.Length == targetSize
            ? read
            : new StreamRead(read.Data[..targetSize], read.FileOffsets[..targetSize]);
    }

    private static string? DetectResourceKind(string sampleHex)
    {
        if (sampleHex.StartsWith("00000100", StringComparison.OrdinalIgnoreCase)) return "ico";
        if (sampleHex.StartsWith("28000000", StringComparison.OrdinalIgnoreCase)) return "dib";
        if (sampleHex.StartsWith("424D", StringComparison.OrdinalIgnoreCase)) return "bmp";
        if (sampleHex.StartsWith("89504E47", StringComparison.OrdinalIgnoreCase)) return "png";
        if (sampleHex.Contains("4D6963726F736F667420466F726D73", StringComparison.OrdinalIgnoreCase)) return "forms";
        return null;
    }

    private static IReadOnlyList<ResourceHit> ScanResourceHits(byte[] data)
    {
        var patterns = new (string Kind, byte[] Pattern)[]
        {
            ("ico", [0x00, 0x00, 0x01, 0x00]),
            ("dib", [0x28, 0x00, 0x00, 0x00]),
            ("bmp", [0x42, 0x4D]),
            ("png", [0x89, 0x50, 0x4E, 0x47]),
        };

        var hits = new List<ResourceHit>();
        foreach (var (kind, pattern) in patterns)
        {
            for (var i = 0; i <= data.Length - pattern.Length; i++)
            {
                if (!data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                {
                    continue;
                }

                if (kind == "ico" && !LooksLikeIconDirectory(data, i))
                {
                    continue;
                }

                if (kind == "dib" && !LooksLikeDibHeader(data, i))
                {
                    continue;
                }

                var resourceLength = TryGetResourceLength(data, i, kind);
                var contextStart = Math.Max(0, i - 32);
                var contextLength = Math.Min(64, data.Length - contextStart);
                hits.Add(new ResourceHit(
                    kind,
                    i,
                    Convert.ToHexString(data.AsSpan(i, Math.Min(32, data.Length - i))),
                    Convert.ToHexString(data.AsSpan(contextStart, contextLength)),
                    resourceLength,
                    TryDetectFrxImageHeader(data, i),
                    TryDetectMsFormsPictureHeader(data, i, resourceLength)));
            }
        }

        return hits.OrderBy(h => h.Offset).Take(32).ToList();
    }

    private static int? TryGetResourceLength(byte[] data, int offset, string kind)
    {
        if (kind == "ico" && offset + 6 <= data.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
            if (count is <= 0 or > 20 || offset + 6 + count * 16 > data.Length)
            {
                return null;
            }

            var end = 0;
            for (var i = 0; i < count; i++)
            {
                var entryOffset = offset + 6 + i * 16;
                var bytesInResource = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entryOffset + 8, 4));
                var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entryOffset + 12, 4));
                if (bytesInResource > int.MaxValue || imageOffset > int.MaxValue)
                {
                    return null;
                }

                end = Math.Max(end, (int)(imageOffset + bytesInResource));
            }

            return end > 0 && offset + end <= data.Length ? end : null;
        }

        return null;
    }

    private static FrxImageHeaderGuess? TryDetectFrxImageHeader(byte[] data, int contentOffset)
    {
        var longHeader = TryReadImageHeader(data, contentOffset, 24);
        if (longHeader is not null)
        {
            return longHeader;
        }

        return TryReadImageHeader(data, contentOffset, 8);
    }

    private static FrxImageHeaderGuess? TryReadImageHeader(byte[] data, int contentOffset, int headerLength)
    {
        var payloadOffset = contentOffset - headerLength;
        var recordOffset = payloadOffset - 4;
        if (recordOffset < 0 || payloadOffset < 0)
        {
            return null;
        }

        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(recordOffset, 4));
        var contentLengthOffset = payloadOffset + (headerLength == 24 ? 20 : 4);
        if (contentLengthOffset < payloadOffset || contentLengthOffset + 4 > data.Length)
        {
            return null;
        }

        var contentLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(contentLengthOffset, 4));
        if (declaredLength <= 0 || contentLength <= 0 || declaredLength != contentLength + headerLength)
        {
            return null;
        }

        if (contentOffset + contentLength > data.Length)
        {
            return null;
        }

        return new FrxImageHeaderGuess(recordOffset, payloadOffset, headerLength, declaredLength, contentLength);
    }

    private static MsFormsPictureHeaderGuess? TryDetectMsFormsPictureHeader(byte[] data, int contentOffset, int? resourceLength)
    {
        if (resourceLength is null || contentOffset < 32)
        {
            return null;
        }

        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(contentOffset - 4, 4));
        if (declaredLength != resourceLength)
        {
            return null;
        }

        var clsidOffset = contentOffset - 24;
        var clsidHex = Convert.ToHexString(data.AsSpan(clsidOffset, 16));
        if (!clsidHex.Equals("0452E30B918FCE119DE300AA004BB851", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new MsFormsPictureHeaderGuess(contentOffset - 32, clsidOffset, declaredLength, clsidHex);
    }

    private static bool LooksLikeIconDirectory(byte[] data, int offset)
    {
        if (offset + 22 > data.Length)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
        if (count is <= 0 or > 20)
        {
            return false;
        }

        var width = data[offset + 6];
        var height = data[offset + 7];
        var reserved = data[offset + 9];
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 10, 2));
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 12, 2));
        var bytesInResource = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 14, 4));
        var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 18, 4));

        return width <= 128 &&
               height <= 128 &&
               reserved == 0 &&
               planes is 0 or 1 &&
               bitCount is 0 or 1 or 4 or 8 or 24 or 32 &&
               bytesInResource > 0 &&
               imageOffset >= 6 + count * 16 &&
               offset + imageOffset < data.Length;
    }

    private static bool LooksLikeDibHeader(byte[] data, int offset)
    {
        if (offset + 16 > data.Length)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8, 4));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 12, 2));
        return width is > 0 and <= 4096 && height is > 0 and <= 8192 && planes == 1;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..(offset + 4)]);
}

internal sealed record CompoundStorageDump(
    int SectorSize,
    int MiniSectorSize,
    int MiniStreamCutoff,
    int FatSectorCount,
    IReadOnlyList<StorageEntryDump> Streams);

internal sealed record StorageEntryDump(
    int Index,
    string Name,
    string Kind,
    int StartSector,
    ulong Size,
    bool IsMiniStream,
    string SampleHex,
    string? DataHex,
    string? ResourceKind,
    IReadOnlyList<ResourceHit> ResourceHits,
    [property: JsonIgnore] byte[] Data,
    [property: JsonIgnore] int[] FileOffsets,
    byte DirectoryColor = 1,
    string ClsidHex = "00000000000000000000000000000000",
    uint StateBits = 0,
    string CreationTimeHex = "0000000000000000",
    string ModifiedTimeHex = "0000000000000000")
{
    public string Path { get; init; } = string.Empty;
    public string? ParentPath { get; init; }

    public static StorageEntryDump CreateSegment(byte[] data, int[] fileOffsets)
    {
        return new StorageEntryDump(
            -1, "Segment", "Segment", -1, (ulong)data.Length, false,
            string.Empty, null, null, [], data, fileOffsets);
    }
}

internal sealed record StreamRead(byte[] Data, int[] FileOffsets);
