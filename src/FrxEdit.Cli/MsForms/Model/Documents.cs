internal sealed record LayoutDocument(
    string FormName,
    string FrxFile,
    Dictionary<string, object?> FormProperties,
    IReadOnlyList<ControlInfo> Controls,
    Dictionary<string, object?>? FrxFormControl = null,
    Dictionary<string, object?>? ParserValidation = null);

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
        "borderColor",
        "fontName",
        "fontSize",
        "fontWeight",
        "fontEffects",
        "fontItalic",
        "fontUnderline",
        "fontStrikethrough",
        "enabled",
        "visible",
        "locked",
        "tabIndex",
        "tabStop",
        "wordWrap",
        "autoSize",
        "specialEffect",
        "borderStyle",
        "displayStyle",
        "mousePointer",
        "picturePosition",
        "min",
        "max",
        "position",
        "smallChange",
        "largeChange",
        "orientation"
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
            Left: new HumanMeasure(control.LeftPt, control.LeftOffset is not null),
            Top: new HumanMeasure(control.TopPt, control.TopOffset is not null),
            Width: new HumanMeasure(control.WidthPt, control.WidthOffset is not null),
            Height: new HumanMeasure(control.HeightPt, control.HeightOffset is not null));

        return new HumanControlInfo(
            control.Name,
            control.Type,
            control.Parent,
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

        AddDefaultPlaceholders(control.Type, values, sources);

        return HumanPropertyOrder
            .Where(values.ContainsKey)
            .Select(name => new HumanProperty(
                name,
                values[name],
                sources.TryGetValue(name, out var source) ? source : "unknown",
                IsCurrentlyEditable(name, control.Properties)))
            .Concat(values.Keys
                .Where(name => !HumanPropertyOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new HumanProperty(
                    name,
                    values[name],
                    sources.TryGetValue(name, out var source) ? source : "unknown",
                    IsCurrentlyEditable(name, control.Properties))))
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

        return name.ToLowerInvariant() switch
        {
            "caption" or "tag" or "controltiptext" or "fontname" or "value" or "groupname" =>
                properties.ContainsKey($"{name}Span") || properties.ContainsKey($"{name}Offset"),
            "backcolor" or "forecolor" or "fontsize" or "bordercolor" =>
                properties.ContainsKey($"{name}Offset"),
            "tabindex" =>
                properties.ContainsKey("recordMarkerOffset"),
            _ => false
        };
    }
}

internal sealed record HumanControlInfo(
    string Name,
    string Type,
    string? Parent,
    HumanBounds Bounds,
    IReadOnlyList<HumanProperty> Properties);

internal sealed record HumanBounds(
    HumanMeasure Left,
    HumanMeasure Top,
    HumanMeasure Width,
    HumanMeasure Height);

internal sealed record HumanMeasure(double? Pt, bool Editable);

internal sealed record HumanProperty(string Name, object? Value, string Source, bool Editable);

internal sealed record LayoutInspection(
    IReadOnlyList<ControlInfo> Controls,
    Dictionary<string, object?>? FrxFormControl = null,
    Dictionary<string, object?>? ParserValidation = null);

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
    Dictionary<string, object?> SiteProperties,
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
