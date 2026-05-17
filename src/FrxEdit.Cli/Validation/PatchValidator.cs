internal static class PatchValidator
{
    public static void Validate(PatchDocument patch, IReadOnlyList<ControlInfo> controls)
    {
        var known = controls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalNames = controls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = (patch.Remove ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in removed)
        {
            if (!known.Contains(name))
            {
                throw new CliException($"Remove target '{name}' does not exist.");
            }

            if (patch.Renames?.ContainsKey(name) == true)
            {
                throw new CliException($"Remove target '{name}' cannot also be renamed.");
            }

            if (patch.Layout?.ContainsKey(name) == true)
            {
                throw new CliException($"Remove target '{name}' cannot also receive a layout patch.");
            }

            if (patch.Properties?.ContainsKey(name) == true)
            {
                throw new CliException($"Remove target '{name}' cannot also receive a properties patch.");
            }
        }

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
            if (removed.Contains(name))
            {
                throw new CliException($"Layout target '{name}' is removed by this patch.");
            }

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
            if (removed.Contains(name))
            {
                throw new CliException($"Properties target '{name}' is removed by this patch.");
            }

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
