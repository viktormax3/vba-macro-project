internal sealed record FormControlProperties(
    Dictionary<string, object?> Properties,
    int? DisplayedWidth,
    int? DisplayedHeight,
    int? DisplayedWidthOffset,
    int? DisplayedHeightOffset,
    int? LogicalWidth,
    int? LogicalHeight,
    int? LogicalWidthOffset,
    int? LogicalHeightOffset,
    int? ScrollLeft,
    int? ScrollTop,
    int? ScrollLeftOffset,
    int? ScrollTopOffset);
