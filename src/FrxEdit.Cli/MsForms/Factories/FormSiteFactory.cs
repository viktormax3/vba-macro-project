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
        var nameBytes = Encoding.Latin1.GetBytes(name);
        var controlSource = properties is null ? null : MsFormsFactoryBinary.GetString(properties, "controlSource");
        var controlSourceBytes = string.IsNullOrEmpty(controlSource) ? [] : Encoding.Latin1.GetBytes(controlSource);
        var propMask = 0x0000_01F5u; // Name, ID, BitFlags, ObjectStreamSize, TabIndex, ClsidCacheIndex, Position.
        if (controlSourceBytes.Length > 0) propMask |= 1u << 13;

        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteCount(dataBlock, nameBytes.Length);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)siteId));
        MsFormsFactoryBinary.WriteUInt32(dataBlock, siteFlags);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)objectStreamSize));
        MsFormsFactoryBinary.WriteUInt16(dataBlock, checked((ushort)tabIndex));
        MsFormsFactoryBinary.WriteUInt16(dataBlock, typeCode);
        if ((propMask & (1u << 13)) != 0)
        {
            MsFormsFactoryBinary.WritePadding(dataBlock, 4);
            MsFormsFactoryBinary.WriteCount(dataBlock, controlSourceBytes.Length);
        }

        using var extra = new MemoryStream();
        extra.Write(nameBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);
        MsFormsFactoryBinary.WriteInt32(extra, left);
        MsFormsFactoryBinary.WriteInt32(extra, top);
        if ((propMask & (1u << 13)) != 0)
        {
            MsFormsFactoryBinary.WritePadding(extra, 4);
            extra.Write(controlSourceBytes);
            MsFormsFactoryBinary.WritePadding(extra, 4);
        }

        using var output = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt16(output, 0);
        MsFormsFactoryBinary.WriteUInt16(output, checked((ushort)(4 + dataBlock.Length + extra.Length)));
        MsFormsFactoryBinary.WriteUInt32(output, propMask);
        output.Write(dataBlock.ToArray());
        output.Write(extra.ToArray());
        return output.ToArray();
    }

    public static byte[] BuildStorageOleSiteConcrete(
        string name,
        int siteId,
        int tabIndex,
        byte typeCode,
        int left,
        int top,
        uint siteFlags)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteCount(dataBlock, nameBytes.Length);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)siteId));
        MsFormsFactoryBinary.WriteUInt32(dataBlock, siteFlags);
        MsFormsFactoryBinary.WriteUInt16(dataBlock, checked((ushort)tabIndex));
        MsFormsFactoryBinary.WriteUInt16(dataBlock, typeCode);

        using var extra = new MemoryStream();
        extra.Write(nameBytes);
        MsFormsFactoryBinary.WritePadding(extra, 4);
        MsFormsFactoryBinary.WriteInt32(extra, left);
        MsFormsFactoryBinary.WriteInt32(extra, top);

        using var output = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt16(output, 0);
        MsFormsFactoryBinary.WriteUInt16(output, checked((ushort)(4 + dataBlock.Length + extra.Length)));
        output.WriteByte(0xD5);
        output.WriteByte(0x01);
        output.WriteByte(0x00);
        output.WriteByte(0x00);
        output.Write(dataBlock.ToArray());
        output.Write(extra.ToArray());
        return output.ToArray();
    }
}
