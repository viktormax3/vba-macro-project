internal static class FrxRebuilder
{
    public static byte[] RebuildContainer(FrxBinary source)
    {
        var dump = CompoundStorageInspector.Inspect(source.Bytes, source.OleOffset);
        var rebuiltOle = CompoundStorageRebuilder.BuildFromDump(dump);

        var output = new byte[source.OleOffset + rebuiltOle.Length];
        Buffer.BlockCopy(source.Bytes, 0, output, 0, source.OleOffset);
        Buffer.BlockCopy(rebuiltOle, 0, output, source.OleOffset, rebuiltOle.Length);
        return output;
    }
}
