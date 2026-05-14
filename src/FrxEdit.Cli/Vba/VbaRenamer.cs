internal static class VbaRenamer
{
    public static string Apply(string source, Dictionary<string, string>? renames)
    {
        if (renames is null || renames.Count == 0)
        {
            return source;
        }

        var result = source;
        foreach (var (oldName, newName) in renames)
        {
            result = Regex.Replace(result, $@"(?<![A-Za-z0-9_]){Regex.Escape(oldName)}(?![A-Za-z0-9])", newName);
        }

        return result;
    }
}
