namespace LlamaLauncher.Application.CommandLine;

/// <summary>按 Windows 命令行解析规则把参数字符串拆分为 token 列表。</summary>
public static class WindowsArgumentParser
{
    public static List<string> Parse(string? commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(commandLine))
        {
            return tokens;
        }

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;

        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var backslashCount = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashCount++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashCount / 2);
                    if (backslashCount % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        hasToken = true;
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', backslashCount);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                i++;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken || current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                i++;
                continue;
            }

            current.Append(c);
            hasToken = true;
            i++;
        }

        if (hasToken || current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

/// <summary>Windows 风格命令行引号转义（仅用于可读预览，执行一律使用 argv）。</summary>
public static class WindowsCommandLine
{
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
        {
            return argument;
        }

        var result = new System.Text.StringBuilder();
        result.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
            }
            else
            {
                result.Append('\\', backslashes);
                result.Append(c);
                backslashes = 0;
            }
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    public static string BuildPreview(string executablePath, IEnumerable<string> arguments)
    {
        var tokens = new List<string> { QuoteArgument(executablePath) };
        tokens.AddRange(arguments.Select(QuoteArgument));
        return string.Join(' ', tokens);
    }
}
