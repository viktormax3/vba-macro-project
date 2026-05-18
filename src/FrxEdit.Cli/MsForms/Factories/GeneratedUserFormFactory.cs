internal static class GeneratedUserFormFactory
{
    private const double HimetricPerPoint = 2540.0 / 72.0;

    public static (byte[] FrxBytes, string FrmText) Create(
        string formName,
        string caption,
        double widthPt,
        double heightPt,
        string frxFileName)
    {
        var width = ToRawPoints(widthPt);
        var height = ToRawPoints(heightPt);
        var ole = CompoundStorageRebuilder.BuildFromDump(BuildDump(width, height));
        var frx = BuildFrxWrapper(ole, widthPt, heightPt);
        return (frx, BuildFrm(formName, caption, widthPt, heightPt, frxFileName));
    }

    private static CompoundStorageDump BuildDump(int width, int height)
    {
        var root = new StorageEntryDump(
            0,
            "Root Entry",
            "Root",
            -1,
            0,
            false,
            string.Empty,
            null,
            null,
            [],
            [],
            [],
            ClsidHex: "F0692AC6DC16CE119E9800AA00574A4F")
        { Path = "Root Entry" };

        return new CompoundStorageDump(
            512,
            64,
            4096,
            0,
            [
                root,
                CreateStream("Root Entry/f", "f", BuildRootFormStream(width, height)),
                CreateStream("Root Entry/o", "o", []),
                CreateStream("Root Entry/\u0001CompObj", "\u0001CompObj", MsFormsCompObjFactory.Form)
            ]);
    }

    private static byte[] BuildRootFormStream(int width, int height)
    {
        using var dataBlock = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 1);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 1);
        MsFormsFactoryBinary.WriteUInt32(dataBlock, 32_000);

        using var extra = new MemoryStream();
        MsFormsFactoryBinary.WriteSize(extra, width, height);
        MsFormsFactoryBinary.WriteSize(extra, 0, 0);

        using var formControl = new MemoryStream();
        formControl.WriteByte(0);
        formControl.WriteByte(4);
        MsFormsFactoryBinary.WriteUInt16(formControl, checked((ushort)(4 + dataBlock.Length + extra.Length)));
        MsFormsFactoryBinary.WriteUInt32(formControl, 0x0C00_0C08);
        formControl.Write(dataBlock.ToArray());
        formControl.Write(extra.ToArray());

        using var siteData = new MemoryStream();
        MsFormsFactoryBinary.WriteUInt16(siteData, 0);
        MsFormsFactoryBinary.WriteUInt32(siteData, 0);
        MsFormsFactoryBinary.WriteUInt32(siteData, 0);

        return [.. formControl.ToArray(), .. siteData.ToArray()];
    }

    private static byte[] BuildFrxWrapper(byte[] ole, double widthPt, double heightPt)
    {
        var prefix = new byte[24];
        prefix[0] = 0x4C;
        prefix[1] = 0x42;
        prefix[2] = 0x08;
        prefix[3] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4, 4), checked((uint)ole.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(16, 2), checked((ushort)Math.Max(0, Math.Round(widthPt * 20))));
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(20, 2), checked((ushort)Math.Max(0, Math.Round(heightPt * 20 + 585))));

        var output = new byte[prefix.Length + ole.Length];
        Buffer.BlockCopy(prefix, 0, output, 0, prefix.Length);
        Buffer.BlockCopy(ole, 0, output, prefix.Length, ole.Length);
        return output;
    }

    private static string BuildFrm(string formName, string caption, double widthPt, double heightPt, string frxFileName)
    {
        var clientWidth = checked((int)Math.Round(widthPt * 20));
        var clientHeight = checked((int)Math.Round(heightPt * 20));
        return string.Join(Environment.NewLine,
        [
            "VERSION 5.00",
            $"Begin {{C62A69F0-16DC-11CE-9E98-00AA00574A4F}} {formName} ",
            $"   Caption         =   \"{EscapeFrmString(caption)}\"",
            $"   ClientHeight    =   {clientHeight}",
            "   ClientLeft      =   120",
            "   ClientTop       =   465",
            $"   ClientWidth     =   {clientWidth}",
            $"   OleObjectBlob   =   \"{frxFileName}\":0000",
            "   StartUpPosition =   1  'Centrar en propietario",
            "End",
            $"Attribute VB_Name = \"{formName}\"",
            "Attribute VB_GlobalNameSpace = False",
            "Attribute VB_Creatable = False",
            "Attribute VB_PredeclaredId = True",
            "Attribute VB_Exposed = False",
            string.Empty
        ]);
    }

    private static StorageEntryDump CreateStream(string path, string name, byte[] data) =>
        new(-1, name, "Stream", -1, (ulong)data.Length, data.Length < 4096, string.Empty, null, null, [], data, [])
        {
            Path = path,
            ParentPath = path[..path.LastIndexOf('/')]
        };

    private static int ToRawPoints(double value) =>
        checked((int)Math.Round(value * HimetricPerPoint, MidpointRounding.AwayFromZero));

    private static string EscapeFrmString(string value) => value.Replace("\"", "\"\"", StringComparison.Ordinal);
}
