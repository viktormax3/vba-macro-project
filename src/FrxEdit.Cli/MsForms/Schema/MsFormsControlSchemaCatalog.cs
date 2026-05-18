internal static class MsFormsControlSchemaCatalog
{
    public static IReadOnlyList<MsFormsControlSchemaInfo> All { get; } =
    [
        new("CommandButton", "2.2.1", "msOFormsCommandButton", "0x00000028", "0x00000075", null, "0x00000013", "CommandButtonDataBlock, CommandButtonExtraDataBlock, CommandButtonStreamData, TextProps", FactoryStatus.Ready),
        new("Label", "2.2.4", "msOFormsLabel", "0x00000028", "0x00000035", null, "0x00000032", "LabelDataBlock, LabelExtraDataBlock, LabelStreamData, TextProps", FactoryStatus.Ready),
        new("TextBox", "2.2.5", "msOFormsMorphData", "0x0000000080000101", "0x00000035", "0x2C80481B", "0x00000013", "MorphDataDataBlock, MorphDataExtraDataBlock, MorphDataStreamData, TextProps", FactoryStatus.Ready),
        new("ComboBox", "2.2.5", "msOFormsMorphData", "0x0000000080050141", "0x00000035", "0x2C80481B", "0x00000013", "MorphDataDataBlock, MorphDataExtraDataBlock, MorphDataStreamData, TextProps, optional ColumnInfo", FactoryStatus.Ready),
        new("ListBox", "2.2.5", "msOFormsMorphData", "0x0000000080010160", "0x00000035", null, "0x00000013", "MorphDataDataBlock, MorphDataExtraDataBlock, MorphDataStreamData, TextProps, optional ColumnInfo", FactoryStatus.Ready),
        new("CheckBox", "2.2.5", "msOFormsMorphData", "0x0000000080C00146", "0x00000035", null, "0x00000013", "MorphDataDataBlock, MorphDataExtraDataBlock, MorphDataStreamData, TextProps", FactoryStatus.Ready),
        new("OptionButton", "2.2.5", "msOFormsMorphData", "0x0000000080C00146", "0x00000035", null, "0x00000013", "MorphDataDataBlock, MorphDataExtraDataBlock, MorphDataStreamData, TextProps", FactoryStatus.Ready),
        new("ToggleButton", "2.2.5", "msOFormsMorphData", "0x0000000080C00146", "0x00000075", null, "0x00000013", "MorphDataDataBlock, MorphDataExtraDataBlock, MorphDataStreamData, TextProps", FactoryStatus.Ready),
        new("Image", "2.2.3", "msOFormsImage", "0x00000200", null, null, "0x00000013", "ImageDataBlock, ImageExtraDataBlock, ImageStreamData", FactoryStatus.Ready),
        new("ScrollBar", "2.2.7", "msOFormsScrollBar", "0x00002008", null, null, "0x00000013", "ScrollBarDataBlock, ScrollBarExtraDataBlock, ScrollBarStreamData", FactoryStatus.Ready),
        new("SpinButton", "2.2.8", "msOFormsSpinButton", "0x00000808", null, null, "0x00000013", "SpinButtonDataBlock, SpinButtonExtraDataBlock, SpinButtonStreamData", FactoryStatus.Ready),
        new("TabStrip", "2.2.9", "msOFormsTabStrip", "0x00FA8031", "0x00000035", null, "0x00000013", "TabStripDataBlock, TabStripExtraDataBlock, TabStripStreamData, TextProps, TabStripTabFlagData", FactoryStatus.Ready),
        new("Frame", "2.2.10", "msOFormsFormSiteData", null, null, null, "0x00040023", "Storage-backed FormControl/FormSiteData plus parent object metadata", FactoryStatus.Ready),
        new("MultiPage", "2.2.6", "msOFormsFormSiteData", null, null, null, "0x00040023", "Storage-backed FormControl/FormSiteData plus x stream and inner TabStrip", FactoryStatus.PendingStorageFactory),
        new("Page", "2.2.6.4", "msOFormsFormSiteData", null, null, null, "0x00040021/0x00040023", "PageProperties inside MultiPage x stream plus page storage", FactoryStatus.PendingStorageFactory),
    ];

    public static bool TryGet(string type, out MsFormsControlSchemaInfo info)
    {
        info = All.FirstOrDefault(candidate => candidate.Type.Equals(type, StringComparison.OrdinalIgnoreCase))!;
        return info is not null;
    }
}

internal sealed record MsFormsControlSchemaInfo(
    string Type,
    string SpecSection,
    string Parser,
    string? MinimalPropMask,
    string? TextPropsMask,
    string? VariousPropertyBits,
    string? SiteFlags,
    string RequiredStructures,
    FactoryStatus FactoryStatus);

internal enum FactoryStatus
{
    Ready,
    PendingFactory,
    PendingStorageFactory
}
