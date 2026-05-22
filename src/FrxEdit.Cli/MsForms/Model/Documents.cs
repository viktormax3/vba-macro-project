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
    Dictionary<string, Dictionary<string, object?>> Properties)
{
    private static readonly string[] HumanPropertyOrder =
    [
        "caption", "text", "value", "tag", "controlTipText", "controlSource", "accelerator",
        "textAlign", "paragraphAlign", "backColor", "foreColor", "borderColor", "fontName",
        "fontSize", "fontWeight", "fontEffects", "fontItalic", "fontUnderline", "fontStrikethrough",
        "enabled", "visible", "locked", "tabIndex", "tabStop", "default", "cancel", "backStyle",
        "alignment", "wordWrap", "autoSize", "autoTab", "autoWordSelect", "hideSelection",
        "integralHeight", "multiLine", "selectionMargin", "enterKeyBehavior", "tabKeyBehavior",
        "enterFieldBehavior", "dragBehavior", "imeMode", "takeFocusOnClick", "maxLength",
        "passwordChar", "scrollBars", "specialEffect", "borderStyle", "displayStyle", "listWidth",
        "boundColumn", "textColumn", "columnCount", "listRows", "matchEntry", "listStyle",
        "showDropButtonWhen", "dropButtonStyle", "multiSelect", "columnHeads", "matchRequired",
        "editable", "mousePointer", "picturePosition", "min", "max", "position", "smallChange",
        "largeChange", "orientation"
    ];

    public static HumanLayoutDocument FromRaw(LayoutDocument raw)
    {
        var properties = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        var formProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (raw.FrxFormControl is null)
        {
            foreach (var kvp in raw.FormProperties)
            {
                formProps[kvp.Key] = kvp.Value;
            }
        }
        else
        {
            foreach (var kvp in raw.FrxFormControl)
            {
                if (kvp.Value is System.Text.Json.JsonElement element && (element.ValueKind == System.Text.Json.JsonValueKind.Object || element.ValueKind == System.Text.Json.JsonValueKind.Array))
                {
                    continue;
                }
                formProps[kvp.Key] = kvp.Value;
            }
        }
        properties[raw.FormName] = formProps;

        foreach (var control in raw.Controls)
        {
            var controlProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = control.Type,
                ["parent"] = control.Parent,
                ["leftPt"] = control.LeftPt,
                ["topPt"] = control.TopPt,
                ["widthPt"] = control.WidthPt,
                ["heightPt"] = control.HeightPt
            };

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddDefaultPlaceholders(control.Type, values, sources);

            if (control.Properties is not null)
            {
                foreach (var name in HumanPropertyOrder)
                {
                    if (control.Properties.TryGetValue(name, out var value))
                    {
                        controlProps[name] = value;
                    }
                    else if (values.TryGetValue(name, out var defaultValue))
                    {
                        controlProps[name] = defaultValue;
                    }
                }
                foreach (var kvp in control.Properties)
                {
                    if (!controlProps.ContainsKey(kvp.Key))
                    {
                        if (!kvp.Key.EndsWith("Offset", StringComparison.OrdinalIgnoreCase) &&
                            !kvp.Key.EndsWith("Span", StringComparison.OrdinalIgnoreCase))
                        {
                            controlProps[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            else
            {
                foreach (var name in HumanPropertyOrder)
                {
                    if (values.TryGetValue(name, out var defaultValue))
                    {
                        controlProps[name] = defaultValue;
                    }
                }
            }
            
            foreach (var kvp in values)
            {
                if (!controlProps.ContainsKey(kvp.Key))
                {
                    controlProps[kvp.Key] = kvp.Value;
                }
            }

            properties[control.Name] = controlProps;
        }

        return new HumanLayoutDocument(raw.FormName, raw.FrxFile, properties);
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
}

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
