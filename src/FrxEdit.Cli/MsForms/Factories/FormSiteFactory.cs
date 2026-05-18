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
        uint siteFlags)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteCount(dataBlock, nameBytes.Length);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)siteId));
        MsFormsFactoryBinary.WriteUInt32(dataBlock, siteFlags);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, checked((uint)objectStreamSize));
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
        MsFormsFactoryBinary.WriteUInt32(output, 0x0000_01F5); // Name, ID, BitFlags, ObjectStreamSize, TabIndex, ClsidCacheIndex, Position.
        output.Write(dataBlock.ToArray());
        output.Write(extra.ToArray());
        return output.ToArray();
    }
}
