internal sealed class PatchDocument
{
    public Dictionary<string, string>? Renames { get; set; }
    public Dictionary<string, LayoutPatch>? Layout { get; set; }
    public Dictionary<string, Dictionary<string, JsonElement>>? Properties { get; set; }
    public List<AddControlPatch>? Add { get; set; }
}

internal sealed class LayoutPatch
{
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RawWidth { get; set; }
    public int? RawHeight { get; set; }
}

internal sealed class AddControlPatch
{
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string? FromTemplate { get; set; }
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Caption { get; set; }
}
