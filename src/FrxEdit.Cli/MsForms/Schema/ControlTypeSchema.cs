internal static class ControlTypeSchema
{
    public static bool TryGetMsFormsType(byte typeCode, out string type)
    {
        type = typeCode switch
        {
            0x0C => "Image",
            0x0E => "Frame",
            0x10 => "SpinButton",
            0x11 => "CommandButton",
            0x12 => "TabStrip",
            0x15 => "Label",
            0x17 => "TextBox",
            0x18 => "ListBox",
            0x19 => "ComboBox",
            0x1A => "CheckBox",
            0x1B => "OptionButton",
            0x1C => "ToggleButton",
            0x2F => "ScrollBar",
            0x39 => "MultiPage",
            _ => string.Empty,
        };

        return type.Length > 0;
    }
}
