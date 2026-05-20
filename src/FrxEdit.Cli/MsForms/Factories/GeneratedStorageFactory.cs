internal static class GeneratedStorageFactory
{
    public static bool CanCreate(string type) =>
        type.Equals("Frame", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Page", StringComparison.OrdinalIgnoreCase);

    public static GeneratedStorageControlBytes CreateFrame(
        string name,
        int siteId,
        int tabIndex,
        int left,
        int top,
        int width,
        int height,
        string? caption,
        string storagePath)
    {
        var sitePayload = FormSiteFactory.BuildStorageOleSiteConcrete(
            name,
            siteId,
            tabIndex,
            0x0E,
            left,
            top,
            0x0004_0023);

        var frameCaption = string.IsNullOrWhiteSpace(caption) ? name : caption!;
        var fStream = BuildFrameFormStream(frameCaption, width, height, siteId);

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsFormSiteData",
            ["siteParser"] = "msOFormsOleSiteConcrete",
            ["siteBitFlags"] = "0x00040023",
            ["siteAutoSize"] = true,
            ["formControlParser"] = "msOFormsFormControl",
            ["formPropMask"] = "0x081A0C40",
            ["formCaption"] = frameCaption,
            ["sizeSource"] = "formControlDisplayedSize",
            ["displayedWidth"] = width,
            ["displayedHeight"] = height,
            ["logicalWidth"] = 0,
            ["logicalHeight"] = 0,
            ["generatedStoragePath"] = storagePath,
            ["generatedStorageF"] = fStream,
            ["generatedStorageO"] = Array.Empty<byte>(),
            ["generatedStorageCompObjKind"] = "Frame"
        };

        return new GeneratedStorageControlBytes(sitePayload, metadata);
    }

    private static byte[] BuildFrameFormStream(string caption, int width, int height, int siteId)
    {
        var captionBytes = Encoding.Latin1.GetBytes(caption);

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 0x0000_8004);
        dataBlock.WriteByte(3);
        MsFormsFactoryBinary.WritePadding(dataBlock, 4);
        MsFormsFactoryBinary.WriteCount(dataBlock, captionBytes.Length);
        MsFormsFactoryBinary.WriteUInt16(dataBlock, 0xFFFF);
        MsFormsFactoryBinary.WritePadding(dataBlock, 4);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 32_000);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, width, height);
        MsFormsFactoryBinary.WriteSize(extra, 0, 0);
        extra.Write(captionBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);

        using var formControl = new MemoryStream();
        formControl.WriteByte(0);
        formControl.WriteByte(4);
        MsFormsFactoryBinary.WriteUInt16(formControl, checked((ushort)(4 + dataBlock.Length + extra.Length)));
        MsFormsFactoryBinary.WriteUInt32(formControl, 0x081A_0C40);
        formControl.Write(dataBlock.ToArray());
        formControl.Write(extra.ToArray());
        formControl.Write(BuildDefaultFontStreamData());

        return formControl.ToArray();
    }

    private static byte[] BuildDefaultFontStreamData()
    {
        // Matches the minimal Frame FormControl font StreamData persisted by the native
        // designer for a default Tahoma frame.
        return Convert.FromHexString("0352E30B918FCE119DE300AA004BB85101000000900144420100065461686F6D610000000000000000");
    }

    public static GeneratedMultiPageControlBytes CreateMultiPage(
        string name,
        int multiPageId,
        int tabIndex,
        int left,
        int top,
        int width,
        int height,
        string storagePath,
        IReadOnlyList<string> pageNames,
        IReadOnlyList<string> pageCaptions)
    {
        if (pageNames.Count == 0 || pageNames.Count != pageCaptions.Count)
        {
            throw new CliException($"Cannot create MultiPage '{name}': pageNames and pageCaptions must have the same non-zero count.");
        }

        var tabStripId = multiPageId + 1;
        var pageIds = Enumerable.Range(multiPageId + 2, pageNames.Count).ToArray();
        var sitePayload = FormSiteFactory.BuildStorageOleSiteConcrete(name, multiPageId, tabIndex, 0x39, left, top, 0x0004_0023);
        var tabStripPayload = BuildInternalTabStripPayload(pageNames, pageCaptions, width, height);
        var pageSites = new List<byte[]>(pageNames.Count);
        var pages = new List<GeneratedPageControlBytes>(pageNames.Count);
        for (var i = 0; i < pageNames.Count; i++)
        {
            var pageId = pageIds[i];
            var pageName = pageNames[i];
            var pageStoragePath = $"{storagePath}/i{FormatStorageId(pageId)}";
            pageSites.Add(FormSiteFactory.BuildStorageOleSiteConcrete(
                pageName,
                pageId,
                i,
                0x07,
                0,
                0,
                i == 0 ? 0x0004_0021u : 0x0004_0023u));
            pages.Add(new GeneratedPageControlBytes(
                pageName,
                pageId,
                pageStoragePath,
                BuildPageFormStream(width, height, pageId),
                Array.Empty<byte>(),
                pageSites[^1],
                BuildDefaultPageProperties()));
        }

        var internalTabSite = BuildInternalObjectSite(tabStripId, 0x12, tabStripPayload.Length);

        var fStream = BuildMultiPageFormStream(width, height, multiPageId, [internalTabSite, .. pageSites]);
        var xStream = BuildMultiPageXStream(multiPageId, pageIds);
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsFormSiteData",
            ["siteParser"] = "msOFormsOleSiteConcrete",
            ["siteBitFlags"] = "0x00040023",
            ["siteAutoSize"] = true,
            ["formControlParser"] = "msOFormsFormControl",
            ["formPropMask"] = "0x0C000C48",
            ["sizeSource"] = "formControlDisplayedSize",
            ["displayedWidth"] = width,
            ["displayedHeight"] = height,
            ["logicalWidth"] = 0,
            ["logicalHeight"] = 0,
            ["multiPagePageCount"] = pageNames.Count,
            ["multiPageId"] = tabStripId,
            ["multiPageXStreamPath"] = $"{storagePath}/x",
            ["generatedStoragePath"] = storagePath,
            ["generatedStorageF"] = fStream,
            ["generatedStorageO"] = tabStripPayload,
            ["generatedStorageX"] = xStream,
            ["generatedStorageCompObjKind"] = "MultiPage"
        };

        return new GeneratedMultiPageControlBytes(sitePayload, metadata, pages);
    }

    public static GeneratedPageControlBytes CreatePage(
        string name,
        int siteId,
        int tabIndex,
        int width,
        int height,
        string storagePath,
        bool isFirstPage = false)
    {
        var sitePayload = FormSiteFactory.BuildStorageOleSiteConcrete(
            name,
            siteId,
            tabIndex,
            0x07,
            0,
            0,
            isFirstPage ? 0x0004_0021u : 0x0004_0023u);

        return new GeneratedPageControlBytes(
            name,
            siteId,
            storagePath,
            BuildPageFormStream(width, height, siteId),
            Array.Empty<byte>(),
            sitePayload,
            BuildDefaultPageProperties());
    }

    private static byte[] BuildMultiPageFormStream(int width, int height, int nextAvailableId, IReadOnlyList<byte[]> sites) =>
        BuildContainerFormStream(width, height, nextAvailableId + 2 + sites.Count, sites, includePageTail: true);

    private static byte[] BuildPageFormStream(int width, int height, int nextAvailableId) =>
        BuildContainerFormStream(width, height, nextAvailableId + 2, [], includePageTail: false);

    private static byte[] BuildContainerFormStream(int width, int height, int nextAvailableId, IReadOnlyList<byte[]> sites, bool includePageTail)
    {
        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)nextAvailableId));
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 0x0000_C004);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 1);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 32_000);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, width, height);
        MsFormsFactoryBinary.WriteSize(extra, 0, 0);

        using var formControl = new MemoryStream();
        formControl.WriteByte(0);
        formControl.WriteByte(4);
        MsFormsFactoryBinary.WriteUInt16(formControl, checked((ushort)(4 + dataBlock.Length + extra.Length)));
        MsFormsFactoryBinary.WriteUInt32(formControl, 0x0C00_0C48);
        formControl.Write(dataBlock.ToArray());
        formControl.Write(extra.ToArray());

        using var payload = new MemoryStream();
        if (sites.Count > 0)
        {
            payload.WriteByte(0);
            payload.WriteByte(checked((byte)(0x80 | sites.Count)));
            payload.WriteByte(1);
            MsFormsFactoryBinary.WritePadding(payload, 4);
        }
        foreach (var site in sites)
        {
            payload.Write(site);
        }

        using var siteData = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(siteData, checked((uint)sites.Count));
        MsFormsFactoryBinary.WriteUInt32(siteData, checked((uint)payload.Length));
        siteData.Write(payload.ToArray());
        return includePageTail
            ? [.. formControl.ToArray(), .. siteData.ToArray(), .. BuildDefaultMultiPageTail()]
            : [.. formControl.ToArray(), .. siteData.ToArray()];
    }

    private static byte[] BuildInternalObjectSite(int siteId, byte typeCode, int objectStreamSize)
    {
        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)siteId));
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)objectStreamSize));
        MsFormsFactoryBinary.WriteUInt16(dataBlock, 0);
        MsFormsFactoryBinary.WriteUInt16(dataBlock, typeCode);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteInt32(extra, 0);
        MsFormsFactoryBinary.WriteInt32(extra, 0);

        using var output = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt16(output, 0);
        MsFormsFactoryBinary.WriteUInt16(output, checked((ushort)(4 + dataBlock.Length + extra.Length)));
        MsFormsFactoryBinary.WriteUInt32(output, 0x0000_01E4);
        output.Write(dataBlock.ToArray());
        output.Write(extra.ToArray());
        return output.ToArray();
    }

    private static byte[] BuildMultiPageXStream(int multiPageId, IReadOnlyList<int> pageIds)
    {
        using var output = new MemoryStream();
        for (var i = 0; i < pageIds.Count + 1; i++)
        {
            output.WriteByte(0);
            output.WriteByte(2);
            MsFormsFactoryBinary.WriteUInt16(output, 4);
            MsFormsFactoryBinary.WriteUInt32(output, 0);
        }

        output.WriteByte(0);
        output.WriteByte(2);
        MsFormsFactoryBinary.WriteUInt16(output, 12);
        MsFormsFactoryBinary.WriteUInt32(output, 0x0000_0006);
        MsFormsFactoryBinary.WriteInt32(output, pageIds.Count);
        MsFormsFactoryBinary.WriteInt32(output, multiPageId + 1);
        foreach (var pageId in pageIds)
        {
            MsFormsFactoryBinary.WriteUInt32(output, checked((uint)pageId));
        }

        return output.ToArray();
    }

    public static byte[] BuildDefaultPageProperties()
    {
        using var output = new MemoryStream();
        output.WriteByte(0);
        output.WriteByte(2);
        MsFormsFactoryBinary.WriteUInt16(output, 4);
        MsFormsFactoryBinary.WriteUInt32(output, 0);
        return output.ToArray();
    }

    public static byte[] BuildDefaultPageTail() =>
        Convert.FromHexString("00020C0019000000F3FF0100FF010000");

    private static byte[] BuildDefaultMultiPageTail() =>
        Convert.FromHexString("00020C0019000000F08F0000FF010000");

    private static byte[] BuildInternalTabStripPayload(IReadOnlyList<string> pageNames, IReadOnlyList<string> pageCaptions, int width, int height)
    {
        var request = new GeneratedControlRequest(
            "TabStrip",
            "__internal",
            0,
            0,
            0,
            0,
            width,
            height,
            null,
            null,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["tabCaptions"] = pageCaptions.ToArray(),
                ["tabNames"] = pageNames.ToArray()
            });
        return new TabStripControlSchema().BuildObjectPayload(request);
    }

    private static string FormatStorageId(int id) => id is >= 0 and < 10 ? $"0{id}" : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed record GeneratedStorageControlBytes(
    byte[] SitePayload,
    IReadOnlyDictionary<string, object?> Metadata);

internal sealed record GeneratedMultiPageControlBytes(
    byte[] SitePayload,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<GeneratedPageControlBytes> Pages);

internal sealed record GeneratedPageControlBytes(
    string Name,
    int SiteId,
    string StoragePath,
    byte[] FStream,
    byte[] OStream,
    byte[] SitePayload,
    byte[] PageProperties);
