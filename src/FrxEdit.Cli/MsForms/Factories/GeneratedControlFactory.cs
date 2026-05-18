internal static class GeneratedControlFactory
{
    private const int DefaultWidth = 72 * 2540 / 72;
    private const int DefaultHeight = 18 * 2540 / 72;

    private static readonly IGeneratedControlSchema[] Schemas =
    [
        new CommandButtonControlSchema(),
        new LabelControlSchema(),
        new TextBoxControlSchema(),
        new ComboBoxControlSchema(),
        new ListBoxControlSchema(),
        new CheckBoxControlSchema(),
        new OptionButtonControlSchema(),
        new ToggleButtonControlSchema(),
        new ImageControlSchema(),
        new ScrollBarControlSchema(),
        new SpinButtonControlSchema(),
        new TabStripControlSchema()
    ];

    public static bool CanCreate(string type) => TryGetSchema(type, out _);
    public static string SupportedTypes => string.Join(", ", Schemas.Select(schema => schema.Type));

    public static GeneratedControlBytes Create(
        string type,
        string name,
        int siteId,
        int tabIndex,
        int left,
        int top,
        int? rawWidth,
        int? rawHeight,
        string? caption,
        string? value,
        Dictionary<string, object?> properties)
    {
        if (!MsFormsControlSchemaCatalog.TryGet(type, out var catalogEntry) ||
            catalogEntry.FactoryStatus != FactoryStatus.Ready)
        {
            throw new CliException($"Cannot create '{name}': type '{type}' does not have a document-backed factory yet.");
        }

        if (!TryGetSchema(type, out var schema))
        {
            throw new CliException($"Cannot create '{name}': type '{type}' does not have a document-backed factory yet.");
        }

        if (!ControlTypeSchema.TryGetMsFormsTypeCode(schema.Type, out var typeCode))
        {
            throw new CliException($"Cannot create '{name}': unsupported MSForms type '{schema.Type}'.");
        }

        var request = new GeneratedControlRequest(
            schema.Type,
            name,
            siteId,
            tabIndex,
            left,
            top,
            rawWidth ?? DefaultWidth,
            rawHeight ?? DefaultHeight,
            caption,
            value,
            properties);

        var objectPayload = schema.BuildObjectPayload(request);
        var sitePayload = FormSiteFactory.BuildOleSiteConcrete(
            name,
            siteId,
            tabIndex,
            typeCode,
            left,
            top,
            objectPayload.Length,
            schema.SiteFlags);

        return new GeneratedControlBytes(sitePayload, objectPayload, schema.BuildMetadata(request, objectPayload.Length));
    }

    private static bool TryGetSchema(string type, out IGeneratedControlSchema schema)
    {
        schema = Schemas.FirstOrDefault(candidate => candidate.Type.Equals(type, StringComparison.OrdinalIgnoreCase))!;
        return schema is not null;
    }
}

internal sealed record GeneratedControlBytes(byte[] SitePayload, byte[] ObjectPayload, IReadOnlyDictionary<string, object?> Metadata);
