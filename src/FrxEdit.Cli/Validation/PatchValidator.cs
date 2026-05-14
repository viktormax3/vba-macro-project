internal static class PatchValidator
{
    public static void Validate(PatchDocument patch, IReadOnlyList<ControlInfo> controls)
    {
        var known = controls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalNames = controls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (oldName, newName) in patch.Renames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Contains(oldName))
            {
                throw new CliException($"Rename source '{oldName}' does not exist.");
            }

            finalNames.Remove(oldName);
            if (!finalNames.Add(newName))
            {
                throw new CliException($"Rename target '{newName}' would duplicate an existing control.");
            }
        }

        foreach (var name in patch.Layout?.Keys ?? Enumerable.Empty<string>())
        {
            if (known.Contains(name))
            {
                continue;
            }

            if (patch.Renames?.Values.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            throw new CliException($"Layout target '{name}' does not exist.");
        }

        foreach (var name in patch.Properties?.Keys ?? Enumerable.Empty<string>())
        {
            if (known.Contains(name))
            {
                continue;
            }

            if (patch.Renames?.Values.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            throw new CliException($"Properties target '{name}' does not exist.");
        }
    }
}
