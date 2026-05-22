using System;
using System.Collections.Generic;
using System.IO;
using System.Text;


internal static class FormSiteFactory
{
    public static byte[] BuildOleSiteConcrete(
        string name,
        int siteId,
        int tabIndex,
        byte typeCode,
        int left,
        int top,
        int objectStreamSize,
        uint siteFlags,
        Dictionary<string, object?>? properties = null)
    {
        return BuildSite(name, siteId, tabIndex, typeCode, left, top, objectStreamSize, siteFlags, properties);
    }

    public static byte[] BuildStorageOleSiteConcrete(
        string name,
        int siteId,
        int tabIndex,
        byte typeCode,
        int left,
        int top,
        uint siteFlags,
        Dictionary<string, object?>? properties = null)
    {
        return BuildSite(name, siteId, tabIndex, typeCode, left, top, null, siteFlags, properties);
    }

    private static byte[] BuildSite(
        string name,
        int siteId,
        int tabIndex,
        byte typeCode,
        int left,
        int top,
        int? objectStreamSize,
        uint siteFlags,
        Dictionary<string, object?>? properties)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);

        string? tag = properties is null ? null : MsFormsFactoryBinary.GetString(properties, "tag");
        var tagBytes = string.IsNullOrEmpty(tag) ? Array.Empty<byte>() : Encoding.Latin1.GetBytes(tag);

        int? helpContextId = properties is null ? null : MsFormsFactoryBinary.GetInt32(properties, "helpContextId");
        int? groupId = properties is null ? null : MsFormsFactoryBinary.GetInt32(properties, "groupId");

        string? controlTipText = properties is null ? null : MsFormsFactoryBinary.GetString(properties, "controlTipText");
        var controlTipBytes = string.IsNullOrEmpty(controlTipText) ? Array.Empty<byte>() : Encoding.Latin1.GetBytes(controlTipText);

        string? controlSource = properties is null ? null : MsFormsFactoryBinary.GetString(properties, "controlSource");
        var controlSourceBytes = string.IsNullOrEmpty(controlSource) ? Array.Empty<byte>() : Encoding.Latin1.GetBytes(controlSource);

        string? rowSource = properties is null ? null : MsFormsFactoryBinary.GetString(properties, "rowSource");
        var rowSourceBytes = string.IsNullOrEmpty(rowSource) ? Array.Empty<byte>() : Encoding.Latin1.GetBytes(rowSource);

        // Base propMask: Name (0), ID (2), BitFlags (4), TabIndex (6), ClsidCacheIndex (7), Position (8)
        uint propMask = 0x0000_01D5u;

        if (objectStreamSize.HasValue) propMask |= 1u << 5;
        if (tagBytes.Length > 0) propMask |= 1u << 1;
        if (helpContextId.HasValue) propMask |= 1u << 3;
        if (groupId.HasValue) propMask |= 1u << 9;
        if (controlTipBytes.Length > 0) propMask |= 1u << 11;
        if (controlSourceBytes.Length > 0) propMask |= 1u << 13;
        if (rowSourceBytes.Length > 0) propMask |= 1u << 14;

        using var dataBlock = new MemoryStream();

        void AlignDataBlock(int alignment)
        {
            var currentRel = 8 + dataBlock.Position;
            var padding = (alignment - (currentRel % alignment)) % alignment;
            for (var i = 0; i < padding; i++)
            {
                dataBlock.WriteByte(0);
            }
        }

        // 0. Name
        AlignDataBlock(4);
        MsFormsFactoryBinary.WriteCount(dataBlock, nameBytes.Length);

        // 1. Tag
        if ((propMask & (1u << 1)) != 0)
        {
            AlignDataBlock(4);
            MsFormsFactoryBinary.WriteCount(dataBlock, tagBytes.Length);
        }

        // 2. ID
        AlignDataBlock(4);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)siteId));

        // 3. HelpContextID
        if ((propMask & (1u << 3)) != 0)
        {
            AlignDataBlock(4);
            MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)helpContextId!.Value));
        }

        // 4. BitFlags
        AlignDataBlock(4);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, siteFlags);

        // 5. ObjectStreamSize
        if ((propMask & (1u << 5)) != 0)
        {
            AlignDataBlock(4);
            MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)objectStreamSize!.Value));
        }

        // 6. TabIndex
        AlignDataBlock(2);
        MsFormsFactoryBinary.WriteUInt16(dataBlock, tabIndex);

        // 7. ClsidCacheIndex
        AlignDataBlock(2);
        MsFormsFactoryBinary.WriteUInt16(dataBlock, typeCode);

        // 9. GroupID
        if ((propMask & (1u << 9)) != 0)
        {
            AlignDataBlock(2);
            MsFormsFactoryBinary.WriteUInt16(dataBlock, groupId!.Value);
        }

        // 11. ControlTipText
        if ((propMask & (1u << 11)) != 0)
        {
            AlignDataBlock(4);
            MsFormsFactoryBinary.WriteCount(dataBlock, controlTipBytes.Length);
        }

        // 13. ControlSource
        if ((propMask & (1u << 13)) != 0)
        {
            AlignDataBlock(4);
            MsFormsFactoryBinary.WriteCount(dataBlock, controlSourceBytes.Length);
        }

        // 14. RowSource
        if ((propMask & (1u << 14)) != 0)
        {
            AlignDataBlock(4);
            MsFormsFactoryBinary.WriteCount(dataBlock, rowSourceBytes.Length);
        }

        // Pad dataBlock so extraBlock starts relative-aligned to 4
        AlignDataBlock(4);

        using var extraBlock = new MemoryStream();

        void AlignExtraBlock(int alignment)
        {
            var currentRel = 8 + dataBlock.Length + extraBlock.Position;
            var padding = (alignment - (currentRel % alignment)) % alignment;
            for (var i = 0; i < padding; i++)
            {
                extraBlock.WriteByte(0);
            }
        }

        // 0. Name FMString
        extraBlock.Write(nameBytes);
        MsFormsFactoryBinary.WritePadding(extraBlock, 4);

        // 1. Tag FMString
        if ((propMask & (1u << 1)) != 0)
        {
            extraBlock.Write(tagBytes);
            MsFormsFactoryBinary.WritePadding(extraBlock, 4);
        }

        // 8. Position
        AlignExtraBlock(4);
        MsFormsFactoryBinary.WriteInt32(extraBlock, left);
        MsFormsFactoryBinary.WriteInt32(extraBlock, top);

        // 11. ControlTipText FMString
        if ((propMask & (1u << 11)) != 0)
        {
            AlignExtraBlock(4);
            extraBlock.Write(controlTipBytes);
            MsFormsFactoryBinary.WritePadding(extraBlock, 4);
        }

        // 13. ControlSource FMString
        if ((propMask & (1u << 13)) != 0)
        {
            AlignExtraBlock(4);
            extraBlock.Write(controlSourceBytes);
            MsFormsFactoryBinary.WritePadding(extraBlock, 4);
        }

        // 14. RowSource FMString
        if ((propMask & (1u << 14)) != 0)
        {
            AlignExtraBlock(4);
            extraBlock.Write(rowSourceBytes);
            MsFormsFactoryBinary.WritePadding(extraBlock, 4);
        }

        using var output = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt16(output, 0); // version prefix
        MsFormsFactoryBinary.WriteUInt16(output, checked((ushort)(4 + dataBlock.Length + extraBlock.Length))); // cbSite
        MsFormsFactoryBinary.WriteUInt32(output, propMask);
        output.Write(dataBlock.ToArray());
        output.Write(extraBlock.ToArray());

        return output.ToArray();
    }
}
