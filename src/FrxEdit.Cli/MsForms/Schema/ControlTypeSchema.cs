internal static class ControlTypeSchema
{
    public static bool TryGetMsFormsType(byte typeCode, out string type)
    {
        type = typeCode switch
        {
            0x07 => "Page",
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

    public static bool TryGetMsFormsTypeCode(string type, out byte typeCode)
    {
        typeCode = type.Trim() switch
        {
            var value when value.Equals("Page", StringComparison.OrdinalIgnoreCase) => 0x07,
            var value when value.Equals("Image", StringComparison.OrdinalIgnoreCase) => 0x0C,
            var value when value.Equals("Frame", StringComparison.OrdinalIgnoreCase) => 0x0E,
            var value when value.Equals("SpinButton", StringComparison.OrdinalIgnoreCase) => 0x10,
            var value when value.Equals("CommandButton", StringComparison.OrdinalIgnoreCase) => 0x11,
            var value when value.Equals("TabStrip", StringComparison.OrdinalIgnoreCase) => 0x12,
            var value when value.Equals("Label", StringComparison.OrdinalIgnoreCase) => 0x15,
            var value when value.Equals("TextBox", StringComparison.OrdinalIgnoreCase) => 0x17,
            var value when value.Equals("ListBox", StringComparison.OrdinalIgnoreCase) => 0x18,
            var value when value.Equals("ComboBox", StringComparison.OrdinalIgnoreCase) => 0x19,
            var value when value.Equals("CheckBox", StringComparison.OrdinalIgnoreCase) => 0x1A,
            var value when value.Equals("OptionButton", StringComparison.OrdinalIgnoreCase) => 0x1B,
            var value when value.Equals("ToggleButton", StringComparison.OrdinalIgnoreCase) => 0x1C,
            var value when value.Equals("ScrollBar", StringComparison.OrdinalIgnoreCase) => 0x2F,
            var value when value.Equals("MultiPage", StringComparison.OrdinalIgnoreCase) => 0x39,
            _ => 0,
        };

        return typeCode != 0;
    }
}
