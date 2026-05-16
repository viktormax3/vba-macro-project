internal sealed class CommandLine
{
    public required List<string> Positionals { get; init; }
    public required Dictionary<string, string> Options { get; init; }

    public static CommandLine Parse(string[] args, int minPositionals, int maxPositionals)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var key = arg[2..];
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new CliException("Invalid empty option.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliException($"Option '--{key}' requires a value.");
            }

            options[key] = args[++i];
        }

        if (positionals.Count < minPositionals || positionals.Count > maxPositionals)
        {
            throw new CliException($"Expected {minPositionals} positional argument(s), got {positionals.Count}.");
        }

        return new CommandLine { Positionals = positionals, Options = options };
    }

    public string? GetOption(string name) => Options.TryGetValue(name, out var value) ? value : null;

    public string RequireOption(string name) =>
        GetOption(name) ?? throw new CliException($"Missing required option '--{name}'.");

    public ParserMode GetParserModeOption(string name, ParserMode defaultValue)
    {
        var value = GetOption(name);
        if (value is null)
        {
            return defaultValue;
        }

        return value.ToLowerInvariant() switch
        {
            "tolerant" => ParserMode.Tolerant,
            "strict" => ParserMode.Strict,
            "legacy" => ParserMode.Legacy,
            _ => throw new CliException($"Option '--{name}' must be one of: tolerant, strict, legacy.")
        };
    }

    public int GetIntOption(string name, int defaultValue)
    {
        var value = GetOption(name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new CliException($"Option '--{name}' must be a non-negative integer.");
        }

        return parsed;
    }
}
