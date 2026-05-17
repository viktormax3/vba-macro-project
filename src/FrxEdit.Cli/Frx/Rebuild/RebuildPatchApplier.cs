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

    public static LayoutInspection ApplyObjectPropertyPatch(LayoutInspection source, PatchDocument patch)
    {
        ValidateObjectPatch(patch);

        if (patch.Properties is null || patch.Properties.Count == 0)
        {
            return source;
        }

        var patchedByName = patch.Properties.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        var controls = new List<ControlInfo>(source.Controls.Count);
        foreach (var control in source.Controls)
        {
            if (!patchedByName.TryGetValue(control.Name, out var requested))
            {
                controls.Add(control);
                continue;
            }

            controls.Add(ApplyToControl(control, requested));
        }

        return source with { Controls = controls };
    }

    public static void ValidateObjectPatch(PatchDocument patch)
    {
        if (patch.Add is { Count: > 0 })
        {
            throw new CliException("Rebuild object-patch does not support 'add' yet. Add/remove controls requires FormSiteData rebuild.");
        }

        if (patch.Renames is { Count: > 0 })
        {
            throw new CliException("Rebuild object-patch does not support 'renames' yet because control names live in FormSiteData. Use the in-place editor for short renames, or wait for f-stream rebuild.");
        }

        if (patch.Layout is { Count: > 0 })
        {
            throw new CliException("Rebuild object-patch does not support 'layout' yet. Layout touches FormSiteData positions and object sizes together; use apply for now, or wait for f-stream rebuild.");
        }

        foreach (var (controlName, properties) in patch.Properties ?? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var propertyName in properties.Keys)
            {
                if (!ObjectPropertyNames.Contains(propertyName))
                {
                    throw new CliException($"Rebuild object-patch cannot write '{controlName}.{propertyName}' yet. Supported object payload properties: {string.Join(", ", ObjectPropertyNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}.");
                }
            }
        }
    }

    private static ControlInfo ApplyToControl(ControlInfo control, Dictionary<string, JsonElement> requested)
    {
        if (control.Properties is null)
        {
            throw new CliException($"Cannot patch '{control.Name}': control has no object metadata.");
        }

        var props = new Dictionary<string, object?>(control.Properties, StringComparer.OrdinalIgnoreCase);
        foreach (var (propertyName, value) in requested)
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
                default:
                    throw new CliException($"Property '{propertyName}' is not supported by object-patch.");
            }
        }

        return control with { Properties = props };
    }

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
