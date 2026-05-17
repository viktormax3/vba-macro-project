internal sealed record RebuildComparison(
    int SourceControlCount,
    int RebuiltControlCount,
    bool SemanticMatch,
    IReadOnlyList<string> Differences)
{
    public static RebuildComparison From(LayoutInspection source, LayoutInspection rebuilt)
    {
        var differences = new List<string>();
        var sourceControls = source.Controls.OrderBy(c => c.Parent ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rebuiltControls = rebuilt.Controls.OrderBy(c => c.Parent ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceControls.Count != rebuiltControls.Count)
        {
            differences.Add($"control count differs: {sourceControls.Count} vs {rebuiltControls.Count}");
        }

        var rebuiltByKey = rebuiltControls.ToDictionary(ControlKey, StringComparer.OrdinalIgnoreCase);
        foreach (var sourceControl in sourceControls)
        {
            var key = ControlKey(sourceControl);
            if (!rebuiltByKey.TryGetValue(key, out var rebuiltControl))
            {
                differences.Add($"missing rebuilt control: {key}");
                continue;
            }

            CompareControl(sourceControl, rebuiltControl, differences);
        }

        return new RebuildComparison(source.Controls.Count, rebuilt.Controls.Count, differences.Count == 0, differences);
    }

    private static string ControlKey(ControlInfo control) =>
        $"{control.Parent ?? "<root>"}/{control.Name}:{control.Type}";

    private static void CompareControl(ControlInfo source, ControlInfo rebuilt, List<string> differences)
    {
        var key = ControlKey(source);
        CompareValue(key, "left", source.Left, rebuilt.Left, differences);
        CompareValue(key, "top", source.Top, rebuilt.Top, differences);
        CompareValue(key, "width", source.RawWidth, rebuilt.RawWidth, differences);
        CompareValue(key, "height", source.RawHeight, rebuilt.RawHeight, differences);

        foreach (var propertyName in new[]
                 {
                     "caption", "value", "tag", "controlTipText", "backColor", "foreColor", "borderColor",
                     "fontName", "fontSize", "tabIndex", "parser", "sizeSource"
                 })
        {
            object? sourceValue = null;
            object? rebuiltValue = null;

            source.Properties?.TryGetValue(propertyName, out sourceValue);
            rebuilt.Properties?.TryGetValue(propertyName, out rebuiltValue);

            if (sourceValue is null && rebuiltValue is null)
            {
                continue;
            }

            var sourceJson = JsonSerializer.Serialize(sourceValue, FrxEditApp.JsonOptions);
            var rebuiltJson = JsonSerializer.Serialize(rebuiltValue, FrxEditApp.JsonOptions);
            if (!sourceJson.Equals(rebuiltJson, StringComparison.Ordinal))
            {
                differences.Add($"{key}.{propertyName}: {sourceJson} != {rebuiltJson}");
            }
        }
    }

    private static void CompareValue<T>(string key, string name, T? source, T? rebuilt, List<string> differences)
    {
        if (!EqualityComparer<T?>.Default.Equals(source, rebuilt))
        {
            differences.Add($"{key}.{name}: {source} != {rebuilt}");
        }
    }
}
