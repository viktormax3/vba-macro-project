internal static class ObjectStreamRoundTripRewriter
{
    public static CompoundStorageDump RewriteObjectStreams(
        CompoundStorageDump dump,
        LayoutInspection layout,
        ObjectStreamRewriteMode mode = ObjectStreamRewriteMode.RoundTrip)
    {
        var slicesByObjectStreamPath = BuildSlicesByObjectStreamPath(layout);
        var additionsByObjectStreamPath = BuildAdditionsByObjectStreamPath(layout);
        var removalsByObjectStreamPath = BuildRemovedByObjectStreamPath(layout);
        if (slicesByObjectStreamPath.Count == 0 && additionsByObjectStreamPath.Count == 0 && removalsByObjectStreamPath.Count == 0)
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
                string.IsNullOrWhiteSpace(stream.Path))
            {
                continue;
            }

            slicesByObjectStreamPath.TryGetValue(stream.Path, out var slices);
            additionsByObjectStreamPath.TryGetValue(stream.Path, out var additions);
            removalsByObjectStreamPath.TryGetValue(stream.Path, out var removals);
            if ((slices is null || slices.Count == 0) &&
                (additions is null || additions.Count == 0) &&
                (removals is null || removals.Count == 0))
            {
                continue;
            }

            var rewritten = RewriteObjectStream(stream.Path, stream.Data, slices ?? [], additions ?? [], removals ?? [], mode, sizeUpdates);
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

        var removedByFormStreamPath = mode == ObjectStreamRewriteMode.FormAndObjectPatch
            ? BuildRemovedControlsByFormStreamPath(layout)
            : new Dictionary<string, List<ControlInfo>>(StringComparer.OrdinalIgnoreCase);

        var removedStoragePaths = (layout.RemovedStoragePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

                var hasTargetControls = controlsByFormStreamPath.TryGetValue(stream.Path, out var controls);
                var hasRemovedControls = removedByFormStreamPath.TryGetValue(stream.Path, out var removedControls);
                if (mode == ObjectStreamRewriteMode.FormAndObjectPatch && (hasTargetControls || hasRemovedControls))
                {
                    updatesByFormStreamPath.TryGetValue(stream.Path, out var updates);
                    patched = RewriteFormSiteData(stream with { Data = patched }, controls ?? [], updates ?? [], removedControls ?? []);
                    changed = true;
                }
                else if (updatesByFormStreamPath.TryGetValue(stream.Path, out var updates))
                {
                    patched = PatchObjectStreamSizes(stream with { Data = patched }, updates);
                    changed = true;
                }

                if (changed)
                {
                    return WithNewData(stream, patched);
                }
            }

            return stream;
        }).Where(stream => !IsUnderRemovedStorage(stream.Path, removedStoragePaths)).ToList();

        return dump with { Streams = streams };
    }

    private static bool IsUnderRemovedStorage(string? streamPath, IReadOnlyList<string> removedStoragePaths)
    {
        if (string.IsNullOrWhiteSpace(streamPath) || removedStoragePaths.Count == 0)
        {
            return false;
        }

        foreach (var storagePath in removedStoragePaths)
        {
            if (streamPath.Equals(storagePath, StringComparison.OrdinalIgnoreCase) ||
                streamPath.StartsWith(storagePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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


    private static Dictionary<string, List<ControlInfo>> BuildRemovedControlsByFormStreamPath(LayoutInspection layout)
    {
        var result = new Dictionary<string, List<ControlInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in layout.RemovedControls ?? [])
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


    private static byte[] RewriteFormSiteData(
        StorageEntryDump formStream,
        IReadOnlyList<ControlInfo> controls,
        IReadOnlyList<ObjectStreamSizeUpdate> sizeUpdates,
        IReadOnlyList<ControlInfo> removedControls)
    {
        var original = formStream.Data;
        var slices = BuildFormSiteSlices(formStream, controls);
        var additions = BuildAddedFormSiteSlices(formStream, controls);
        var removals = BuildRemovedFormSiteSlices(formStream, removedControls);
        if (slices.Count == 0 && additions.Count == 0 && removals.Count == 0)
        {
            var fallback = formStream.Data.ToArray();
            if (sizeUpdates.Count > 0)
            {
                fallback = PatchObjectStreamSizes(formStream with { Data = fallback }, sizeUpdates);
            }

            return PatchFormSiteScalars(formStream with { Data = fallback }, controls);
        }

        var objectSizeUpdatesByControl = sizeUpdates
            .GroupBy(update => update.ControlName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().NewSize, StringComparer.OrdinalIgnoreCase);

        using var output = new MemoryStream(original.Length);
        var cursor = 0;
        var totalDelta = 0;
        var depthDelta = 0;
        var countDelta = additions.Count - removals.Count;

        var allOriginalSlices = slices
            .Select(slice => new FormSiteRewriteSlice(slice.Control, slice.Start, slice.Size, IsRemoval: false))
            .Concat(removals.Select(slice => new FormSiteRewriteSlice(slice.Control, slice.Start, slice.Size, IsRemoval: true)))
            .OrderBy(slice => slice.Start)
            .ToList();

        var shouldRewriteDepths = additions.Count > 0 || removals.Count > 0;
        if (shouldRewriteDepths)
        {
            if (allOriginalSlices.Count == 0)
            {
                throw new CliException($"Cannot rebuild SiteDepthsAndTypes in '{formStream.Path}': no existing FormSiteData slices were found.");
            }

            var referenceControl = allOriginalSlices[0].Control;
            var depthStart = GetSiteDepthStart(referenceControl, formStream.Path);
            var sitesStart = allOriginalSlices[0].Start;
            if (depthStart < 0 || depthStart > sitesStart)
            {
                throw new CliException($"Cannot rebuild SiteDepthsAndTypes in form stream '{formStream.Path}': invalid SiteDepthsAndTypes range.");
            }

            output.Write(original, 0, depthStart);
            var rebuiltDepths = SerializeSiteDepthsAndTypes(slices.Select(s => s.Control).Concat(additions.Select(a => a.Control)).ToList(), formStream.Path);
            output.Write(rebuiltDepths, 0, rebuiltDepths.Length);
            depthDelta = rebuiltDepths.Length - (sitesStart - depthStart);
            cursor = sitesStart;
        }

        foreach (var slice in allOriginalSlices)
        {
            if (slice.Start < cursor)
            {
                throw new CliException($"Cannot rebuild form stream '{formStream.Path}': site slice for '{slice.Control.Name}' overlaps a previous site.");
            }

            if (slice.Start + slice.Size > original.Length)
            {
                throw new CliException($"Cannot rebuild form stream '{formStream.Path}': site slice for '{slice.Control.Name}' exceeds stream length.");
            }

            if (slice.Start > cursor)
            {
                output.Write(original, cursor, slice.Start - cursor);
            }

            if (slice.IsRemoval)
            {
                totalDelta -= slice.Size;
                cursor = slice.Start + slice.Size;
                continue;
            }

            objectSizeUpdatesByControl.TryGetValue(slice.Control.Name, out var newObjectSize);
            var sitePayload = SerializeFormSite(slice.Control, formStream, original.AsSpan(slice.Start, slice.Size), slice.Start, newObjectSize);
            totalDelta += sitePayload.Length - slice.Size;
            output.Write(sitePayload, 0, sitePayload.Length);
            cursor = slice.Start + slice.Size;
        }

        foreach (var addition in additions)
        {
            if (addition.Start < 0 || addition.Start + addition.Size > original.Length)
            {
                throw new CliException($"Cannot add '{addition.Control.Name}' to form stream '{formStream.Path}': template site slice exceeds stream length.");
            }

            objectSizeUpdatesByControl.TryGetValue(addition.Control.Name, out var newObjectSize);
            var sitePayload = SerializeFormSite(addition.Control, formStream, original.AsSpan(addition.Start, addition.Size), addition.Start, newObjectSize);
            totalDelta += sitePayload.Length;
            output.Write(sitePayload, 0, sitePayload.Length);
        }

        if (cursor < original.Length)
        {
            output.Write(original, cursor, original.Length - cursor);
        }

        var rebuilt = output.ToArray();
        var fullDelta = totalDelta + depthDelta;
        if (fullDelta != 0)
        {
            var referenceControl = slices.Count > 0 ? slices[0].Control : removals.Count > 0 ? removals[0].Control : additions[0].Control;
            PatchSiteDataCountOfBytes(rebuilt, referenceControl, fullDelta, formStream.Path);
        }

        if (countDelta != 0)
        {
            var referenceControl = slices.Count > 0 ? slices[0].Control : removals.Count > 0 ? removals[0].Control : additions[0].Control;
            PatchSiteDataCountOfSites(rebuilt, referenceControl, countDelta, formStream.Path);
        }

        return rebuilt;
    }

    private static List<FormSiteSlice> BuildFormSiteSlices(StorageEntryDump formStream, IReadOnlyList<ControlInfo> controls)
    {
        var result = new List<FormSiteSlice>();
        foreach (var control in controls)
        {
            var props = control.Properties;
            if (props is null || IsAddedControl(props))
            {
                continue;
            }

            if (!TryGetInt(props, "siteLocalOffset", out var start) || !TryGetInt(props, "cbSite", out var cbSite) || cbSite <= 0)
            {
                continue;
            }

            if (!TryGetString(props, "siteParser", out var siteParser) || !siteParser.Equals("msOFormsOleSiteConcrete", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new FormSiteSlice(control, start, checked(cbSite + 4)));
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static List<FormSiteSlice> BuildRemovedFormSiteSlices(StorageEntryDump formStream, IReadOnlyList<ControlInfo> controls)
    {
        var result = new List<FormSiteSlice>();
        foreach (var control in controls)
        {
            var props = control.Properties;
            if (props is null)
            {
                continue;
            }

            if (!TryGetInt(props, "siteLocalOffset", out var start) || !TryGetInt(props, "cbSite", out var cbSite) || cbSite <= 0)
            {
                throw new CliException($"Cannot remove '{control.Name}' from form stream '{formStream.Path}': missing FormSiteData slice metadata.");
            }

            if (!TryGetString(props, "siteParser", out var siteParser) || !siteParser.Equals("msOFormsOleSiteConcrete", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Cannot remove '{control.Name}' from form stream '{formStream.Path}': removed control is not an OleSiteConcrete site.");
            }

            result.Add(new FormSiteSlice(control, start, checked(cbSite + 4)));
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static List<FormSiteSlice> BuildAddedFormSiteSlices(StorageEntryDump formStream, IReadOnlyList<ControlInfo> controls)
    {
        var result = new List<FormSiteSlice>();
        foreach (var control in controls)
        {
            var props = control.Properties;
            if (props is null || !IsAddedControl(props))
            {
                continue;
            }

            if (!TryGetInt(props, "siteLocalOffset", out var templateStart) || !TryGetInt(props, "cbSite", out var cbSite) || cbSite <= 0)
            {
                throw new CliException($"Cannot add '{control.Name}' to form stream '{formStream.Path}': template is missing FormSiteData slice metadata.");
            }

            if (!TryGetString(props, "siteParser", out var siteParser) || !siteParser.Equals("msOFormsOleSiteConcrete", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Cannot add '{control.Name}' to form stream '{formStream.Path}': template is not an OleSiteConcrete site.");
            }

            result.Add(new FormSiteSlice(control, templateStart, checked(cbSite + 4)));
        }

        return result;
    }

    private static int GetSiteDepthStart(ControlInfo firstControl, string path)
    {
        if (firstControl.Properties is null)
        {
            throw new CliException($"Cannot rebuild SiteDepthsAndTypes in '{path}': missing properties.");
        }

        if (TryGetInt(firstControl.Properties, "siteDataDepthsLocalOffset", out var depthsLocalOffset))
        {
            return depthsLocalOffset;
        }

        if (TryGetInt(firstControl.Properties, "siteDataCountOfSitesLocalOffset", out var countOfSitesLocalOffset))
        {
            return checked(countOfSitesLocalOffset + 8);
        }

        if (TryGetInt(firstControl.Properties, "siteDataLocalOffset", out var siteDataLocalOffset))
        {
            return checked(siteDataLocalOffset + 8);
        }

        throw new CliException($"Cannot rebuild SiteDepthsAndTypes in '{path}': missing siteDataDepthsLocalOffset/siteDataCountOfSitesLocalOffset/siteDataLocalOffset metadata.");
    }

    private static byte[] SerializeSiteDepthsAndTypes(IReadOnlyList<ControlInfo> controls, string path)
    {
        using var output = new MemoryStream(controls.Count * 2 + 3);
        foreach (var control in controls)
        {
            if (control.Properties is null ||
                !TryGetInt(control.Properties, "siteDepth", out var depth) ||
                !TryGetInt(control.Properties, "siteType", out var siteType))
            {
                throw new CliException($"Cannot rebuild SiteDepthsAndTypes in '{path}': missing siteDepth/siteType metadata for '{control.Name}'.");
            }

            if (depth is < 0 or > 255 || siteType is < 0 or > 127)
            {
                throw new CliException($"Cannot rebuild SiteDepthsAndTypes in '{path}': invalid depth/type for '{control.Name}'.");
            }

            output.WriteByte((byte)depth);
            output.WriteByte((byte)siteType);
        }

        while (output.Length % 4 != 0)
        {
            output.WriteByte(0);
        }

        return output.ToArray();
    }

    private static byte[] SerializeFormSite(
        ControlInfo control,
        StorageEntryDump formStream,
        ReadOnlySpan<byte> originalSite,
        int siteStartLocalOffset,
        int newObjectStreamSize)
    {
        var site = originalSite.ToArray();
        var props = control.Properties;
        if (props is null)
        {
            return site;
        }

        // Write fixed/scalar site fields first.  Variable-length site strings are rebuilt later in
        // reverse order, so shifted tail bytes carry these patched values to their final positions.
        if (control.Left is int left && control.LeftOffset is int leftFileOffset)
        {
            WriteInt32ByFileOffsetToSlice(formStream, site, siteStartLocalOffset, leftFileOffset, left, $"{control.Name}.left");
        }

        if (control.Top is int top && control.TopOffset is int topFileOffset)
        {
            WriteInt32ByFileOffsetToSlice(formStream, site, siteStartLocalOffset, topFileOffset, top, $"{control.Name}.top");
        }

        if (TryGetInt(props, "tabIndex", out var tabIndex) && TryGetInt(props, "tabIndexOffset", out var tabIndexFileOffset))
        {
            WriteUInt16ByFileOffsetToSlice(formStream, site, siteStartLocalOffset, tabIndexFileOffset, tabIndex, $"{control.Name}.tabIndex");
        }

        if (TryGetInt(props, "siteId", out var siteId) && TryGetInt(props, "siteIdOffset", out var siteIdFileOffset))
        {
            WriteUInt32ByFileOffsetToSlice(formStream, site, siteStartLocalOffset, siteIdFileOffset, siteId, $"{control.Name}.ID");
        }

        if (newObjectStreamSize > 0 && TryGetInt(props, "siteObjectStreamSizeOffset", out var objectSizeFileOffset))
        {
            WriteUInt32ByFileOffsetToSlice(formStream, site, siteStartLocalOffset, objectSizeFileOffset, newObjectStreamSize, $"{control.Name}.ObjectStreamSize");
        }

        var segments = BuildFormSiteStringSegments(control, props, siteStartLocalOffset);
        foreach (var segment in segments
            .OrderByDescending(segment => segment.DataLocalOffset)
            .ThenByDescending(segment => segment.CountLocalOffset))
        {
            site = NormalizeFormSiteStringSegment(control.Name, segment, site);
        }

        if (site.Length != originalSite.Length)
        {
            WriteUInt16At(site, 2, checked(site.Length - 4), $"{control.Name}.cbSite");
        }

        return site;
    }

    private static List<FormSiteStringSegment> BuildFormSiteStringSegments(ControlInfo control, Dictionary<string, object?> props, int siteStartLocalOffset)
    {
        var result = new List<FormSiteStringSegment>();

        void Add(string propertyName)
        {
            if (!props.TryGetValue(propertyName, out var rawValue) || rawValue is null)
            {
                return;
            }

            if (!TryGetStringSpan(props, propertyName, out var span) || span.CountLocalOffset is null)
            {
                return;
            }

            result.Add(new FormSiteStringSegment(
                propertyName,
                rawValue.ToString() ?? string.Empty,
                span.DataLocalOffset - siteStartLocalOffset,
                span.CountLocalOffset.Value - siteStartLocalOffset,
                span.ByteCount,
                span.PaddedByteCount,
                span.Compressed));
        }

        Add("name");
        Add("controlTipText");
        Add("tag");
        return result;
    }

    private static byte[] NormalizeFormSiteStringSegment(string controlName, FormSiteStringSegment segment, byte[] site)
    {
        if (segment.CountLocalOffset < 0 || segment.CountLocalOffset + 4 > site.Length)
        {
            throw new CliException($"Cannot rebuild {controlName}.{segment.PropertyName}: count offset {segment.CountLocalOffset} is outside the site payload.");
        }

        if (segment.DataLocalOffset < 0 || segment.DataLocalOffset > site.Length ||
            segment.ByteCount < 0 || segment.PaddedByteCount < 0 ||
            segment.DataLocalOffset + segment.PaddedByteCount > site.Length)
        {
            throw new CliException($"Cannot rebuild {controlName}.{segment.PropertyName}: string span exceeds the site payload.");
        }

        var encoded = segment.Compressed
            ? Encoding.Latin1.GetBytes(segment.Value)
            : Encoding.Unicode.GetBytes(segment.Value);

        if (!segment.Compressed && encoded.Length % 2 != 0)
        {
            throw new CliException($"Cannot rebuild {controlName}.{segment.PropertyName}: UTF-16 byte count must be even.");
        }

        var newPaddedByteCount = Align4(encoded.Length);
        var count = (uint)encoded.Length;
        if (segment.Compressed)
        {
            count |= 0x8000_0000u;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(site.AsSpan(segment.CountLocalOffset, 4), count);

        if (newPaddedByteCount == segment.PaddedByteCount)
        {
            site.AsSpan(segment.DataLocalOffset, segment.PaddedByteCount).Clear();
            encoded.CopyTo(site.AsSpan(segment.DataLocalOffset));
            return site;
        }

        using var output = new MemoryStream(site.Length + newPaddedByteCount - segment.PaddedByteCount);
        output.Write(site, 0, segment.DataLocalOffset);
        output.Write(encoded, 0, encoded.Length);
        if (newPaddedByteCount > encoded.Length)
        {
            output.Write(new byte[newPaddedByteCount - encoded.Length]);
        }

        var oldTailStart = segment.DataLocalOffset + segment.PaddedByteCount;
        output.Write(site, oldTailStart, site.Length - oldTailStart);
        return output.ToArray();
    }

    private static void PatchSiteDataCountOfBytes(byte[] formStream, ControlInfo control, int delta, string path)
    {
        if (control.Properties is null)
        {
            throw new CliException($"Cannot update FormSiteData.CountOfBytes in '{path}': missing properties.");
        }

        var countOfBytesOffset = GetSiteDataCountOfBytesLocalOffset(control, path);
        if (countOfBytesOffset < 0 || countOfBytesOffset + 4 > formStream.Length)
        {
            throw new CliException($"Cannot update FormSiteData.CountOfBytes in '{path}': offset {countOfBytesOffset} is outside the stream.");
        }

        var current = BinaryPrimitives.ReadUInt32LittleEndian(formStream.AsSpan(countOfBytesOffset, 4));
        var next = checked((int)current + delta);
        if (next < 0)
        {
            throw new CliException($"Cannot update FormSiteData.CountOfBytes in '{path}': result would be negative.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(formStream.AsSpan(countOfBytesOffset, 4), unchecked((uint)next));
    }

    private static void PatchSiteDataCountOfSites(byte[] formStream, ControlInfo control, int addedCount, string path)
    {
        if (control.Properties is null)
        {
            throw new CliException($"Cannot update FormSiteData.CountOfSites in '{path}': missing properties.");
        }

        var countOfSitesOffset = GetSiteDataCountOfSitesLocalOffset(control, path);
        if (countOfSitesOffset < 0 || countOfSitesOffset + 4 > formStream.Length)
        {
            throw new CliException($"Cannot update FormSiteData.CountOfSites in '{path}': offset {countOfSitesOffset} is outside the stream.");
        }

        var current = BinaryPrimitives.ReadUInt32LittleEndian(formStream.AsSpan(countOfSitesOffset, 4));
        var next = checked((int)current + addedCount);
        if (next < 0)
        {
            throw new CliException($"Cannot update FormSiteData.CountOfSites in '{path}': result would be negative.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(formStream.AsSpan(countOfSitesOffset, 4), unchecked((uint)next));
    }

    private static int GetSiteDataCountOfSitesLocalOffset(ControlInfo control, string path)
    {
        if (control.Properties is not null && TryGetInt(control.Properties, "siteDataCountOfSitesLocalOffset", out var localOffset))
        {
            return localOffset;
        }

        if (control.Properties is not null && TryGetInt(control.Properties, "siteDataLocalOffset", out var siteDataLocalOffset))
        {
            // Backward-compatible fallback for older raw metadata.
            // formSiteDataNoClassCount points directly to CountOfSites; formSiteData includes
            // the 2-byte CountOfSiteClassInfo before CountOfSites when no ClassTable follows.
            return IsFormSiteDataWithClassInfo(control)
                ? checked(siteDataLocalOffset + 2)
                : siteDataLocalOffset;
        }

        throw new CliException($"Cannot update FormSiteData.CountOfSites in '{path}': missing siteDataCountOfSitesLocalOffset/siteDataLocalOffset metadata.");
    }

    private static int GetSiteDataCountOfBytesLocalOffset(ControlInfo control, string path)
    {
        if (control.Properties is not null && TryGetInt(control.Properties, "siteDataCountOfBytesLocalOffset", out var localOffset))
        {
            return localOffset;
        }

        if (control.Properties is not null && TryGetInt(control.Properties, "siteDataCountOfSitesLocalOffset", out var countOfSitesLocalOffset))
        {
            return checked(countOfSitesLocalOffset + 4);
        }

        if (control.Properties is not null && TryGetInt(control.Properties, "siteDataLocalOffset", out var siteDataLocalOffset))
        {
            // Backward-compatible fallback for older raw metadata.
            return IsFormSiteDataWithClassInfo(control)
                ? checked(siteDataLocalOffset + 6)
                : checked(siteDataLocalOffset + 4);
        }

        throw new CliException($"Cannot update FormSiteData.CountOfBytes in '{path}': missing siteDataCountOfBytesLocalOffset/siteDataCountOfSitesLocalOffset/siteDataLocalOffset metadata.");
    }

    private static bool IsFormSiteDataWithClassInfo(ControlInfo control)
    {
        return control.Properties is not null &&
               TryGetString(control.Properties, "siteDataParser", out var parser) &&
               parser.Equals("formSiteData", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteInt32ByFileOffsetToSlice(StorageEntryDump stream, byte[] site, int siteStartLocalOffset, int fileOffset, int value, string context)
    {
        if (!TryMapFileOffsetToLocal(stream.FileOffsets, fileOffset, out var streamLocalOffset))
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': file offset {fileOffset} is not part of the form stream.");
        }

        var localOffset = streamLocalOffset - siteStartLocalOffset;
        if (localOffset < 0 || localOffset + 4 > site.Length)
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': local offset {localOffset} is outside the site payload.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(site.AsSpan(localOffset, 4), value);
    }

    private static void WriteUInt16ByFileOffsetToSlice(StorageEntryDump stream, byte[] site, int siteStartLocalOffset, int fileOffset, int value, string context)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new CliException($"Cannot patch {context}: value {value} is outside UInt16 range.");
        }

        if (!TryMapFileOffsetToLocal(stream.FileOffsets, fileOffset, out var streamLocalOffset))
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': file offset {fileOffset} is not part of the form stream.");
        }

        var localOffset = streamLocalOffset - siteStartLocalOffset;
        if (localOffset < 0 || localOffset + 2 > site.Length)
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': local offset {localOffset} is outside the site payload.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(site.AsSpan(localOffset, 2), unchecked((ushort)value));
    }

    private static void WriteUInt32ByFileOffsetToSlice(StorageEntryDump stream, byte[] site, int siteStartLocalOffset, int fileOffset, int value, string context)
    {
        if (value < 0)
        {
            throw new CliException($"Cannot patch {context}: value {value} is negative.");
        }

        if (!TryMapFileOffsetToLocal(stream.FileOffsets, fileOffset, out var streamLocalOffset))
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': file offset {fileOffset} is not part of the form stream.");
        }

        var localOffset = streamLocalOffset - siteStartLocalOffset;
        if (localOffset < 0 || localOffset + 4 > site.Length)
        {
            throw new CliException($"Cannot patch {context} in '{stream.Path}': local offset {localOffset} is outside the site payload.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(site.AsSpan(localOffset, 4), unchecked((uint)value));
    }

    private static void WriteUInt16At(byte[] data, int localOffset, int value, string context)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new CliException($"Cannot patch {context}: value {value} is outside UInt16 range.");
        }

        if (localOffset < 0 || localOffset + 2 > data.Length)
        {
            throw new CliException($"Cannot patch {context}: offset {localOffset} is outside the payload.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(localOffset, 2), unchecked((ushort)value));
    }

    private static int Align4(int value) => (value + 3) & ~3;

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


    private static bool IsAddedControl(Dictionary<string, object?> props) =>
        TryGetBool(props, "isAddedControl", out var isAdded) && isAdded;

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
            if (props is null || IsAddedControl(props))
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

    private static Dictionary<string, List<ObjectStreamSlice>> BuildAdditionsByObjectStreamPath(LayoutInspection layout)
    {
        var result = new Dictionary<string, List<ObjectStreamSlice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in layout.Controls)
        {
            var props = control.Properties;
            if (props is null || !IsAddedControl(props))
            {
                continue;
            }

            if (!TryGetString(props, "storagePath", out var storagePath) || string.IsNullOrWhiteSpace(storagePath) ||
                !TryGetInt(props, "objectStreamLocalOffset", out var templateStart) ||
                !TryGetInt(props, "objectStreamSize", out var templateSize) || templateSize <= 0)
            {
                throw new CliException($"Cannot add '{control.Name}': template is missing object stream slice metadata.");
            }

            var objectStreamPath = $"{storagePath}/o";
            if (!result.TryGetValue(objectStreamPath, out var additions))
            {
                additions = [];
                result[objectStreamPath] = additions;
            }

            additions.Add(new ObjectStreamSlice(control, templateStart, templateSize));
        }

        return result;
    }

    private static Dictionary<string, List<ObjectStreamSlice>> BuildRemovedByObjectStreamPath(LayoutInspection layout)
    {
        var result = new Dictionary<string, List<ObjectStreamSlice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in layout.RemovedControls ?? [])
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
        IReadOnlyList<ObjectStreamSlice> additions,
        IReadOnlyList<ObjectStreamSlice> removals,
        ObjectStreamRewriteMode mode,
        List<ObjectStreamSizeUpdate> sizeUpdates)
    {
        using var output = new MemoryStream(original.Length);
        var cursor = 0;
        var originalSegments = slices
            .Select(slice => new ObjectStreamRewriteSlice(slice.Control, slice.Start, slice.Size, IsRemoval: false))
            .Concat(removals.Select(slice => new ObjectStreamRewriteSlice(slice.Control, slice.Start, slice.Size, IsRemoval: true)))
            .OrderBy(slice => slice.Start)
            .ToList();

        foreach (var slice in originalSegments)
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

            if (slice.IsRemoval)
            {
                cursor = slice.Start + slice.Size;
                continue;
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

        foreach (var addition in additions)
        {
            if (addition.Start < 0 || addition.Start + addition.Size > original.Length)
            {
                throw new CliException($"Cannot add '{addition.Control.Name}' to object stream '{path}': template slice exceeds stream length.");
            }

            var originalPayload = original.AsSpan(addition.Start, addition.Size);
            var payload = mode switch
            {
                ObjectStreamRewriteMode.ActiveSerializeFixed => ObjectPayloadSerializer.SerializeFixedLength(addition.Control, originalPayload),
                ObjectStreamRewriteMode.NormalizeStrings => ObjectPayloadSerializer.SerializeNormalizedStrings(addition.Control, originalPayload),
                ObjectStreamRewriteMode.PatchProperties or ObjectStreamRewriteMode.FormAndObjectPatch => ObjectPayloadSerializer.SerializePatchedProperties(addition.Control, originalPayload),
                _ => originalPayload.ToArray()
            };

            AddSizeUpdate(addition.Control, payload.Length, sizeUpdates);
            output.Write(payload, 0, payload.Length);
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


    private static bool TryGetStringSpan(Dictionary<string, object?> props, string propertyName, out FormStringSpanInfo span)
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

    private static bool TryGetStringSpan(Dictionary<string, object?> dict, out FormStringSpanInfo span)
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

        span = new FormStringSpanInfo(dataLocalOffset, byteCount, paddedByteCount, compressed, countLocalOffset);
        return true;
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

    private readonly record struct FormStringSpanInfo(
        int DataLocalOffset,
        int ByteCount,
        int PaddedByteCount,
        bool Compressed,
        int? CountLocalOffset);

    private sealed record FormSiteStringSegment(
        string PropertyName,
        string Value,
        int DataLocalOffset,
        int CountLocalOffset,
        int ByteCount,
        int PaddedByteCount,
        bool Compressed);

    private sealed record FormSiteSlice(ControlInfo Control, int Start, int Size);
    private sealed record FormSiteRewriteSlice(ControlInfo Control, int Start, int Size, bool IsRemoval);

    private sealed record ObjectStreamSlice(ControlInfo Control, int Start, int Size);
    private sealed record ObjectStreamRewriteSlice(ControlInfo Control, int Start, int Size, bool IsRemoval);
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
