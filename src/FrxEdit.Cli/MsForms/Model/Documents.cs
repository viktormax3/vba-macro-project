internal sealed record LayoutDocument(
    string FormName,
    string FrxFile,
    Dictionary<string, object?> FormProperties,
    IReadOnlyList<ControlInfo> Controls);

internal sealed record HumanLayoutDocument(
    string FormName,
    string FrxFile,
    Dictionary<string, object?> FormProperties,
    IReadOnlyList<HumanControlInfo> Controls)
{
    private static readonly string[] HumanPropertyOrder =
    [
        "caption",
        "text",
        "value",
        "tag",
        "controlTipText",
        "accelerator",
        "backColor",
        "foreColor",
        "fontName",
        "fontSize",
        "default",
        "cancel",
        "enabled",
        "visible",
        "locked",
        "tabIndex",
        "tabStop",
        "wordWrap",
        "autoSize",
        "specialEffect",
        "picturePosition",
        "sizeSource"
    ];

    public static HumanLayoutDocument FromRaw(LayoutDocument raw)
    {
        return new HumanLayoutDocument(
            raw.FormName,
            raw.FrxFile,
            raw.FormProperties,
            raw.Controls.Select(ToHumanControl).ToList());
    }

    private static HumanControlInfo ToHumanControl(ControlInfo control)
    {
        var bounds = new HumanBounds(
            control.LeftPt,
            control.TopPt,
            control.WidthPt,
            control.HeightPt,
            control.Left,
            control.Top,
            control.RawWidth,
            control.RawHeight,
            LeftEditable: control.LeftOffset is not null,
            TopEditable: control.TopOffset is not null,
            RawWidthEditable: control.WidthOffset is not null,
            RawHeightEditable: control.HeightOffset is not null);

        return new HumanControlInfo(
            control.Name,
            control.Type,
            control.Parent,
            control.BinaryName,
            bounds,
            BuildHumanProperties(control));
    }

    private static IReadOnlyList<HumanProperty> BuildHumanProperties(ControlInfo control)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (control.Properties is not null)
        {
            foreach (var name in HumanPropertyOrder)
            {
                if (control.Properties.TryGetValue(name, out var value))
                {
                    values[name] = value;
                    sources[name] = "frx";
                }
            }
        }

        if (control.Properties?.TryGetValue("caption", out var caption) == true)
        {
            values["caption"] = caption;
            sources["caption"] = "frx";
        }

        AddDefaultPlaceholders(control.Type, values, sources);

        return values
            .Select(pair => new HumanProperty(
                pair.Key,
                pair.Value,
                sources.TryGetValue(pair.Key, out var source) ? source : "unknown",
                IsCurrentlyEditable(pair.Key, control.Properties)))
            .ToList();
    }

    private static void AddDefaultPlaceholders(
        string controlType,
        Dictionary<string, object?> values,
        Dictionary<string, string> sources)
    {
        if (controlType.Equals("CommandButton", StringComparison.OrdinalIgnoreCase))
        {
            AddDefault(values, sources, "enabled", true);
            AddDefault(values, sources, "visible", true);
            AddDefault(values, sources, "locked", false);
            AddDefault(values, sources, "tabStop", true);
            AddDefault(values, sources, "wordWrap", false);
            AddDefault(values, sources, "autoSize", false);
            AddDefault(values, sources, "backColor", "&H8000000F&");
            AddDefault(values, sources, "foreColor", "&H80000012&");
            AddDefault(values, sources, "fontName", "Tahoma");
            AddDefault(values, sources, "fontSize", 8);
        }
    }

    private static void AddDefault(
        Dictionary<string, object?> values,
        Dictionary<string, string> sources,
        string name,
        object? value)
    {
        if (values.ContainsKey(name))
        {
            return;
        }

        values[name] = value;
        sources[name] = "default";
    }

    private static bool IsCurrentlyEditable(string name, Dictionary<string, object?>? properties)
    {
        if (properties is null)
        {
            return false;
        }

        return name.Equals("tabIndex", StringComparison.OrdinalIgnoreCase)
            ? properties.ContainsKey("recordMarkerOffset")
            : properties.ContainsKey($"{name}Offset");
    }
}

internal sealed record HumanControlInfo(
    string Name,
    string Type,
    string? Parent,
    string? BinaryName,
    HumanBounds Bounds,
    IReadOnlyList<HumanProperty> Properties);

internal sealed record HumanBounds(
    double? LeftPt,
    double? TopPt,
    double? WidthPt,
    double? HeightPt,
    int? LeftRaw,
    int? TopRaw,
    int? WidthRaw,
    int? HeightRaw,
    bool LeftEditable,
    bool TopEditable,
    bool RawWidthEditable,
    bool RawHeightEditable);

internal sealed record HumanProperty(string Name, object? Value, string Source, bool Editable);

internal sealed record LayoutInspection(IReadOnlyList<ControlInfo> Controls);

internal sealed record RecordDump(
    int Index,
    string Name,
    string Type,
    int? RecordBlock,
    int NameOffset,
    int? DeltaFromPrevious,
    int? Left,
    int? Top,
    double? LeftPt,
    double? TopPt,
    int? RawWidth,
    int? RawHeight,
    Dictionary<string, object?>? Properties,
    string HeaderHex,
    string NameAndPositionHex);

internal sealed record ControlInfo(
    string Name,
    string Type,
    int? Left,
    int? Top,
    int? RawWidth,
    int? RawHeight,
    double? LeftPt,
    double? TopPt,
    double? WidthPt,
    double? HeightPt,
    Dictionary<string, object?>? Properties,
    string? Parent,
    string? BinaryName,
    int? RecordIndex,
    int? RecordDelta,
    int? RecordBlock,
    int NameOffset,
    int? LeftOffset,
    int? TopOffset,
    int? WidthOffset,
    int? HeightOffset);

internal sealed record Placement(
    int Left,
    int Top,
    int? RawWidth,
    int? RawHeight,
    int LeftOffset,
    int TopOffset,
    int? WidthOffset,
    int? HeightOffset);

internal sealed record ContainerDimensions(int Width, int Height, int WidthOffset, int HeightOffset);

internal sealed record ControlTypeMarker(int Offset, byte TabIndex, byte TypeCode);

internal sealed record StructuredControlCandidate(
    ControlTypeMarker Marker,
    int NameOffset,
    string RawName,
    string Name,
    string Type,
    Placement Placement);

internal sealed record StructuredControlRecord(
    StorageEntryDump Stream,
    ControlTypeMarker Marker,
    int RecordStartOffset,
    int RecordEndOffset,
    int NameOffset,
    int NameLength,
    string RawName,
    string Name,
    string Type,
    Placement Placement,
    StorageEntryDump? ObjectStream);

internal sealed record ObjectStreamProperties(
    Dictionary<string, object?> Properties,
    int? Width,
    int? Height,
    int? WidthOffset,
    int? HeightOffset);

internal readonly record struct CountOfBytesWithCompressionFlag(int Count, bool Compressed);

internal readonly record struct TextRun(int Offset, string Text);

internal readonly record struct PairCandidate(int Offset, int First, int Second);

internal readonly record struct FontSizeCandidate(int Offset, int Raw, double Size);

internal sealed record SystemColorValue(int StreamOffset, int Offset, string Value);
