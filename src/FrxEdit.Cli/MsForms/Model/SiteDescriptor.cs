
internal sealed class SiteDescriptor
{
    public int SiteIndex { get; set; }
    public byte Depth { get; set; }
    public byte SiteType { get; set; }
    public uint PropMask { get; set; }
    
    public int StreamStart { get; set; }
    public int StreamEnd { get; set; }
    
    public string? Name { get; set; }
    public int NameOffset { get; set; }
    
    public string? Tag { get; set; }
    public int TagOffset { get; set; }
    
    public int? Left { get; set; }
    public int LeftOffset { get; set; }
    
    public int? Top { get; set; }
    public int TopOffset { get; set; }
    
    public uint? Id { get; set; }
    public int IdOffset { get; set; }
    
    public ushort? TabIndex { get; set; }
    public int TabIndexOffset { get; set; }
    
    public ushort? ClsidCacheIndex { get; set; }
    public int ClsidCacheIndexOffset { get; set; }
    
    public int? ObjectStreamSize { get; set; }
    public int ObjectStreamSizeOffset { get; set; }

    public uint? BitFlags { get; set; }
    public int BitFlagsOffset { get; set; }

    public string? ControlType { get; set; }
    
    public int ObjectStreamLocalOffset { get; set; } = -1;
    public long ObjectStreamFileOffset { get; set; }

    public Dictionary<string, object?> ExtraProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
}
