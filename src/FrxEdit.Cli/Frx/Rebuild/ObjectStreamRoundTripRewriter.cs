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
        var streamsByPath = dump.Streams
            .Where(stream => !string.IsNullOrWhiteSpace(stream.Path))
            .GroupBy(stream => stream.Path!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
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

            if (mode == ObjectStreamRewriteMode.FormAndObjectPatch &&
                TryRewriteMultiPageInnerTabStripStream(stream, layout, sizeUpdates, out var rewrittenTabStripStream))
            {
                rewrittenObjectStreams[stream.Path] = rewrittenTabStripStream;
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

            var rewritten = RewriteObjectStream(stream.Path, stream.Data, streamsByPath, slices ?? [], additions ?? [], removals ?? [], mode, sizeUpdates);
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
                    patched = RewriteFormSiteData(stream with { Data = patched }, streamsByPath, controls ?? [], updates ?? [], removedControls ?? []);
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

            if (mode == ObjectStreamRewriteMode.FormAndObjectPatch &&
                !string.IsNullOrWhiteSpace(stream.Path) &&
                stream.Kind.Equals("Stream", StringComparison.OrdinalIgnoreCase) &&
                stream.Name.Equals("x", StringComparison.OrdinalIgnoreCase) &&
                TryRewriteMultiPageXStream(stream, layout, out var rewrittenXStream))
            {
                return WithNewData(stream, rewrittenXStream);
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


    private static bool TryRewriteMultiPageXStream(StorageEntryDump xStream, LayoutInspection layout, out byte[] rewritten)
    {
        rewritten = [];
        if (string.IsNullOrWhiteSpace(xStream.Path))
        {
            return false;
        }

        var multiPage = layout.Controls.FirstOrDefault(control =>
            control.Type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase) &&
            control.Properties is not null &&
            TryGetString(control.Properties, "multiPageXStreamPath", out var path) &&
            path.Equals(xStream.Path, StringComparison.OrdinalIgnoreCase));

        if (multiPage?.Properties is null)
        {
            return false;
        }

        if (!TryGetInt(multiPage.Properties, "multiPagePageCount", out var originalPageCount))
        {
            return false;
        }

        var pages = layout.Controls
            .Where(control => control.Type.Equals("Page", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(control.Parent, multiPage.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(control => control.Properties is not null && TryGetInt(control.Properties, "multiPagePageIndex", out var index) ? index : int.MaxValue)
            .ThenBy(control => control.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pages.Count == originalPageCount)
        {
            // No page add/remove happened for this MultiPage. Keep the stream byte-for-byte as-is.
            return false;
        }

        if (pages.Count <= 0)
        {
            throw new CliException($"Cannot rebuild MultiPage x stream '{xStream.Path}': a MultiPage must keep at least one page.");
        }

        var data = xStream.Data;
        var ignoredPageProperties = TryGetFirstPagePropertiesSlice(multiPage, pages, data);
        var multiPageProperties = GetMultiPagePropertiesSlice(multiPage, data);
        using var output = new MemoryStream(data.Length);
        output.Write(data, ignoredPageProperties.Start, ignoredPageProperties.Length);

        foreach (var page in pages)
        {
            var pageSlice = GetPagePropertiesSlice(page, data);
            output.Write(data, pageSlice.Start, pageSlice.Length);
        }

        var multiPageBytes = data.AsSpan(multiPageProperties.Start, multiPageProperties.Length).ToArray();
        if (TryGetInt(multiPage.Properties, "multiPagePageCountLocalOffset", out var pageCountLocalOffset))
        {
            var relative = pageCountLocalOffset - multiPageProperties.Start;
            if (relative < 0 || relative + 4 > multiPageBytes.Length)
            {
                throw new CliException($"Cannot rebuild MultiPage x stream '{xStream.Path}': page count offset is outside MultiPageProperties.");
            }

            BinaryPrimitives.WriteInt32LittleEndian(multiPageBytes.AsSpan(relative, 4), pages.Count);
        }
        else
        {
            throw new CliException($"Cannot rebuild MultiPage x stream '{xStream.Path}': missing multiPagePageCountLocalOffset metadata.");
        }

        output.Write(multiPageBytes, 0, multiPageBytes.Length);

        foreach (var page in pages)
        {
            var pageId = GetPageId(page);
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, pageId);
            output.Write(buffer);
        }

        rewritten = output.ToArray();
        return true;
    }

    private static bool TryRewriteMultiPageInnerTabStripStream(
        StorageEntryDump oStream,
        LayoutInspection layout,
        List<ObjectStreamSizeUpdate> sizeUpdates,
        out byte[] rewritten)
    {
        rewritten = [];
        if (string.IsNullOrWhiteSpace(oStream.Path)) return false;

        var multiPage = layout.Controls.FirstOrDefault(control =>
            control.Type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase) &&
            control.Properties is not null &&
            TryGetString(control.Properties, "multiPageXStreamPath", out var xStreamPath) &&
            xStreamPath.EndsWith("/x", StringComparison.OrdinalIgnoreCase) &&
            (xStreamPath.Substring(0, xStreamPath.Length - 2) + "/o").Equals(oStream.Path, StringComparison.OrdinalIgnoreCase));

        if (multiPage?.Properties is null) return false;
        if (!TryGetInt(multiPage.Properties, "multiPagePageCount", out var originalPageCount)) return false;

        var pages = layout.Controls
            .Where(control => control.Type.Equals("Page", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(control.Parent, multiPage.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(control => control.Properties is not null && TryGetInt(control.Properties, "multiPagePageIndex", out var index) ? index : int.MaxValue)
            .ThenBy(control => control.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pages.Count == originalPageCount) return false;

        var parsed = ObjectStreamParser.TryReadTabStrip(oStream);
        if (parsed == null)
            throw new CliException($"Cannot rewrite MultiPage TabStrip '{oStream.Path}': parser failed.");

        var props = parsed.Properties;

        var captionsBytes = SerializeArrayString(pages, props, "tabCaptions", p => p.Name);
        var tooltipsBytes = SerializeArrayString(pages, props, "tabTooltips", p => "");
        var namesBytes = SerializeArrayString(pages, props, "tabNames", p => p.Name);
        var tagsBytes = SerializeArrayString(pages, props, "tabTags", p => "");
        var acceleratorsBytes = SerializeArrayString(pages, props, "tabAccelerators", p => "");

        var extraDataBytes = new MemoryStream();
        if (TryGetString(props, "sizeSource", out var sizeSource) && sizeSource == "tabStripExtraDataBlock")
        {
            if (!TryGetInt(props, "tabStripDataBlockEndLocalOffset", out var dataBlockEnd))
                throw new CliException("Missing tabStripDataBlockEndLocalOffset");
            extraDataBytes.Write(oStream.Data, dataBlockEnd, 8);
        }
        extraDataBytes.Write(captionsBytes);
        extraDataBytes.Write(tooltipsBytes);
        extraDataBytes.Write(namesBytes);
        extraDataBytes.Write(tagsBytes);
        extraDataBytes.Write(acceleratorsBytes);

        var dataBlock = oStream.Data.AsSpan(0, (int)props["tabStripDataBlockEndLocalOffset"]).ToArray();

        if (TryGetInt(props, "itemsSizeLocalOffset", out var itemsSizeOffset)) BinaryPrimitives.WriteUInt32LittleEndian(dataBlock.AsSpan(itemsSizeOffset, 4), (uint)captionsBytes.Length);
        if (TryGetInt(props, "tipStringsSizeLocalOffset", out var tipStringsSizeOffset)) BinaryPrimitives.WriteUInt32LittleEndian(dataBlock.AsSpan(tipStringsSizeOffset, 4), (uint)tooltipsBytes.Length);
        if (TryGetInt(props, "namesSizeLocalOffset", out var namesSizeOffset)) BinaryPrimitives.WriteUInt32LittleEndian(dataBlock.AsSpan(namesSizeOffset, 4), (uint)namesBytes.Length);
        if (TryGetInt(props, "tagsSizeLocalOffset", out var tagsSizeOffset)) BinaryPrimitives.WriteUInt32LittleEndian(dataBlock.AsSpan(tagsSizeOffset, 4), (uint)tagsBytes.Length);
        if (TryGetInt(props, "acceleratorsSizeLocalOffset", out var acceleratorsSizeOffset)) BinaryPrimitives.WriteUInt32LittleEndian(dataBlock.AsSpan(acceleratorsSizeOffset, 4), (uint)acceleratorsBytes.Length);

        if (TryGetInt(props, "tabsAllocatedLocalOffset", out var tabsAllocatedOffset)) BinaryPrimitives.WriteInt32LittleEndian(dataBlock.AsSpan(tabsAllocatedOffset, 4), pages.Count);
        if (TryGetInt(props, "tabDataLocalOffset", out var tabDataOffset)) BinaryPrimitives.WriteInt32LittleEndian(dataBlock.AsSpan(tabDataOffset, 4), pages.Count);

        var cbTabStrip = dataBlock.Length - 4 + extraDataBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(dataBlock.AsSpan(2, 2), (ushort)cbTabStrip);

        var newStream = new MemoryStream();
        newStream.Write(dataBlock);
        extraDataBytes.Position = 0;
        extraDataBytes.CopyTo(newStream);

        if (TryGetInt(props, "tabStripStreamDataLocalOffset", out var streamDataStart) &&
            TryGetInt(props, "tabStripStreamDataEndLocalOffset", out var streamDataEnd))
        {
            newStream.Write(oStream.Data, streamDataStart, streamDataEnd - streamDataStart);
        }

        if (TryGetInt(props, "textPropsExpectedLocalOffset", out var textPropsStart) &&
            TryGetInt(props, "textPropsEndLocalOffset", out var textPropsEnd))
        {
            newStream.Write(oStream.Data, textPropsStart, textPropsEnd - textPropsStart);
        }

        var defaultFlag = 3u;
        if (TryGetObjectList(props, "tabFlags", out var originalFlags) && originalFlags.Count > 0 &&
            TryGetInt(originalFlags[0], "raw", out var firstRaw))
        {
            defaultFlag = (uint)firstRaw;
        }

        foreach (var page in pages)
        {
            var flag = defaultFlag;
            if (page.Properties is not null && TryGetInt(page.Properties, "multiPagePageIndex", out var originalIdx))
            {
                if (TryGetObjectList(props, "tabFlags", out var flags) && originalIdx < flags.Count)
                {
                    if (TryGetInt(flags[originalIdx], "raw", out var rawFlag))
                        flag = (uint)rawFlag;
                }
            }
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, flag);
            newStream.Write(buffer);
        }

        rewritten = newStream.ToArray();

        var multiPageStoragePath = oStream.Path.Substring(0, oStream.Path.Length - 2);
        var fPath = $"{multiPageStoragePath}/f";
        sizeUpdates.Add(new ObjectStreamSizeUpdate("__internal_site_0_TabStrip", fPath, 0, rewritten.Length));

        return true;
    }

    private static byte[] SerializeArrayString(IReadOnlyList<ControlInfo> pages, Dictionary<string, object?> tabStripProps, string propertyName, Func<ControlInfo, string> fallback)
    {
        var entries = new List<Dictionary<string, object?>>();
        if (TryGetObjectList(tabStripProps, propertyName + "Entries", out var originalEntries))
        {
            entries = originalEntries;
        }

        var stream = new MemoryStream();
        if (entries.Count == 0) return stream.ToArray();

        foreach (var page in pages)
        {
            string value = fallback(page);
            bool compressed = true;

            if (page.Properties is not null && TryGetInt(page.Properties, "multiPagePageIndex", out var idx) && idx >= 0 && idx < entries.Count)
            {
                if (TryGetString(entries[idx], "value", out var originalValue)) value = originalValue;
                if (TryGetBool(entries[idx], "compressed", out var originalComp)) compressed = originalComp;
            }

            var encoded = compressed ? Encoding.Latin1.GetBytes(value) : Encoding.Unicode.GetBytes(value);
            var byteCount = encoded.Length;
            var padded = Align4(byteCount);

            var rawCount = (uint)byteCount;
            if (compressed) rawCount |= 0x8000_0000u;

            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, rawCount);
            stream.Write(header);
            stream.Write(encoded);
            if (padded > byteCount)
            {
                stream.Write(new byte[padded - byteCount]);
            }
        }
        return stream.ToArray();
    }

    private static (int Start, int Length) TryGetFirstPagePropertiesSlice(ControlInfo multiPage, IReadOnlyList<ControlInfo> pages, byte[] xStream)
    {
        if (pages.Count > 0 && pages[0].Properties is not null && TryGetInt(pages[0].Properties, "pagePropertiesLocalOffset", out var firstPageOffset))
        {
            if (firstPageOffset <= 0 || firstPageOffset > xStream.Length)
            {
                throw new CliException($"Cannot rebuild MultiPage x stream for '{multiPage.Name}': invalid first page properties offset.");
            }

            return (0, firstPageOffset);
        }

        if (multiPage.Properties is not null && TryGetObjectList(multiPage.Properties, "pageProperties", out var pageProperties) && pageProperties.Count > 0)
        {
            var first = pageProperties[0];
            if (TryGetInt(first, "localOffset", out var offset) && TryGetInt(first, "cbPage", out var cbPage))
            {
                return CheckedSlice(offset, checked(4 + cbPage), xStream.Length, $"{multiPage.Name}.PageProperties[0]");
            }
        }

        throw new CliException($"Cannot rebuild MultiPage x stream for '{multiPage.Name}': missing ignored PageProperties[0] metadata.");
    }

    private static (int Start, int Length) GetPagePropertiesSlice(ControlInfo page, byte[] xStream)
    {
        if (page.Properties is null || !TryGetInt(page.Properties, "pagePropertiesLocalOffset", out var offset) || !TryGetInt(page.Properties, "pagePropertiesCbPage", out var cbPage))
        {
            throw new CliException($"Cannot rebuild MultiPage x stream: page '{page.Name}' is missing PageProperties metadata.");
        }

        return CheckedSlice(offset, checked(4 + cbPage), xStream.Length, $"{page.Name}.PageProperties");
    }

    private static (int Start, int Length) GetMultiPagePropertiesSlice(ControlInfo multiPage, byte[] xStream)
    {
        if (multiPage.Properties is null || !TryGetInt(multiPage.Properties, "xStreamLocalOffset", out var offset) || !TryGetInt(multiPage.Properties, "cbMultiPageControlProperties", out var cb))
        {
            throw new CliException($"Cannot rebuild MultiPage x stream: MultiPage '{multiPage.Name}' is missing MultiPageProperties metadata.");
        }

        return CheckedSlice(offset, checked(4 + cb), xStream.Length, $"{multiPage.Name}.MultiPageProperties");
    }

    private static (int Start, int Length) CheckedSlice(int offset, int length, int totalLength, string description)
    {
        if (offset < 0 || length < 0 || offset + length > totalLength)
        {
            throw new CliException($"Cannot rebuild {description}: slice {offset}+{length} exceeds x stream length {totalLength}.");
        }

        return (offset, length);
    }

    private static uint GetPageId(ControlInfo page)
    {
        if (page.Properties is null)
        {
            throw new CliException($"Cannot rebuild MultiPage x stream: page '{page.Name}' has no properties.");
        }

        if (TryGetInt(page.Properties, "multiPagePageId", out var pageId) || TryGetInt(page.Properties, "siteId", out pageId) || TryGetInt(page.Properties, "id", out pageId))
        {
            if (pageId < 0)
            {
                throw new CliException($"Cannot rebuild MultiPage x stream: page '{page.Name}' has negative page ID {pageId}.");
            }

            return (uint)pageId;
        }

        throw new CliException($"Cannot rebuild MultiPage x stream: page '{page.Name}' is missing page ID metadata.");
    }

    private static bool TryGetObjectList(Dictionary<string, object?> properties, string name, out List<Dictionary<string, object?>> values)
    {
        values = [];
        if (!properties.TryGetValue(name, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is IEnumerable<Dictionary<string, object?>> dictionaries)
        {
            values = dictionaries.ToList();
            return values.Count > 0;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    dictionary[property.Name] = property.Value;
                }

                values.Add(dictionary);
            }

            return values.Count > 0;
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
        IReadOnlyDictionary<string, StorageEntryDump> streamsByPath,
        IReadOnlyList<ControlInfo> controls,
        IReadOnlyList<ObjectStreamSizeUpdate> sizeUpdates,
        IReadOnlyList<ControlInfo> removedControls)
    {
        var original = formStream.Data;
        var slices = BuildFormSiteSlices(formStream, controls);
        var additions = BuildAddedFormSiteSlices(formStream, controls);
        var removals = BuildRemovedFormSiteSlices(formStream, removedControls);
        var allOriginalSlices = BuildOriginalFormSiteRewriteSlices(formStream, slices, removals);
        if (allOriginalSlices.Count == 0 && additions.Count == 0)
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
            var rebuiltDepths = SerializeSiteDepthsAndTypes(allOriginalSlices
                .Where(slice => !slice.IsRemoval)
                .Select(slice => slice.Control)
                .Concat(additions.Select(a => a.Control))
                .ToList(), formStream.Path);
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
            var sourceFormStream = ResolveSourceStream(streamsByPath, slice.SourceStreamPath, formStream.Path, $"site slice for '{slice.Control.Name}'");
            var sourceSiteData = sourceFormStream.Data.AsSpan(slice.Start, slice.Size);
            var sitePayload = SerializeFormSite(slice.Control, sourceFormStream, sourceSiteData, slice.Start, newObjectSize);
            totalDelta += sitePayload.Length - slice.Size;
            output.Write(sitePayload, 0, sitePayload.Length);
            cursor = slice.Start + slice.Size;
        }

        foreach (var addition in additions)
        {
            byte[] sitePayload;
            if (addition.Control.Properties is not null &&
                TryGetByteArray(addition.Control.Properties, "generatedFormSitePayload", out var generatedSitePayload))
            {
                sitePayload = generatedSitePayload;
            }
            else
            {
                var sourceFormStream = ResolveSourceStream(streamsByPath, addition.SourceStreamPath, formStream.Path, $"added site template for '{addition.Control.Name}'");
                if (addition.Start < 0 || addition.Start + addition.Size > sourceFormStream.Data.Length)
                {
                    throw new CliException($"Cannot add '{addition.Control.Name}' to form stream '{formStream.Path}': template site slice exceeds source stream '{sourceFormStream.Path}' length.");
                }

                objectSizeUpdatesByControl.TryGetValue(addition.Control.Name, out var newObjectSize);
                sitePayload = SerializeFormSite(addition.Control, sourceFormStream, sourceFormStream.Data.AsSpan(addition.Start, addition.Size), addition.Start, newObjectSize);
            }

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
            var referenceControl = allOriginalSlices.Count > 0 ? allOriginalSlices[0].Control : additions[0].Control;
            PatchSiteDataCountOfBytes(rebuilt, referenceControl, fullDelta, formStream.Path);
        }

        if (countDelta != 0)
        {
            var referenceControl = allOriginalSlices.Count > 0 ? allOriginalSlices[0].Control : additions[0].Control;
            PatchSiteDataCountOfSites(rebuilt, referenceControl, countDelta, formStream.Path);
        }

        return rebuilt;
    }


    private static List<FormSiteRewriteSlice> BuildOriginalFormSiteRewriteSlices(
        StorageEntryDump formStream,
        IReadOnlyList<FormSiteSlice> keptSlices,
        IReadOnlyList<FormSiteSlice> removedSlices)
    {
        var keptByStart = keptSlices
            .GroupBy(slice => slice.Start)
            .ToDictionary(group => group.Key, group => group.First());
        var removedByStart = removedSlices
            .GroupBy(slice => slice.Start)
            .ToDictionary(group => group.Key, group => group.First());

        var parsedSites = StructuredMsFormsParser.Parse(formStream);
        if (parsedSites.Count == 0)
        {
            return keptSlices
                .Select(slice => new FormSiteRewriteSlice(slice.Control, slice.Start, slice.Size, slice.SourceStreamPath, IsRemoval: false))
                .Concat(removedSlices.Select(slice => new FormSiteRewriteSlice(slice.Control, slice.Start, slice.Size, slice.SourceStreamPath, IsRemoval: true)))
                .OrderBy(slice => slice.Start)
                .ToList();
        }

        var result = new List<FormSiteRewriteSlice>();
        var matchedKept = new HashSet<int>();
        var matchedRemoved = new HashSet<int>();

        foreach (var site in parsedSites.OrderBy(site => site.StreamStart))
        {
            if (removedByStart.TryGetValue(site.StreamStart, out var removed))
            {
                result.Add(new FormSiteRewriteSlice(removed.Control, removed.Start, removed.Size, removed.SourceStreamPath, IsRemoval: true));
                matchedRemoved.Add(site.StreamStart);
                continue;
            }

            if (keptByStart.TryGetValue(site.StreamStart, out var kept))
            {
                result.Add(new FormSiteRewriteSlice(kept.Control, kept.Start, kept.Size, kept.SourceStreamPath, IsRemoval: false));
                matchedKept.Add(site.StreamStart);
                continue;
            }

            var preserved = CreatePreservedSiteControl(site, formStream);
            result.Add(new FormSiteRewriteSlice(preserved, site.StreamStart, checked(site.StreamEnd - site.StreamStart), formStream.Path ?? string.Empty, IsRemoval: false));
        }

        foreach (var slice in keptSlices)
        {
            if (!matchedKept.Contains(slice.Start))
            {
                throw new CliException($"Cannot rebuild form stream '{formStream.Path}': parsed FormSiteData did not contain kept site '{slice.Control.Name}' at local offset {slice.Start}.");
            }
        }

        foreach (var slice in removedSlices)
        {
            if (!matchedRemoved.Contains(slice.Start))
            {
                throw new CliException($"Cannot rebuild form stream '{formStream.Path}': parsed FormSiteData did not contain removed site '{slice.Control.Name}' at local offset {slice.Start}.");
            }
        }

        return result;
    }

    private static ControlInfo CreatePreservedSiteControl(SiteDescriptor site, StorageEntryDump formStream)
    {
        var props = new Dictionary<string, object?>(site.ExtraProperties, StringComparer.OrdinalIgnoreCase)
        {
            ["siteIndex"] = site.SiteIndex,
            ["siteDepth"] = (int)site.Depth,
            ["siteType"] = (int)site.SiteType,
            ["siteLocalOffset"] = site.StreamStart,
            ["cbSite"] = site.StreamEnd - site.StreamStart - 4,
            ["streamPath"] = formStream.Path,
            ["internalSite"] = site.IsInternalSite,
            ["preservedUnmappedSite"] = true
        };

        if (!string.IsNullOrWhiteSpace(site.Name))
        {
            props["name"] = site.Name;
            props["siteName"] = site.Name;
        }

        var name = !string.IsNullOrWhiteSpace(site.Name)
            ? site.Name!
            : $"__internal_site_{site.SiteIndex}_{site.ControlType ?? "Ole"}";
        var type = !string.IsNullOrWhiteSpace(site.ControlType)
            ? site.ControlType!
            : "OleSite";

        return new ControlInfo(
            name,
            type,
            site.Left,
            site.Top,
            null,
            null,
            null,
            null,
            null,
            null,
            props,
            null,
            null,
            site.SiteIndex,
            null,
            null,
            site.NameOffset,
            site.LeftOffset > 0 && formStream.FileOffsets.Length > site.LeftOffset ? formStream.FileOffsets[site.LeftOffset] : null,
            site.TopOffset > 0 && formStream.FileOffsets.Length > site.TopOffset ? formStream.FileOffsets[site.TopOffset] : null,
            null,
            null);
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

            if (!TryGetString(props, "streamPath", out var sourceStreamPath) || string.IsNullOrWhiteSpace(sourceStreamPath))
            {
                sourceStreamPath = formStream.Path;
            }

            result.Add(new FormSiteSlice(control, start, checked(cbSite + 4), sourceStreamPath));
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

            if (!TryGetString(props, "streamPath", out var sourceStreamPath) || string.IsNullOrWhiteSpace(sourceStreamPath))
            {
                sourceStreamPath = formStream.Path;
            }

            result.Add(new FormSiteSlice(control, start, checked(cbSite + 4), sourceStreamPath));
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

            if (TryGetByteArray(props, "generatedFormSitePayload", out var generatedSite))
            {
                result.Add(new FormSiteSlice(control, 0, generatedSite.Length, formStream.Path ?? string.Empty));
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

            if (!TryGetString(props, "templateStreamPath", out var sourceStreamPath) || string.IsNullOrWhiteSpace(sourceStreamPath))
            {
                if (!TryGetString(props, "streamPath", out sourceStreamPath) || string.IsNullOrWhiteSpace(sourceStreamPath))
                {
                    sourceStreamPath = formStream.Path;
                }
            }

            result.Add(new FormSiteSlice(control, templateStart, checked(cbSite + 4), sourceStreamPath));
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
        StorageEntryDump sourceFormStream,
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
            WriteInt32ByFileOffsetToSlice(sourceFormStream, site, siteStartLocalOffset, leftFileOffset, left, $"{control.Name}.left");
        }

        if (control.Top is int top && control.TopOffset is int topFileOffset)
        {
            WriteInt32ByFileOffsetToSlice(sourceFormStream, site, siteStartLocalOffset, topFileOffset, top, $"{control.Name}.top");
        }

        if (TryGetInt(props, "tabIndex", out var tabIndex) && TryGetInt(props, "tabIndexOffset", out var tabIndexFileOffset))
        {
            WriteUInt16ByFileOffsetToSlice(sourceFormStream, site, siteStartLocalOffset, tabIndexFileOffset, tabIndex, $"{control.Name}.tabIndex");
        }

        if (TryGetInt(props, "siteId", out var siteId) && TryGetInt(props, "siteIdOffset", out var siteIdFileOffset))
        {
            WriteUInt32ByFileOffsetToSlice(sourceFormStream, site, siteStartLocalOffset, siteIdFileOffset, siteId, $"{control.Name}.ID");
        }

        if (newObjectStreamSize > 0 && TryGetInt(props, "siteObjectStreamSizeOffset", out var objectSizeFileOffset))
        {
            WriteUInt32ByFileOffsetToSlice(sourceFormStream, site, siteStartLocalOffset, objectSizeFileOffset, newObjectStreamSize, $"{control.Name}.ObjectStreamSize");
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

            slices.Add(new ObjectStreamSlice(control, start, size, objectStreamPath));
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

            if (!TryGetString(props, "storagePath", out var storagePath) || string.IsNullOrWhiteSpace(storagePath))
            {
                throw new CliException($"Cannot add '{control.Name}': missing target storagePath metadata.");
            }

            if (!TryGetInt(props, "objectStreamLocalOffset", out var templateStart) ||
                !TryGetInt(props, "objectStreamSize", out var templateSize) || templateSize <= 0)
            {
                if (TryGetByteArray(props, "generatedObjectPayload", out var generatedObject))
                {
                    templateStart = 0;
                    templateSize = generatedObject.Length;
                }
                else
                {
                    throw new CliException($"Cannot add '{control.Name}': template is missing object stream slice metadata.");
                }
            }

            var objectStreamPath = $"{storagePath}/o";
            if (!result.TryGetValue(objectStreamPath, out var additions))
            {
                additions = [];
                result[objectStreamPath] = additions;
            }

            if (!TryGetString(props, "templateObjectStreamPath", out var sourceObjectStreamPath) || string.IsNullOrWhiteSpace(sourceObjectStreamPath))
            {
                sourceObjectStreamPath = objectStreamPath;
            }

            additions.Add(new ObjectStreamSlice(control, templateStart, templateSize, sourceObjectStreamPath));
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

            slices.Add(new ObjectStreamSlice(control, start, size, objectStreamPath));
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
        IReadOnlyDictionary<string, StorageEntryDump> streamsByPath,
        IReadOnlyList<ObjectStreamSlice> slices,
        IReadOnlyList<ObjectStreamSlice> additions,
        IReadOnlyList<ObjectStreamSlice> removals,
        ObjectStreamRewriteMode mode,
        List<ObjectStreamSizeUpdate> sizeUpdates)
    {
        using var output = new MemoryStream(original.Length);
        var cursor = 0;
        var originalSegments = slices
            .Select(slice => new ObjectStreamRewriteSlice(slice.Control, slice.Start, slice.Size, slice.SourceStreamPath, IsRemoval: false))
            .Concat(removals.Select(slice => new ObjectStreamRewriteSlice(slice.Control, slice.Start, slice.Size, slice.SourceStreamPath, IsRemoval: true)))
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

            var sourceObjectStream = ResolveSourceStream(streamsByPath, slice.SourceStreamPath, path, $"object slice for '{slice.Control.Name}'");
            if (slice.Start < 0 || slice.Start + slice.Size > sourceObjectStream.Data.Length)
            {
                throw new CliException($"Cannot rebuild object stream '{path}': slice for '{slice.Control.Name}' exceeds source stream '{sourceObjectStream.Path}' length.");
            }

            var originalPayload = sourceObjectStream.Data.AsSpan(slice.Start, slice.Size);
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
            byte[] payload;
            var isGeneratedPayload = false;
            if (addition.Control.Properties is not null &&
                TryGetByteArray(addition.Control.Properties, "generatedObjectPayload", out var generatedObjectPayload))
            {
                payload = generatedObjectPayload;
                isGeneratedPayload = true;
            }
            else
            {
                var sourceObjectStream = ResolveSourceStream(streamsByPath, addition.SourceStreamPath, path, $"added object template for '{addition.Control.Name}'");
                if (addition.Start < 0 || addition.Start + addition.Size > sourceObjectStream.Data.Length)
                {
                    throw new CliException($"Cannot add '{addition.Control.Name}' to object stream '{path}': template slice exceeds source stream '{sourceObjectStream.Path}' length.");
                }

                var originalPayload = sourceObjectStream.Data.AsSpan(addition.Start, addition.Size);
                payload = mode switch
                {
                    ObjectStreamRewriteMode.ActiveSerializeFixed => ObjectPayloadSerializer.SerializeFixedLength(addition.Control, originalPayload),
                    ObjectStreamRewriteMode.NormalizeStrings => ObjectPayloadSerializer.SerializeNormalizedStrings(addition.Control, originalPayload),
                    ObjectStreamRewriteMode.PatchProperties or ObjectStreamRewriteMode.FormAndObjectPatch => ObjectPayloadSerializer.SerializePatchedProperties(addition.Control, originalPayload),
                    _ => originalPayload.ToArray()
                };
            }

            if (!isGeneratedPayload)
            {
                AddSizeUpdate(addition.Control, payload.Length, sizeUpdates);
            }

            output.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static StorageEntryDump ResolveSourceStream(IReadOnlyDictionary<string, StorageEntryDump> streamsByPath, string? sourcePath, string fallbackPath, string context)
    {
        var path = string.IsNullOrWhiteSpace(sourcePath) ? fallbackPath : sourcePath;
        if (streamsByPath.TryGetValue(path, out var stream))
        {
            return stream;
        }

        throw new CliException($"Cannot resolve source stream '{path}' for {context}.");
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

    private static bool TryGetByteArray(Dictionary<string, object?> props, string key, out byte[] value)
    {
        value = [];
        if (!props.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is byte[] bytes)
        {
            value = bytes;
            return true;
        }

        return false;
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

    private sealed record FormSiteSlice(ControlInfo Control, int Start, int Size, string SourceStreamPath);
    private sealed record FormSiteRewriteSlice(ControlInfo Control, int Start, int Size, string SourceStreamPath, bool IsRemoval);

    private sealed record ObjectStreamSlice(ControlInfo Control, int Start, int Size, string SourceStreamPath);
    private sealed record ObjectStreamRewriteSlice(ControlInfo Control, int Start, int Size, string SourceStreamPath, bool IsRemoval);
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
