internal static class FrxRebuilder
{
    public static byte[] RebuildContainer(FrxBinary source, LayoutInspection? layout = null, RebuildStreamMode streamMode = RebuildStreamMode.ContainerOnly)
    {
        var dump = CompoundStorageInspector.Inspect(source.Bytes, source.OleOffset);
        if (streamMode == RebuildStreamMode.ObjectStreamRoundTrip)
        {
            if (layout is null)
            {
                throw new CliException("Object stream round-trip rebuild requires a parsed layout.");
            }

            dump = ObjectStreamRoundTripRewriter.RewriteObjectStreams(dump, layout);
        }

        var rebuiltOle = CompoundStorageRebuilder.BuildFromDump(dump);

        var output = new byte[source.OleOffset + rebuiltOle.Length];
        Buffer.BlockCopy(source.Bytes, 0, output, 0, source.OleOffset);
        Buffer.BlockCopy(rebuiltOle, 0, output, source.OleOffset, rebuiltOle.Length);
        return output;
    }
}

internal enum RebuildStreamMode
{
    ContainerOnly,
    ObjectStreamRoundTrip
}
