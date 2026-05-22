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
        "controlSource",
        "accelerator",
        "textAlign",
        "paragraphAlign",
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
        "default",
        "cancel",
        "backStyle",
        "alignment",
        "wordWrap",
        "autoSize",
        "autoTab",
        "autoWordSelect",
        "hideSelection",
        "integralHeight",
        "multiLine",
        "selectionMargin",
        "enterKeyBehavior",
        "tabKeyBehavior",
        "enterFieldBehavior",
        "dragBehavior",
        "imeMode",
        "takeFocusOnClick",
        "maxLength",
        "passwordChar",
        "scrollBars",
        "specialEffect",
        "borderStyle",
        "displayStyle",
        "listWidth",
        "boundColumn",
        "textColumn",
        "columnCount",
        "listRows",
        "matchEntry",
        "listStyle",
        "showDropButtonWhen",
        "dropButtonStyle",
        "multiSelect",
        "columnHeads",
        "matchRequired",
        "editable",
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

        if (controlType.Equals("TextBox", StringComparison.OrdinalIgnoreCase))
        {
            AddDefault(values, sources, "enabled", true);
            AddDefault(values, sources, "visible", true);
            AddDefault(values, sources, "locked", false);
            AddDefault(values, sources, "tabStop", true);
            AddDefault(values, sources, "backColor", "&H80000005&");
            AddDefault(values, sources, "foreColor", "&H80000008&");
            AddDefault(values, sources, "borderColor", "&H80000006&");
            AddDefault(values, sources, "backStyle", 1);
            AddDefault(values, sources, "wordWrap", true);
            AddDefault(values, sources, "autoSize", false);
            AddDefault(values, sources, "autoTab", false);
            AddDefault(values, sources, "autoWordSelect", true);
            AddDefault(values, sources, "dragBehavior", 0);
            AddDefault(values, sources, "enterFieldBehavior", 0);
            AddDefault(values, sources, "enterKeyBehavior", false);
            AddDefault(values, sources, "hideSelection", true);
            AddDefault(values, sources, "integralHeight", true);
            AddDefault(values, sources, "multiLine", false);
            AddDefault(values, sources, "selectionMargin", true);
            AddDefault(values, sources, "tabKeyBehavior", false);
            AddDefault(values, sources, "imeMode", 0);
            AddDefault(values, sources, "maxLength", 0);
            AddDefault(values, sources, "scrollBars", 0);
            AddDefault(values, sources, "borderStyle", 1);
            AddDefault(values, sources, "specialEffect", 2);
            AddDefault(values, sources, "textAlign", "left");
            AddDefault(values, sources, "fontName", "Tahoma");
            AddDefault(values, sources, "fontSize", 8);
        }

        if (controlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase) ||
            controlType.Equals("ListBox", StringComparison.OrdinalIgnoreCase))
        {
            AddDefault(values, sources, "backColor", "&H80000005&");
            AddDefault(values, sources, "foreColor", "&H80000008&");
            AddDefault(values, sources, "borderColor", "&H80000006&");
            AddDefault(values, sources, "borderStyle", 1);
            AddDefault(values, sources, "specialEffect", 2);
            AddDefault(values, sources, "boundColumn", 1);
            AddDefault(values, sources, "textColumn", -1);
            AddDefault(values, sources, "columnCount", 1);
            AddDefault(values, sources, "listWidth", 0);
            AddDefault(values, sources, "listStyle", 0);
            AddDefault(values, sources, "matchEntry", 2);
            AddDefault(values, sources, "textAlign", "left");
            AddDefault(values, sources, "fontName", "Tahoma");
            AddDefault(values, sources, "fontSize", 8);
        }

        if (controlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase))
        {
            AddDefault(values, sources, "listRows", 8);
            AddDefault(values, sources, "dropButtonStyle", 1);
            AddDefault(values, sources, "showDropButtonWhen", 0);
            AddDefault(values, sources, "maxLength", 0);
        }

        if (controlType.Equals("ListBox", StringComparison.OrdinalIgnoreCase))
        {
            AddDefault(values, sources, "multiSelect", 0);
            AddDefault(values, sources, "scrollBars", 3);
        }

        if (controlType.Equals("CheckBox", StringComparison.OrdinalIgnoreCase) ||
            controlType.Equals("OptionButton", StringComparison.OrdinalIgnoreCase))
        {
            AddDefault(values, sources, "value", "0");
            AddDefault(values, sources, "backColor", "&H8000000F&");
            AddDefault(values, sources, "foreColor", "&H80000008&");
            AddDefault(values, sources, "enabled", true);
            AddDefault(values, sources, "visible", true);
            AddDefault(values, sources, "locked", false);
            AddDefault(values, sources, "tabStop", true);
            AddDefault(values, sources, "backStyle", 1);
            AddDefault(values, sources, "alignment", 1);
            AddDefault(values, sources, "wordWrap", true);
            AddDefault(values, sources, "autoSize", false);
            AddDefault(values, sources, "imeMode", 0);
            AddDefault(values, sources, "picturePosition", 458753);
            AddDefault(values, sources, "specialEffect", 0);
            AddDefault(values, sources, "multiSelect", 0);
            AddDefault(values, sources, "textAlign", "left");
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

        if (IsRebuiltMorphProperty(name, properties))
        {
            return true;
        }

        return name.ToLowerInvariant() switch
        {
            "caption" or "tag" or "controltiptext" or "controlsource" or "fontname" or "value" or "groupname" =>
                properties.ContainsKey($"{name}Span") || properties.ContainsKey($"{name}Offset"),
            "backcolor" or "forecolor" or "fontsize" or "bordercolor" =>
                properties.ContainsKey($"{name}Offset"),
            "tabindex" =>
                properties.ContainsKey("tabIndexOffset") || properties.ContainsKey("recordMarkerOffset"),
            _ => false
        };
    }

    private static bool IsRebuiltMorphProperty(string name, Dictionary<string, object?> properties)
    {
        if (!properties.TryGetValue("controlType", out var controlType) ||
            (!string.Equals(controlType?.ToString(), "TextBox", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(controlType?.ToString(), "ComboBox", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(controlType?.ToString(), "ListBox", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(controlType?.ToString(), "CheckBox", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(controlType?.ToString(), "OptionButton", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.Equals(controlType?.ToString(), "CheckBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(controlType?.ToString(), "OptionButton", StringComparison.OrdinalIgnoreCase))
        {
            return name.ToLowerInvariant() is
                "caption" or "value" or "groupname" or "fontname" or "fontsize" or "backcolor" or
                "forecolor" or "enabled" or "locked" or "backstyle" or "alignment" or "wordwrap" or
                "autosize" or "imemode" or "specialeffect" or "mousepointer" or "pictureposition" or
                "accelerator" or "textalign";
        }

        return name.ToLowerInvariant() is
            "value" or "fontname" or "fontsize" or "backcolor" or "forecolor" or "bordercolor" or
            "enabled" or "locked" or "backstyle" or "autosize" or "autotab" or "autowordselect" or
            "dragbehavior" or "enterfieldbehavior" or "enterkeybehavior" or "hideselection" or
            "integralheight" or "multiline" or "selectionmargin" or "tabkeybehavior" or "wordwrap" or
            "imemode" or "maxlength" or "passwordchar" or "scrollbars" or "borderstyle" or
            "specialeffect" or "mousepointer" or "textalign" or "listwidth" or "boundcolumn" or
            "textcolumn" or "columncount" or "listrows" or "matchentry" or "liststyle" or
            "showdropbuttonwhen" or "dropbuttonstyle" or "multiselect" or "columnheads" or
            "matchrequired" or "editable" or "displaystyle" or "caption" or "groupname" or
            "pictureposition" or "accelerator" or "alignment";
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
    Dictionary<string, object?>? ParserValidation = null,
    IReadOnlyList<ControlInfo>? RemovedControls = null,
    IReadOnlyList<string>? RemovedStoragePaths = null);

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
