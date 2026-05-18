internal static class GeneratedStorageFactory
{
    public static bool CanCreate(string type) =>
        type.Equals("Frame", StringComparison.OrdinalIgnoreCase);

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
        var fStream = BuildFrameFormStream(frameCaption, width, height);

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

    private static byte[] BuildFrameFormStream(string caption, int width, int height)
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

        using var siteData = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt16(siteData, 0);
        MsFormsFactoryBinary.WriteUInt32(siteData, 0);
        MsFormsFactoryBinary.WriteUInt32(siteData, 0);

        return [.. formControl.ToArray(), .. siteData.ToArray()];
    }
}

internal sealed record GeneratedStorageControlBytes(
    byte[] SitePayload,
    IReadOnlyDictionary<string, object?> Metadata);
