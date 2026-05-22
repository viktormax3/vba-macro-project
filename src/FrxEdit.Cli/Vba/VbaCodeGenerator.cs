using System.Text.RegularExpressions;

internal static class VbaCodeGenerator
{
    private const string TabStripPanelsStart = "' <frxedit:tabstrip-panels>";
    private const string TabStripPanelsEnd = "' </frxedit:tabstrip-panels>";

    private static readonly Regex IdentifierRegex = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    public static string Apply(string frmText, CodePatch? code)
    {
        if (code?.TabStripPanels is null || code.TabStripPanels.Count == 0)
        {
            return frmText;
        }

        return ApplyTabStripPanels(frmText, code.TabStripPanels);
    }

    public static void Validate(CodePatch? code, IReadOnlyList<ControlInfo> controls)
    {
        if (code?.TabStripPanels is null || code.TabStripPanels.Count == 0)
        {
            return;
        }

        var byName = controls.ToDictionary(control => control.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (tabStripName, panels) in code.TabStripPanels)
        {
            ValidateIdentifier(tabStripName, "TabStrip");
            if (!byName.TryGetValue(tabStripName, out var tabStrip))
            {
                throw new CliException($"Code tabStripPanels target '{tabStripName}' does not exist.");
            }

            if (!tabStrip.Type.Equals("TabStrip", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Code tabStripPanels target '{tabStripName}' is type '{tabStrip.Type}', not TabStrip.");
            }

            if (panels.Count == 0)
            {
                throw new CliException($"Code tabStripPanels target '{tabStripName}' must list at least one panel.");
            }

            foreach (var panelName in panels)
            {
                ValidateIdentifier(panelName, "panel");
                if (!byName.TryGetValue(panelName, out var panel))
                {
                    throw new CliException($"Code tabStripPanels panel '{panelName}' for '{tabStripName}' does not exist.");
                }

                if (!panel.Type.Equals("Frame", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliException($"Code tabStripPanels panel '{panelName}' for '{tabStripName}' is type '{panel.Type}', not Frame.");
                }
            }
        }
    }

    private static string ApplyTabStripPanels(string frmText, IReadOnlyDictionary<string, List<string>> tabStripPanels)
    {
        var stripped = RemoveGeneratedBlock(frmText);
        var generated = BuildTabStripPanelsBlock(tabStripPanels);
        EnsureNoConflictingProcedure(stripped, "UserForm_Initialize");
        foreach (var tabStripName in tabStripPanels.Keys)
        {
            EnsureNoConflictingProcedure(stripped, $"{tabStripName}_Change");
        }

        return EnsureTrailingNewLine(stripped) + Environment.NewLine + generated;
    }

    private static string RemoveGeneratedBlock(string frmText)
    {
        var pattern = Regex.Escape(TabStripPanelsStart) + ".*?" + Regex.Escape(TabStripPanelsEnd) + @"\s*";
        return Regex.Replace(frmText, pattern, string.Empty, RegexOptions.Singleline);
    }

    private static string BuildTabStripPanelsBlock(IReadOnlyDictionary<string, List<string>> tabStripPanels)
    {
        using var output = new StringWriter();
        output.WriteLine(TabStripPanelsStart);
        output.WriteLine("Private Sub UserForm_Initialize()");
        foreach (var tabStripName in tabStripPanels.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"    FrxEdit_Update{tabStripName}Panels");
        }

        output.WriteLine("End Sub");
        output.WriteLine();

        foreach (var (tabStripName, panels) in tabStripPanels.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"Private Sub {tabStripName}_Change()");
            output.WriteLine($"    FrxEdit_Update{tabStripName}Panels");
            output.WriteLine("End Sub");
            output.WriteLine();
            output.WriteLine($"Private Sub FrxEdit_Update{tabStripName}Panels()");
            output.WriteLine($"    Select Case {tabStripName}.Value");
            for (var i = 0; i < panels.Count; i++)
            {
                output.WriteLine($"        Case {i}");
                foreach (var panel in panels)
                {
                    output.WriteLine($"            {panel}.Visible = {VbaBool(panel.Equals(panels[i], StringComparison.OrdinalIgnoreCase))}");
                }
            }

            output.WriteLine("        Case Else");
            for (var i = 0; i < panels.Count; i++)
            {
                output.WriteLine($"            {panels[i]}.Visible = {VbaBool(i == 0)}");
            }

            output.WriteLine("    End Select");
            output.WriteLine("End Sub");
            output.WriteLine();
        }

        output.WriteLine(TabStripPanelsEnd);
        return output.ToString();
    }

    private static string EnsureTrailingNewLine(string value) =>
        value.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? value.TrimEnd('\r', '\n') + Environment.NewLine
            : value + Environment.NewLine;

    private static void EnsureNoConflictingProcedure(string frmText, string procedureName)
    {
        var regex = new Regex(
            @"^\s*Private\s+Sub\s+" + Regex.Escape(procedureName) + @"\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (regex.IsMatch(frmText))
        {
            throw new CliException($"Cannot generate tabStripPanels code: '{procedureName}' already exists outside the frxedit generated block.");
        }
    }

    private static void ValidateIdentifier(string value, string label)
    {
        if (!IdentifierRegex.IsMatch(value))
        {
            throw new CliException($"Invalid VBA identifier for {label}: '{value}'.");
        }
    }

    private static string VbaBool(bool value) => value ? "True" : "False";
}
