internal static class RebuildPatchApplier
{
    private static readonly HashSet<string> ObjectPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "caption",
        "value",
        "groupName",
        "fontName",
        "fontSize",
        "backColor",
        "foreColor",
        "borderColor"
    };

    private static readonly HashSet<string> FormSitePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tabIndex"
    };

    public static LayoutInspection ApplyObjectPropertyPatch(LayoutInspection source, PatchDocument patch, bool allowFormSitePatch = false)
    {
        ValidateObjectPatch(patch, allowFormSitePatch);

        if ((patch.Properties is null || patch.Properties.Count == 0) &&
            (!allowFormSitePatch || patch.Layout is null || patch.Layout.Count == 0))
        {
            return source;
        }

        var patchedByName = patch.Properties?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);

        var layoutByName = allowFormSitePatch
            ? patch.Layout?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, LayoutPatch>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LayoutPatch>(StringComparer.OrdinalIgnoreCase);

        var controls = new List<ControlInfo>(source.Controls.Count);
        foreach (var control in source.Controls)
        {
            patchedByName.TryGetValue(control.Name, out var requested);
            layoutByName.TryGetValue(control.Name, out var layout);

            if (requested is null && layout is null)
            {
                controls.Add(control);
                continue;
            }

            controls.Add(ApplyToControl(control, requested, layout));
        }

        return source with { Controls = controls };
    }

    public static void ValidateObjectPatch(PatchDocument patch, bool allowFormSitePatch = false)
    {
        if (patch.Add is { Count: > 0 })
        {
            throw new CliException("Rebuild object-patch does not support 'add' yet. Add/remove controls requires FormSiteData rebuild.");
        }

        if (patch.Renames is { Count: > 0 })
        {
            throw new CliException("Rebuild object-patch does not support 'renames' yet because control names live in FormSiteData. Use the in-place editor for short renames, or wait for f-stream rebuild.");
        }

        if (patch.Layout is { Count: > 0 } && !allowFormSitePatch)
        {
            throw new CliException("Rebuild object-patch does not support 'layout'. Use '--stream-mode full-patch' for rebuild layout edits.");
        }

        foreach (var (controlName, properties) in patch.Properties ?? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var propertyName in properties.Keys)
            {
                if (!ObjectPropertyNames.Contains(propertyName) && !(allowFormSitePatch && FormSitePropertyNames.Contains(propertyName)))
                {
                    var supported = ObjectPropertyNames.Concat(allowFormSitePatch ? FormSitePropertyNames : Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                    throw new CliException($"Rebuild patch cannot write '{controlName}.{propertyName}' yet. Supported properties: {string.Join(", ", supported)}.");
                }
            }
        }
    }

    private static ControlInfo ApplyToControl(ControlInfo control, Dictionary<string, JsonElement>? requested, LayoutPatch? layout)
    {
        if (control.Properties is null)
        {
            throw new CliException($"Cannot patch '{control.Name}': control has no object metadata.");
        }

        var props = new Dictionary<string, object?>(control.Properties, StringComparer.OrdinalIgnoreCase);
        foreach (var (propertyName, value) in requested ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase))
        {
            switch (propertyName.ToLowerInvariant())
            {
                case "caption":
                case "value":
                case "groupname":
                case "fontname":
                    props[CanonicalPropertyName(propertyName)] = RequireString(control.Name, propertyName, value);
                    break;
                case "backcolor":
                case "forecolor":
                case "bordercolor":
                    props[CanonicalPropertyName(propertyName)] = RequireColorLikeString(control.Name, propertyName, value);
                    break;
                case "fontsize":
                    var size = RequireFontSize(control.Name, value);
                    props["fontSize"] = size;
                    props["fontHeightRaw"] = (int)Math.Round(size * 20.0, MidpointRounding.AwayFromZero);
                    break;
                case "tabindex":
                    props["tabIndex"] = RequireUInt16(control.Name, propertyName, value);
                    break;
                default:
                    throw new CliException($"Property '{propertyName}' is not supported by object-patch.");
            }
        }

        var left = control.Left;
        var top = control.Top;
        var rawWidth = control.RawWidth;
        var rawHeight = control.RawHeight;

        if (layout is not null)
        {
            left = layout.Left ?? ToRawPoints(layout.LeftPt) ?? left;
            top = layout.Top ?? ToRawPoints(layout.TopPt) ?? top;
            rawWidth = layout.RawWidth ?? layout.Width ?? ToRawPoints(layout.WidthPt) ?? rawWidth;
            rawHeight = layout.RawHeight ?? layout.Height ?? ToRawPoints(layout.HeightPt) ?? rawHeight;
        }

        return control with
        {
            Left = left,
            Top = top,
            RawWidth = rawWidth,
            RawHeight = rawHeight,
            LeftPt = left is int l ? FromRawPoints(l) : control.LeftPt,
            TopPt = top is int t ? FromRawPoints(t) : control.TopPt,
            WidthPt = rawWidth is int w ? FromRawPoints(w) : control.WidthPt,
            HeightPt = rawHeight is int h ? FromRawPoints(h) : control.HeightPt,
            Properties = props
        };
    }

    private const double HimetricPerPoint = 2540.0 / 72.0;

    private static int? ToRawPoints(double? points) =>
        points is null ? null : (int)Math.Round(points.Value * HimetricPerPoint, MidpointRounding.AwayFromZero);

    private static double FromRawPoints(int raw) =>
        Math.Round(raw / HimetricPerPoint, 2, MidpointRounding.AwayFromZero);

    private static string CanonicalPropertyName(string propertyName) =>
        propertyName.ToLowerInvariant() switch
        {
            "groupname" => "groupName",
            "fontname" => "fontName",
            "backcolor" => "backColor",
            "forecolor" => "foreColor",
            "bordercolor" => "borderColor",
            _ => propertyName
        };

    private static string RequireString(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string RequireColorLikeString(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new CliException($"Property '{propertyName}' for '{controlName}' cannot be empty.");
            }

            return text;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var raw))
        {
            return $"&H{raw:X8}&";
        }

        throw new CliException($"Property '{propertyName}' for '{controlName}' must be a VBA color string like '&H00CCCCCC&' or an unsigned integer.");
    }

    private static int RequireUInt16(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed) || parsed is < 0 or > ushort.MaxValue)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be an integer between 0 and 65535.");
        }

        return parsed;
    }

    private static double RequireFontSize(string controlName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var size))
        {
            throw new CliException($"Property 'fontSize' for '{controlName}' must be numeric.");
        }

        if (size is <= 0 or > 72)
        {
            throw new CliException($"Property 'fontSize' for '{controlName}' must be between 0 and 72.");
        }

        return size;
    }
}
