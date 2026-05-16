
internal static class FormStreamParser
{
    public static IReadOnlyList<StructuredControlRecord> Read(
        StorageEntryDump stream,
        IReadOnlySet<string>? knownControlNames,
        StorageEntryDump? objectStream = null)
    {
        var sites = StructuredMsFormsParser.Parse(stream);
        if (sites.Count > 0)
        {
            if (objectStream != null)
            {
                StructuredMsFormsParser.EnrichFromObjectStream(sites, objectStream);
            }

            return sites.Select(s => MapToRecord(s, stream, objectStream)).ToList();
        }

        return LegacyNameScanParser.Scan(stream, knownControlNames);
    }

    private static StructuredControlRecord MapToRecord(
        SiteDescriptor site,
        StorageEntryDump stream,
        StorageEntryDump? objectStream)
    {
        var marker = new ControlTypeMarker(
            site.TabIndexOffset > 0 ? site.TabIndexOffset : site.StreamStart,
            (byte)(site.TabIndex ?? 0),
            (byte)(site.ClsidCacheIndex ?? 0));

        var fileOffsets = stream.FileOffsets;
        var placement = new Placement(
            site.Left ?? 0,
            site.Top ?? 0,
            null,
            null,
            site.LeftOffset > 0 && site.LeftOffset < fileOffsets.Length ? fileOffsets[site.LeftOffset] : 0,
            site.TopOffset > 0 && site.TopOffset < fileOffsets.Length ? fileOffsets[site.TopOffset] : 0,
            null,
            null);

        var properties = new Dictionary<string, object?>(site.ExtraProperties, StringComparer.OrdinalIgnoreCase)
        {
            ["parser"] = "msOFormsFormSiteData",
            ["siteParser"] = "msOFormsOleSiteConcrete",
            ["siteIndex"] = site.SiteIndex,
            ["siteDepth"] = site.Depth,
            ["siteType"] = site.SiteType,
            ["sitePropMask"] = $"0x{site.PropMask:X8}",
            ["siteOffset"] = site.StreamStart < fileOffsets.Length ? fileOffsets[site.StreamStart] : 0,
            ["siteLocalOffset"] = site.StreamStart,
            ["siteName"] = site.Name,
            ["siteNameOffset"] = site.NameOffset < fileOffsets.Length ? fileOffsets[site.NameOffset] : 0,
        };

        if (!string.IsNullOrEmpty(stream.Path))
        {
            properties["storagePath"] = stream.ParentPath;
            properties["streamPath"] = stream.Path;
        }

        if (site.Id != null)
        {
            properties["id"] = site.Id;
            properties["siteId"] = site.Id;
            properties["idOffset"] = site.IdOffset < fileOffsets.Length ? fileOffsets[site.IdOffset] : 0;
            properties["siteIdOffset"] = site.IdOffset < fileOffsets.Length ? fileOffsets[site.IdOffset] : 0;
        }

        if (site.HelpContextId != null)
        {
            properties["helpContextId"] = site.HelpContextId;
            properties["helpContextIdOffset"] = site.HelpContextIdOffset < fileOffsets.Length ? fileOffsets[site.HelpContextIdOffset] : 0;
        }

        if (site.GroupId != null)
        {
            properties["groupId"] = site.GroupId;
            properties["groupIdOffset"] = site.GroupIdOffset < fileOffsets.Length ? fileOffsets[site.GroupIdOffset] : 0;
        }

        if (site.TabIndex != null)
        {
            properties["tabIndex"] = site.TabIndex;
            properties["tabIndexOffset"] = site.TabIndexOffset < fileOffsets.Length ? fileOffsets[site.TabIndexOffset] : 0;
        }

        if (site.ClsidCacheIndex != null)
        {
            properties["clsidCacheIndex"] = site.ClsidCacheIndex;
            properties["clsidCacheIndexOffset"] = site.ClsidCacheIndexOffset < fileOffsets.Length ? fileOffsets[site.ClsidCacheIndexOffset] : 0;
        }

        if (site.ObjectStreamSize != null)
        {
            properties["objectStreamSizeFromSite"] = site.ObjectStreamSize;
            properties["objectStreamLocalOffset"] = site.ObjectStreamLocalOffset;
            properties["objectStreamFileOffset"] = site.ObjectStreamFileOffset;
        }

        if (site.BitFlags != null)
        {
            properties["siteBitFlags"] = $"0x{site.BitFlags:X8}";
            AddSiteFlags(properties, site.BitFlags.Value);
        }

        var type = !string.IsNullOrWhiteSpace(site.ControlType) && !site.ControlType.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? site.ControlType
            : ControlTypeSchema.TryGetMsFormsType((byte)(site.ClsidCacheIndex ?? 0), out var resolvedType)
                ? resolvedType
                : "Unknown";

        var oProps = site.ObjectProperties;
        if (oProps is null && objectStream != null && site.ObjectStreamSize > 0 && site.ObjectStreamLocalOffset >= 0)
        {
            var slice = SliceStorage(objectStream, site.ObjectStreamLocalOffset, site.ObjectStreamSize.Value);
            oProps = ObjectStreamParser.Read(slice, type);
        }

        if (oProps != null)
        {
            foreach (var prop in oProps.Properties)
            {
                properties[prop.Key] = prop.Value;
            }

            if (oProps.Width != null || oProps.Height != null)
            {
                placement = placement with
                {
                    RawWidth = oProps.Width ?? placement.RawWidth,
                    RawHeight = oProps.Height ?? placement.RawHeight,
                    WidthOffset = oProps.WidthOffset ?? placement.WidthOffset,
                    HeightOffset = oProps.HeightOffset ?? placement.HeightOffset
                };
            }
        }

        return new StructuredControlRecord(
            stream,
            marker,
            site.StreamStart,
            site.StreamEnd,
            site.NameOffset,
            site.Name?.Length ?? 0,
            site.Name ?? string.Empty,
            site.Name ?? $"Control{site.SiteIndex}",
            type,
            placement,
            properties,
            objectStream);
    }

    private static StorageEntryDump SliceStorage(StorageEntryDump stream, int offset, int size)
    {
        return new StorageEntryDump(
            stream.Index,
            stream.Name,
            stream.Kind,
            stream.StartSector,
            (ulong)size,
            stream.IsMiniStream,
            "",
            null,
            null,
            [],
            stream.Data.AsSpan(offset, size).ToArray(),
            stream.FileOffsets.AsSpan(offset, size).ToArray());
    }

    private static void AddSiteFlags(Dictionary<string, object?> properties, uint value)
    {
        properties["tabStop"] = (value & (1u << 0)) != 0;
        properties["visible"] = (value & (1u << 1)) != 0;
        properties["default"] = (value & (1u << 2)) != 0;
        properties["cancel"] = (value & (1u << 3)) != 0;
        properties["streamed"] = (value & (1u << 4)) != 0;
        properties["siteAutoSize"] = (value & (1u << 5)) != 0;
        properties["fitToParent"] = (value & (1u << 9)) != 0;
        properties["selectChild"] = (value & (1u << 13)) != 0;
        properties["promoteControls"] = (value & (1u << 18)) != 0;
    }
}
