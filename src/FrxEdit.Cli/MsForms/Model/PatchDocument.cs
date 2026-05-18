internal sealed class PatchDocument
{
    public Dictionary<string, string>? Renames { get; set; }
    public Dictionary<string, LayoutPatch>? Layout { get; set; }
    public Dictionary<string, Dictionary<string, JsonElement>>? Properties { get; set; }
    public List<string>? Remove { get; set; }
    public Dictionary<string, string?>? Move { get; set; }
    public List<AddControlPatch>? Add { get; set; }
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
