internal static class FrxRebuilder
{
    public static byte[] RebuildContainer(FrxBinary source, LayoutInspection? layout = null, RebuildStreamMode streamMode = RebuildStreamMode.ContainerOnly)
    {
        var dump = CompoundStorageInspector.Inspect(source.Bytes, source.OleOffset);
        if (streamMode is RebuildStreamMode.ObjectStreamRoundTrip or RebuildStreamMode.ObjectStreamSerializeFixed or RebuildStreamMode.ObjectStreamNormalizeStrings or RebuildStreamMode.ObjectStreamPatchProperties or RebuildStreamMode.FormAndObjectPatch)
        {
            if (layout is null)
            {
                throw new CliException("Object stream rebuild requires a parsed layout.");
            }

            dump = ObjectStreamRoundTripRewriter.RewriteObjectStreams(
                dump,
                layout,
                mode: streamMode switch
                {
                    RebuildStreamMode.ObjectStreamSerializeFixed => ObjectStreamRewriteMode.ActiveSerializeFixed,
                    RebuildStreamMode.ObjectStreamNormalizeStrings => ObjectStreamRewriteMode.NormalizeStrings,
                    RebuildStreamMode.ObjectStreamPatchProperties => ObjectStreamRewriteMode.PatchProperties,
                    RebuildStreamMode.FormAndObjectPatch => ObjectStreamRewriteMode.FormAndObjectPatch,
                    _ => ObjectStreamRewriteMode.RoundTrip
                });
        }

        var rebuiltOle = CompoundStorageRebuilder.BuildFromDump(dump);

        var output = new byte[source.OleOffset + rebuiltOle.Length];
        Buffer.BlockCopy(source.Bytes, 0, output, 0, source.OleOffset);
        Buffer.BlockCopy(rebuiltOle, 0, output, source.OleOffset, rebuiltOle.Length);
        PatchFrxOleBlobLength(source, output, rebuiltOle.Length);
        return output;
    }

    private static void PatchFrxOleBlobLength(FrxBinary source, byte[] output, int rebuiltOleLength)
    {
        // VBA FRX OLEObjectBlob records commonly wrap the CFB payload in a small
        // length-prefixed header. In the fixtures, the CFB starts at byte 24 and
        // the UInt32 at byte 4 is exactly the length of the embedded OLE storage.
        //
        // Our parser can find the OLE signature even when this header is stale,
        // but Corel/Office import uses the declared blob length while assigning
        // OleObjectBlob. If the rebuilt CFB is shorter/longer and this field still
        // contains the original size, import fails before MSForms parsing starts.
        if (source.OleOffset < 8 || output.Length < 8)
        {
            return;
        }

        var originalDeclaredLength = BinaryPrimitives.ReadUInt32LittleEndian(source.Bytes.AsSpan(4, 4));
        var originalOleLength = checked((uint)(source.Bytes.Length - source.OleOffset));
        if (originalDeclaredLength != originalOleLength)
        {
            // Unknown FRX prefix shape. Preserve it rather than guessing.
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), checked((uint)rebuiltOleLength));
    }
}

internal enum RebuildStreamMode
{
    ContainerOnly,
    ObjectStreamRoundTrip,
    ObjectStreamSerializeFixed,
    ObjectStreamNormalizeStrings,
    ObjectStreamPatchProperties,
    FormAndObjectPatch
}
