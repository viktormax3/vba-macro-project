internal static class CompoundStorageRebuilder
{
    private const int HeaderSize = 512;
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int MiniStreamCutoff = 4096;

    private const int FreeSector = unchecked((int)0xFFFFFFFF);
    private const int EndOfChain = unchecked((int)0xFFFFFFFE);
    private const int FatSector = unchecked((int)0xFFFFFFFD);
    private const int DifatSector = unchecked((int)0xFFFFFFFC);

    private const byte Red = 0x00;
    private const byte Black = 0x01;

    public static byte[] BuildFromDump(CompoundStorageDump dump)
    {
        var builder = new Builder();

        foreach (var entry in dump.Streams.Where(s => !string.IsNullOrWhiteSpace(s.Path)))
        {
            if (entry.Kind.Equals("Root", StringComparison.OrdinalIgnoreCase) ||
                entry.Kind.Equals("Storage", StringComparison.OrdinalIgnoreCase))
            {
                builder.ApplyMetadata(entry.Path, entry);
            }
        }

        foreach (var stream in dump.Streams.Where(s => s.Kind.Equals("Stream", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(stream.Path))
            {
                continue;
            }

            builder.AddStream(stream.Path, stream.Data, stream);
        }

        return builder.Build();
    }

    private sealed class Builder
    {
        private readonly Node _root = new("Root Entry", NodeType.Root);

        public void ApplyMetadata(string fullPath, StorageEntryDump metadata)
        {
            var node = EnsureNode(fullPath, metadata.Kind.Equals("Root", StringComparison.OrdinalIgnoreCase) ? NodeType.Root : NodeType.Storage);
            node.ApplyMetadata(metadata);
        }

        public void AddStream(string fullPath, byte[] data, StorageEntryDump? metadata = null)
        {
            var parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !parts[0].Equals("Root Entry", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Cannot rebuild CFB stream with unsupported path '{fullPath}'.");
            }

            var current = _root;
            for (var i = 1; i < parts.Length - 1; i++)
            {
                current = current.GetOrAddStorage(parts[i]);
            }

            current.AddOrReplaceStream(parts[^1], data, metadata);
        }

        private Node EnsureNode(string fullPath, NodeType nodeType)
        {
            var parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !parts[0].Equals("Root Entry", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Cannot apply CFB metadata to unsupported path '{fullPath}'.");
            }

            var current = _root;
            if (nodeType == NodeType.Root && parts.Length == 1)
            {
                return current;
            }

            for (var i = 1; i < parts.Length; i++)
            {
                current = current.GetOrAddStorage(parts[i]);
            }

            return current;
        }

        public byte[] Build()
        {
            var entries = new List<DirectoryEntryBuild>();
            AssignDirectoryIds(_root, entries);
            AssignChildSiblingLinks(_root);

            var regularSectors = new List<byte[]>();
            var sectorNext = new List<int>();

            int AddSector(ReadOnlySpan<byte> data)
            {
                var sector = new byte[SectorSize];
                data[..Math.Min(data.Length, SectorSize)].CopyTo(sector);
                var id = regularSectors.Count;
                regularSectors.Add(sector);
                sectorNext.Add(FreeSector);
                return id;
            }

            int AddRegularStream(ReadOnlySpan<byte> data)
            {
                if (data.Length == 0)
                {
                    return EndOfChain;
                }

                var first = -1;
                var previous = -1;
                for (var offset = 0; offset < data.Length; offset += SectorSize)
                {
                    var count = Math.Min(SectorSize, data.Length - offset);
                    var sectorId = AddSector(data.Slice(offset, count));
                    if (first < 0)
                    {
                        first = sectorId;
                    }

                    if (previous >= 0)
                    {
                        sectorNext[previous] = sectorId;
                    }

                    previous = sectorId;
                }

                sectorNext[previous] = EndOfChain;
                return first;
            }

            var miniFat = new List<int>();
            using var miniStream = new MemoryStream();

            foreach (var entry in entries.Where(e => e.Node.Type == NodeType.Stream))
            {
                var data = entry.Node.Data ?? [];
                entry.Size = (ulong)data.Length;

                if (data.Length < MiniStreamCutoff)
                {
                    if (data.Length == 0)
                    {
                        entry.StartSector = EndOfChain;
                        continue;
                    }

                    var firstMiniSector = miniFat.Count;
                    var miniSectorCount = Align(data.Length, MiniSectorSize) / MiniSectorSize;
                    for (var i = 0; i < miniSectorCount; i++)
                    {
                        miniFat.Add(i == miniSectorCount - 1 ? EndOfChain : firstMiniSector + i + 1);
                    }

                    miniStream.Write(data);
                    var padding = miniSectorCount * MiniSectorSize - data.Length;
                    if (padding > 0)
                    {
                        miniStream.Write(new byte[padding]);
                    }

                    entry.StartSector = firstMiniSector;
                }
                else
                {
                    entry.StartSector = AddRegularStream(data);
                }
            }

            var rootEntry = entries.Single(e => e.Node.Type == NodeType.Root);
            var miniStreamBytes = miniStream.ToArray();
            rootEntry.Size = (ulong)miniStreamBytes.Length;
            rootEntry.StartSector = miniStreamBytes.Length == 0 ? EndOfChain : AddRegularStream(miniStreamBytes);

            var miniFatBytes = BuildInt32Stream(miniFat);
            var firstMiniFatSector = miniFatBytes.Length == 0 ? EndOfChain : AddRegularStream(miniFatBytes);
            var miniFatSectorCount = miniFatBytes.Length == 0 ? 0 : Align(miniFatBytes.Length, SectorSize) / SectorSize;

            var directoryBytes = BuildDirectoryStream(entries);
            var firstDirectorySector = AddRegularStream(directoryBytes);

            var nonFatSectorCount = regularSectors.Count;
            var fatSectorCount = ComputeFatSectorCount(nonFatSectorCount);
            var fatSectorIds = Enumerable.Range(nonFatSectorCount, fatSectorCount).ToArray();

            var fatEntries = Enumerable.Repeat(FreeSector, fatSectorCount * (SectorSize / 4)).ToArray();
            for (var i = 0; i < sectorNext.Count; i++)
            {
                fatEntries[i] = sectorNext[i];
            }

            foreach (var fatSectorId in fatSectorIds)
            {
                fatEntries[fatSectorId] = FatSector;
            }

            var fatSectors = BuildFatSectors(fatEntries, fatSectorCount);

            using var output = new MemoryStream();
            WriteHeader(output, fatSectorCount, firstDirectorySector, firstMiniFatSector, miniFatSectorCount, fatSectorIds);
            foreach (var sector in regularSectors)
            {
                output.Write(sector);
            }

            foreach (var sector in fatSectors)
            {
                output.Write(sector);
            }

            return output.ToArray();
        }

        private static void AssignDirectoryIds(Node root, List<DirectoryEntryBuild> entries)
        {
            void Visit(Node node)
            {
                node.DirectoryId = entries.Count;
                entries.Add(new DirectoryEntryBuild(node));
                foreach (var child in node.Children.Values.OrderBy(c => c.Name, CfbNameComparer.Instance))
                {
                    Visit(child);
                }
            }

            Visit(root);
        }

        private static void AssignChildSiblingLinks(Node root)
        {
            void Visit(Node node)
            {
                var children = node.Children.Values.OrderBy(c => c.Name, CfbNameComparer.Instance).ToList();
                node.ChildId = AssignBalancedSiblingTree(children, 0, children.Count);
                foreach (var child in children)
                {
                    Visit(child);
                }
            }

            Visit(root);
        }

        private static int AssignBalancedSiblingTree(IReadOnlyList<Node> children, int start, int count)
        {
            if (count <= 0)
            {
                return -1;
            }

            // MS-CFB requires each sibling set to be a valid red-black tree.
            // A merely balanced binary tree is not enough: if we preserve colors from the
            // original directory while changing the tree shape, we can produce red-red or
            // black-height violations that our parser tolerates but IStorage/MSForms can reject.
            // Build a fresh red-black tree from the sorted children instead.
            var range = children.Skip(start).Take(count).ToList();
            return AssignRedBlackSiblingTree(range);
        }

        private static int AssignRedBlackSiblingTree(IReadOnlyList<Node> sortedChildren)
        {
            if (sortedChildren.Count == 0)
            {
                return -1;
            }

            foreach (var child in sortedChildren)
            {
                child.LeftSiblingId = -1;
                child.RightSiblingId = -1;
                child.DirectoryColor = Red;
            }

            var byId = sortedChildren.ToDictionary(c => c.DirectoryId);
            var parents = new Dictionary<Node, Node?>();
            Node? root = null;

            foreach (var node in sortedChildren)
            {
                InsertRedBlackNode(node, ref root, byId, parents);
            }

            if (root is null)
            {
                return -1;
            }

            root.DirectoryColor = Black;
            return root.DirectoryId;
        }

        private static void InsertRedBlackNode(
            Node node,
            ref Node? root,
            IReadOnlyDictionary<int, Node> byId,
            Dictionary<Node, Node?> parents)
        {
            Node? parent = null;
            var current = root;

            while (current is not null)
            {
                parent = current;
                current = CfbNameComparer.Instance.Compare(node.Name, current.Name) < 0
                    ? GetNodeById(byId, current.LeftSiblingId)
                    : GetNodeById(byId, current.RightSiblingId);
            }

            parents[node] = parent;
            if (parent is null)
            {
                root = node;
            }
            else if (CfbNameComparer.Instance.Compare(node.Name, parent.Name) < 0)
            {
                parent.LeftSiblingId = node.DirectoryId;
            }
            else
            {
                parent.RightSiblingId = node.DirectoryId;
            }

            node.LeftSiblingId = -1;
            node.RightSiblingId = -1;
            node.DirectoryColor = Red;

            FixRedBlackInsert(node, ref root, byId, parents);
        }

        private static void FixRedBlackInsert(
            Node node,
            ref Node? root,
            IReadOnlyDictionary<int, Node> byId,
            Dictionary<Node, Node?> parents)
        {
            while (parents.TryGetValue(node, out var parent) && parent is not null && parent.DirectoryColor == Red)
            {
                if (!parents.TryGetValue(parent, out var grandparent) || grandparent is null)
                {
                    break;
                }

                if (parent.DirectoryId == grandparent.LeftSiblingId)
                {
                    var uncle = GetNodeById(byId, grandparent.RightSiblingId);
                    if (uncle is not null && uncle.DirectoryColor == Red)
                    {
                        parent.DirectoryColor = Black;
                        uncle.DirectoryColor = Black;
                        grandparent.DirectoryColor = Red;
                        node = grandparent;
                    }
                    else
                    {
                        if (node.DirectoryId == parent.RightSiblingId)
                        {
                            node = parent;
                            RotateLeft(node, ref root, byId, parents);
                            parent = parents[node];
                            if (parent is null || !parents.TryGetValue(parent, out grandparent) || grandparent is null)
                            {
                                break;
                            }
                        }

                        parent.DirectoryColor = Black;
                        grandparent.DirectoryColor = Red;
                        RotateRight(grandparent, ref root, byId, parents);
                    }
                }
                else
                {
                    var uncle = GetNodeById(byId, grandparent.LeftSiblingId);
                    if (uncle is not null && uncle.DirectoryColor == Red)
                    {
                        parent.DirectoryColor = Black;
                        uncle.DirectoryColor = Black;
                        grandparent.DirectoryColor = Red;
                        node = grandparent;
                    }
                    else
                    {
                        if (node.DirectoryId == parent.LeftSiblingId)
                        {
                            node = parent;
                            RotateRight(node, ref root, byId, parents);
                            parent = parents[node];
                            if (parent is null || !parents.TryGetValue(parent, out grandparent) || grandparent is null)
                            {
                                break;
                            }
                        }

                        parent.DirectoryColor = Black;
                        grandparent.DirectoryColor = Red;
                        RotateLeft(grandparent, ref root, byId, parents);
                    }
                }
            }

            if (root is not null)
            {
                root.DirectoryColor = Black;
            }
        }

        private static void RotateLeft(
            Node node,
            ref Node? root,
            IReadOnlyDictionary<int, Node> byId,
            Dictionary<Node, Node?> parents)
        {
            var pivot = GetNodeById(byId, node.RightSiblingId)
                ?? throw new CliException($"Cannot rotate CFB directory tree left at '{node.Name}': missing right sibling.");

            node.RightSiblingId = pivot.LeftSiblingId;
            var pivotLeft = GetNodeById(byId, pivot.LeftSiblingId);
            if (pivotLeft is not null)
            {
                parents[pivotLeft] = node;
            }

            parents.TryGetValue(node, out var parent);
            parents[pivot] = parent;

            if (parent is null)
            {
                root = pivot;
            }
            else if (node.DirectoryId == parent.LeftSiblingId)
            {
                parent.LeftSiblingId = pivot.DirectoryId;
            }
            else
            {
                parent.RightSiblingId = pivot.DirectoryId;
            }

            pivot.LeftSiblingId = node.DirectoryId;
            parents[node] = pivot;
        }

        private static void RotateRight(
            Node node,
            ref Node? root,
            IReadOnlyDictionary<int, Node> byId,
            Dictionary<Node, Node?> parents)
        {
            var pivot = GetNodeById(byId, node.LeftSiblingId)
                ?? throw new CliException($"Cannot rotate CFB directory tree right at '{node.Name}': missing left sibling.");

            node.LeftSiblingId = pivot.RightSiblingId;
            var pivotRight = GetNodeById(byId, pivot.RightSiblingId);
            if (pivotRight is not null)
            {
                parents[pivotRight] = node;
            }

            parents.TryGetValue(node, out var parent);
            parents[pivot] = parent;

            if (parent is null)
            {
                root = pivot;
            }
            else if (node.DirectoryId == parent.RightSiblingId)
            {
                parent.RightSiblingId = pivot.DirectoryId;
            }
            else
            {
                parent.LeftSiblingId = pivot.DirectoryId;
            }

            pivot.RightSiblingId = node.DirectoryId;
            parents[node] = pivot;
        }

        private static Node? GetNodeById(IReadOnlyDictionary<int, Node> byId, int directoryId)
        {
            return directoryId < 0 ? null : byId.GetValueOrDefault(directoryId);
        }

        private static byte[] BuildDirectoryStream(IReadOnlyList<DirectoryEntryBuild> entries)
        {
            var length = Align(Math.Max(entries.Count, 1) * 128, SectorSize);
            var bytes = new byte[length];
            for (var i = 0; i < entries.Count; i++)
            {
                WriteDirectoryEntry(bytes.AsSpan(i * 128, 128), entries[i]);
            }

            return bytes;
        }

        private static void WriteDirectoryEntry(Span<byte> entry, DirectoryEntryBuild build)
        {
            var node = build.Node;
            var nameBytes = Encoding.Unicode.GetBytes(node.Name + "\0");
            if (nameBytes.Length > 64)
            {
                throw new CliException($"CFB directory name '{node.Name}' is too long.");
            }

            nameBytes.CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)nameBytes.Length);
            entry[0x42] = node.Type switch
            {
                NodeType.Root => 5,
                NodeType.Storage => 1,
                NodeType.Stream => 2,
                _ => 0
            };
            entry[0x43] = node.DirectoryColor;
            BinaryPrimitives.WriteInt32LittleEndian(entry[0x44..], node.LeftSiblingId);
            BinaryPrimitives.WriteInt32LittleEndian(entry[0x48..], node.RightSiblingId);
            BinaryPrimitives.WriteInt32LittleEndian(entry[0x4C..], node.ChildId);
            WriteHexBytes(entry[0x50..0x60], node.ClsidHex);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x60..], node.StateBits);
            WriteHexBytes(entry[0x64..0x6C], node.CreationTimeHex);
            WriteHexBytes(entry[0x6C..0x74], node.ModifiedTimeHex);
            var startSector = node.Type == NodeType.Storage ? 0 : build.StartSector;
            BinaryPrimitives.WriteInt32LittleEndian(entry[0x74..], startSector);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0x78..], build.Size);
        }

        private static void WriteHexBytes(Span<byte> target, string? hex)
        {
            target.Clear();
            if (string.IsNullOrWhiteSpace(hex) || hex.Length != target.Length * 2)
            {
                return;
            }

            for (var i = 0; i < target.Length; i++)
            {
                target[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
        }

        private static byte[] BuildInt32Stream(IReadOnlyList<int> values)
        {
            if (values.Count == 0)
            {
                return [];
            }

            var length = Align(values.Count * 4, SectorSize);
            var bytes = new byte[length];
            for (var i = 0; i < length / 4; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), i < values.Count ? values[i] : FreeSector);
            }

            return bytes;
        }

        private static IReadOnlyList<byte[]> BuildFatSectors(int[] fatEntries, int fatSectorCount)
        {
            var sectors = new List<byte[]>(fatSectorCount);
            for (var sectorIndex = 0; sectorIndex < fatSectorCount; sectorIndex++)
            {
                var sector = new byte[SectorSize];
                for (var i = 0; i < SectorSize / 4; i++)
                {
                    var entryIndex = sectorIndex * (SectorSize / 4) + i;
                    BinaryPrimitives.WriteInt32LittleEndian(
                        sector.AsSpan(i * 4, 4),
                        entryIndex < fatEntries.Length ? fatEntries[entryIndex] : FreeSector);
                }

                sectors.Add(sector);
            }

            return sectors;
        }

        private static int ComputeFatSectorCount(int nonFatSectorCount)
        {
            var fatSectorCount = 1;
            while (true)
            {
                var required = (int)Math.Ceiling((nonFatSectorCount + fatSectorCount) / (double)(SectorSize / 4));
                if (required == fatSectorCount)
                {
                    return required;
                }

                fatSectorCount = required;
            }
        }

        private static void WriteHeader(
            Stream output,
            int fatSectorCount,
            int firstDirectorySector,
            int firstMiniFatSector,
            int miniFatSectorCount,
            IReadOnlyList<int> fatSectorIds)
        {
            if (fatSectorIds.Count > 109)
            {
                throw new CliException("Rebuilt CFB requires DIFAT sectors; this first rebuilder pass supports up to 109 FAT sectors.");
            }

            var header = new byte[HeaderSize];
            new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18, 2), 0x003E); // minor
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A, 2), 0x0003); // version 3
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C, 2), 0xFFFE); // little endian
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E, 2), 9);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20, 2), 6);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x28, 4), 0); // directory sectors for v3
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x2C, 4), fatSectorCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x30, 4), firstDirectorySector);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x34, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x38, 4), MiniStreamCutoff);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x3C, 4), firstMiniFatSector);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x40, 4), miniFatSectorCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x44, 4), EndOfChain);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x48, 4), 0);

            for (var i = 0; i < 109; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    header.AsSpan(0x4C + i * 4, 4),
                    i < fatSectorIds.Count ? fatSectorIds[i] : FreeSector);
            }

            output.Write(header);
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    }

    private sealed class Node(string name, NodeType type)
    {
        public string Name { get; } = name;
        public NodeType Type { get; } = type;
        public SortedDictionary<string, Node> Children { get; } = new(CfbNameComparer.Instance);
        public byte[]? Data { get; set; }
        public int DirectoryId { get; set; } = -1;
        public int LeftSiblingId { get; set; } = -1;
        public int RightSiblingId { get; set; } = -1;
        public int ChildId { get; set; } = -1;
        public byte DirectoryColor { get; set; } = 1;
        public string ClsidHex { get; set; } = type == NodeType.Root ? "F0692AC6DC16CE119E9800AA00574A4F" : "00000000000000000000000000000000";
        public uint StateBits { get; set; }
        public string CreationTimeHex { get; set; } = "0000000000000000";
        public string ModifiedTimeHex { get; set; } = "0000000000000000";

        public void ApplyMetadata(StorageEntryDump metadata)
        {
            DirectoryColor = metadata.DirectoryColor;
            ClsidHex = metadata.ClsidHex;
            StateBits = metadata.StateBits;
            CreationTimeHex = metadata.CreationTimeHex;
            ModifiedTimeHex = metadata.ModifiedTimeHex;
        }

        public Node GetOrAddStorage(string name)
        {
            if (Children.TryGetValue(name, out var existing))
            {
                if (existing.Type != NodeType.Storage)
                {
                    throw new CliException($"CFB path collision: '{name}' already exists as a stream.");
                }

                return existing;
            }

            var storage = new Node(name, NodeType.Storage);
            Children[name] = storage;
            return storage;
        }

        public void AddOrReplaceStream(string name, byte[] data, StorageEntryDump? metadata = null)
        {
            if (Children.TryGetValue(name, out var existing) && existing.Type != NodeType.Stream)
            {
                throw new CliException($"CFB path collision: '{name}' already exists as a storage.");
            }

            var stream = new Node(name, NodeType.Stream) { Data = data.ToArray() };
            if (metadata is not null)
            {
                stream.ApplyMetadata(metadata);
            }

            Children[name] = stream;
        }
    }

    private sealed class CfbNameComparer : IComparer<string>
    {
        public static readonly CfbNameComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            var lengthCompare = x.Length.CompareTo(y.Length);
            if (lengthCompare != 0)
            {
                return lengthCompare;
            }

            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }

    private enum NodeType
    {
        Root,
        Storage,
        Stream
    }

    private sealed class DirectoryEntryBuild(Node node)
    {
        public Node Node { get; } = node;
        public int StartSector { get; set; } = EndOfChain;
        public ulong Size { get; set; }
    }
}
