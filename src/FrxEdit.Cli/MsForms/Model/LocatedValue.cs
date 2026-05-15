
public readonly record struct LocatedValue<T>(
    T Value,
    int StreamOffset,
    long FileOffset,
    int Size
);
