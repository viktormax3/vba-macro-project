internal sealed record GeneratedControlRequest(
    string Type,
    string Name,
    int SiteId,
    int TabIndex,
    int Left,
    int Top,
    int Width,
    int Height,
    string? Caption,
    string? Value,
    Dictionary<string, object?> Properties);

internal sealed record GeneratedControlBuildResult(
    byte[] SitePayload,
    byte[] ObjectPayload,
    IReadOnlyDictionary<string, object?> Metadata);
