internal sealed class PatchDocument
{
    public Dictionary<string, string>? Renames { get; set; }
    public Dictionary<string, LayoutPatch>? Layout { get; set; }
    public Dictionary<string, Dictionary<string, JsonElement>>? Properties { get; set; }
    public List<string>? Remove { get; set; }
    public Dictionary<string, string?>? Move { get; set; }
    public List<AddControlPatch>? Add { get; set; }
    public CodePatch? Code { get; set; }

    public void Normalize(string? formName = null)
    {
        if (Properties is null) return;

        Layout ??= new Dictionary<string, LayoutPatch>(StringComparer.OrdinalIgnoreCase);

        var keysToRemove = new[] { "type", "parent", "formName", "frxFile", "recordIndex", "name", "$action", "$newName" };

        foreach (var pair in Properties)
        {
            var controlName = pair.Key;
            var props = pair.Value;
            if (props is null) continue;

            var isForm = string.Equals(controlName, "UserForm", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(controlName, "Form", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(controlName, "root", StringComparison.OrdinalIgnoreCase) ||
                         (formName != null && string.Equals(controlName, formName, StringComparison.OrdinalIgnoreCase)) ||
                         (props.ContainsKey("displayedWidth") || props.ContainsKey("clientWidth")); // heuristic if formName isn't known
            
            if (isForm)
            {
                continue;
            }

            string action = "edit";
            if (props.TryGetValue("$action", out var actionProp) && actionProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                action = actionProp.GetString()?.ToLowerInvariant() ?? "edit";
            }

            if (action == "remove")
            {
                Remove ??= new List<string>();
                if (!Remove.Contains(controlName)) Remove.Add(controlName);
            }
            else if (action == "rename")
            {
                if (props.TryGetValue("$newName", out var newNameProp) && newNameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var newName = newNameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        Renames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        Renames[controlName] = newName;
                    }
                }
            }
            else if (action == "add" || props.ContainsKey("type"))
            {
                Add ??= new List<AddControlPatch>();
                string? type = null;
                if (props.TryGetValue("type", out var typeProp) && typeProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    type = typeProp.GetString();
                string? parent = null;
                if (props.TryGetValue("parent", out var parentProp) && parentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    parent = parentProp.GetString();

                if (!Add.Any(a => string.Equals(a.Name, controlName, StringComparison.OrdinalIgnoreCase)))
                {
                    Add.Add(new AddControlPatch { Name = controlName, Type = type, Parent = parent });
                }
            }

            var layoutPatch = new LayoutPatch();
            bool hasLayout = false;

            if (props.TryGetValue("leftPt", out var leftPt) && leftPt.ValueKind == System.Text.Json.JsonValueKind.Number) { layoutPatch.LeftPt = leftPt.GetDouble(); hasLayout = true; props.Remove("leftPt"); }
            if (props.TryGetValue("topPt", out var topPt) && topPt.ValueKind == System.Text.Json.JsonValueKind.Number) { layoutPatch.TopPt = topPt.GetDouble(); hasLayout = true; props.Remove("topPt"); }
            if (props.TryGetValue("widthPt", out var widthPt) && widthPt.ValueKind == System.Text.Json.JsonValueKind.Number) { layoutPatch.WidthPt = widthPt.GetDouble(); hasLayout = true; props.Remove("widthPt"); }
            if (props.TryGetValue("heightPt", out var heightPt) && heightPt.ValueKind == System.Text.Json.JsonValueKind.Number) { layoutPatch.HeightPt = heightPt.GetDouble(); hasLayout = true; props.Remove("heightPt"); }

            if (hasLayout)
            {
                if (Layout.TryGetValue(controlName, out var existing))
                {
                    existing.LeftPt = layoutPatch.LeftPt ?? existing.LeftPt;
                    existing.TopPt = layoutPatch.TopPt ?? existing.TopPt;
                    existing.WidthPt = layoutPatch.WidthPt ?? existing.WidthPt;
                    existing.HeightPt = layoutPatch.HeightPt ?? existing.HeightPt;
                }
                else
                {
                    Layout[controlName] = layoutPatch;
                }
            }

            foreach (var k in keysToRemove)
            {
                props.Remove(k);
            }
        }
    }
}

internal sealed class CodePatch
{
    public Dictionary<string, List<string>>? TabStripPanels { get; set; }
}

internal sealed class LayoutPatch
{
    // Low-level FRX units. Kept for backwards compatibility and diagnostics.
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? RawWidth { get; set; }
    public int? RawHeight { get; set; }

    // Human-friendly points. These are converted to FRX units at apply time.
    public double? LeftPt { get; set; }
    public double? TopPt { get; set; }
    public double? WidthPt { get; set; }
    public double? HeightPt { get; set; }

    // Reserved aliases. Prefer widthPt/heightPt or rawWidth/rawHeight.
    public int? Width { get; set; }
    public int? Height { get; set; }
}

internal sealed class AddControlPatch
{
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string? Parent { get; set; }
    public string? FromTemplate { get; set; }

    // Low-level FRX units.
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RawWidth { get; set; }
    public int? RawHeight { get; set; }

    // Human-friendly points.
    public double? LeftPt { get; set; }
    public double? TopPt { get; set; }
    public double? WidthPt { get; set; }
    public double? HeightPt { get; set; }

    // Convenience aliases for common object payload fields.
    public string? Caption { get; set; }
    public string? Value { get; set; }
    public Dictionary<string, JsonElement>? Properties { get; set; }
}
