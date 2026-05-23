using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FrxEdit.Cli.MsForms.Model;

internal static class PatchDocumentGenerator
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
        "editable", "mousePointer", "picturePosition", "picture", "mouseIcon",
        "pictureSizeMode", "pictureAlignment", "pictureTiling",
        "min", "max", "position", "smallChange", "largeChange", "orientation",
        "logicalWidth", "logicalHeight", "scrollLeft", "scrollTop",
        "logicalWidthPt", "logicalHeightPt", "scrollLeftPt", "scrollTopPt"
    ];

    private static readonly HashSet<string> RootFormPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "formBackColor", "formForeColor", "formBorderColor", "formCaption", "formBorderStyle",
        "formMousePointer", "formScrollBars", "formCycle", "formSpecialEffect", "formPictureAlignment",
        "formPictureSizeMode", "formZoom", "formPicture", "formMouseIcon", "nextAvailableId", "displayedWidth", "displayedHeight",
        "displayedWidthPt", "displayedHeightPt", "logicalWidth", "logicalHeight", "logicalWidthPt",
        "logicalHeightPt", "scrollLeft", "scrollTop", "scrollLeftPt", "scrollTopPt", "formBooleanProperties",
        "StartUpPosition", "ShowModal", "Tag", "Left", "Top", "Width", "Height", "ClientLeft", "ClientTop", "ClientWidth", "ClientHeight",
        "DrawBuffer", "WhatsThisButton", "WhatsThisHelp", "picture", "mouseIcon"
    };

    private static string CanonicalizeRootFormPropertyName(string name)
    {
        if (name.StartsWith("form", StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[4..];
            if (rest.Length > 0)
                return char.ToLowerInvariant(rest[0]) + rest[1..];
        }
        if (name.Equals("displayedWidthPt", StringComparison.OrdinalIgnoreCase)) return "widthPt";
        if (name.Equals("displayedHeightPt", StringComparison.OrdinalIgnoreCase)) return "heightPt";
        if (name.Equals("StartUpPosition", StringComparison.OrdinalIgnoreCase)) return "startUpPosition";
        if (name.Equals("ShowModal", StringComparison.OrdinalIgnoreCase)) return "showModal";
        if (name.Equals("Tag", StringComparison.OrdinalIgnoreCase)) return "tag";
        if (name.Equals("Left", StringComparison.OrdinalIgnoreCase)) return "left";
        if (name.Equals("Top", StringComparison.OrdinalIgnoreCase)) return "top";
        if (name.Equals("Width", StringComparison.OrdinalIgnoreCase)) return "width";
        if (name.Equals("Height", StringComparison.OrdinalIgnoreCase)) return "height";
        if (name.Equals("ClientLeft", StringComparison.OrdinalIgnoreCase)) return "clientLeft";
        if (name.Equals("ClientTop", StringComparison.OrdinalIgnoreCase)) return "clientTop";
        if (name.Equals("ClientWidth", StringComparison.OrdinalIgnoreCase)) return "clientWidth";
        if (name.Equals("ClientHeight", StringComparison.OrdinalIgnoreCase)) return "clientHeight";
        if (name.Equals("DrawBuffer", StringComparison.OrdinalIgnoreCase)) return "drawBuffer";
        return name;
    }

    public static PatchDocument FromRaw(LayoutInspection raw, string formName, bool asTemplate = false)
    {
        var properties = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
        var layout = new Dictionary<string, LayoutPatch>(StringComparer.OrdinalIgnoreCase);
        var addList = asTemplate ? new List<AddControlPatch>() : null;

        // Map form properties
        var formProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (raw.FrxFormControl is not null)
        {
            foreach (var kvp in raw.FrxFormControl)
            {
                if (kvp.Value is JsonElement element && (element.ValueKind == JsonValueKind.Object || element.ValueKind == JsonValueKind.Array))
                {
                    continue;
                }
                if (RootFormPropertyNames.Contains(kvp.Key))
                {
                    if (kvp.Key.Equals("formBooleanProperties", StringComparison.OrdinalIgnoreCase) && kvp.Value is string hex && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        var bits = Convert.ToUInt32(hex[2..], 16);
                        formProps["enabled"] = (bits & 1) != 0;
                        formProps["pictureTiling"] = (bits & (1u << 4)) != 0;
                        formProps["keepScrollBarsVisible"] = (bits & (1u << 21)) != 0;
                        formProps["rightToLeft"] = (bits & (1u << 22)) != 0;
                    }
                    else
                    {
                        formProps[CanonicalizeRootFormPropertyName(kvp.Key)] = kvp.Value;
                    }
                }
            }
        }
        properties[formName] = ConvertToJsonElements(formProps);

        // Map controls
        foreach (var control in raw.Controls)
        {
            var controlProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            controlProps["leftPt"] = control.LeftPt;
            controlProps["topPt"] = control.TopPt;
            controlProps["widthPt"] = control.WidthPt;
            controlProps["heightPt"] = control.HeightPt;
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
                            !kvp.Key.EndsWith("Span", StringComparison.OrdinalIgnoreCase) &&
                            !kvp.Key.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                            !kvp.Key.Equals("parent", StringComparison.OrdinalIgnoreCase) &&
                            !kvp.Key.Equals("recordIndex", StringComparison.OrdinalIgnoreCase))
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

            // Remove non-patchable properties to avoid bloating the patch
            var cleanedProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in controlProps)
            {
                if (IsRebuiltMorphProperty(kvp.Key, controlProps, control.Type))
                {
                    cleanedProps[kvp.Key] = kvp.Value;
                }
            }
            
            // Add any other specific properties like 'type'
            cleanedProps["type"] = control.Type;
            if (control.Parent != null)
            {
                cleanedProps["parent"] = control.Parent;
            }

            properties[control.Name] = ConvertToJsonElements(cleanedProps);

            if (asTemplate)
            {
                addList!.Add(new AddControlPatch
                {
                    Type = control.Type,
                    Name = control.Name,
                    Parent = control.Parent ?? ""
                });
            }
        }

        return new PatchDocument
        {
            Layout = null,
            Properties = properties,
            Add = addList
        };
    }

    private static Dictionary<string, JsonElement> ConvertToJsonElements(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in dict)
        {
            var jsonString = JsonSerializer.Serialize(kvp.Value);
            result[kvp.Key] = JsonSerializer.Deserialize<JsonElement>(jsonString);
        }
        return result;
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
            AddDefault(values, sources, "backColor", "systemButtonFace");
            AddDefault(values, sources, "foreColor", "systemButtonText");
            AddDefault(values, sources, "fontName", "Tahoma");
            AddDefault(values, sources, "fontSize", 8);
        }

        if (controlType.Equals("TextBox", StringComparison.OrdinalIgnoreCase))
        {
            AddDefault(values, sources, "enabled", true);
            AddDefault(values, sources, "visible", true);
            AddDefault(values, sources, "locked", false);
            AddDefault(values, sources, "tabStop", true);
            AddDefault(values, sources, "backColor", "systemWindow");
            AddDefault(values, sources, "foreColor", "systemWindowText");
            AddDefault(values, sources, "borderColor", "systemWindowFrame");
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
            AddDefault(values, sources, "backColor", "systemWindow");
            AddDefault(values, sources, "foreColor", "systemWindowText");
            AddDefault(values, sources, "borderColor", "systemWindowFrame");
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
            AddDefault(values, sources, "backColor", "systemButtonFace");
            AddDefault(values, sources, "foreColor", "systemWindowText");
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

    private static bool IsRebuiltMorphProperty(string name, Dictionary<string, object?> properties, string controlType)
    {
        if (string.Equals(controlType, "CheckBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(controlType, "OptionButton", StringComparison.OrdinalIgnoreCase))
        {
            return name.ToLowerInvariant() is
                "caption" or "value" or "groupname" or "fontname" or "fontsize" or "fontweight" or "fontitalic" or "fontunderline" or "fontstrikethrough" or "fontcharset" or "backcolor" or
                "forecolor" or "enabled" or "locked" or "backstyle" or "alignment" or "wordwrap" or
                "autosize" or "imemode" or "specialeffect" or "mousepointer" or "pictureposition" or
                "picture" or "mouseicon" or
                "accelerator" or "textalign" or "tabindex" or "tabstop" or "visible" or
                "leftpt" or "toppt" or "widthpt" or "heightpt";
        }

        if (string.Equals(controlType, "CommandButton", StringComparison.OrdinalIgnoreCase))
        {
            return name.ToLowerInvariant() is
                "caption" or "fontname" or "fontsize" or "fontweight" or "fontitalic" or "fontunderline" or "fontstrikethrough" or "fontcharset" or "backcolor" or "forecolor" or
                "enabled" or "locked" or "wordwrap" or "autosize" or "mousepointer" or "pictureposition" or
                "picture" or "mouseicon" or "picturesizemode" or "picturealignment" or "picturetiling" or
                "accelerator" or "takefocusonclick" or "textalign" or "paragraphalign" or "tabindex" or "tabstop" or "visible" or "default" or "cancel" or
                "leftpt" or "toppt" or "widthpt" or "heightpt";
        }

        return name.ToLowerInvariant() is
            "value" or "fontname" or "fontsize" or "fontweight" or "fontitalic" or "fontunderline" or "fontstrikethrough" or "fontcharset" or "backcolor" or "forecolor" or "bordercolor" or
            "enabled" or "locked" or "backstyle" or "autosize" or "autotab" or "autowordselect" or
            "dragbehavior" or "enterfieldbehavior" or "enterkeybehavior" or "hideselection" or
            "integralheight" or "multiline" or "selectionmargin" or "tabkeybehavior" or "wordwrap" or
            "imemode" or "maxlength" or "passwordchar" or "scrollbars" or "borderstyle" or
            "specialeffect" or "mousepointer" or "textalign" or "listwidth" or "boundcolumn" or
            "textcolumn" or "columncount" or "listrows" or "matchentry" or "liststyle" or
            "showdropbuttonwhen" or "dropbuttonstyle" or "multiselect" or "columnheads" or
            "matchrequired" or "editable" or "displaystyle" or "caption" or "groupname" or
            "pictureposition" or "accelerator" or "alignment" or "tabindex" or "tabstop" or "visible" or
            "scrollleft" or "scrolltop" or "logicalwidth" or "logicalheight" or
            "scrollleftpt" or "scrolltoppt" or "logicalwidthpt" or "logicalheightpt" or
            "leftpt" or "toppt" or "widthpt" or "heightpt";
    }
}
