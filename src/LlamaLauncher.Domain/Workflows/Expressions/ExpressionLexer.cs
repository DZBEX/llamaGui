namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>表达式词法单元类型。</summary>
internal enum ExpressionTokenKind
{
    End = 0,
    Number = 1,
    String = 2,
    Identifier = 3,
    Keyword = 4,
    Punctuation = 5,
    Operator = 6,
}

/// <summary>表达式词法单元。</summary>
internal readonly record struct ExpressionToken(
    ExpressionTokenKind Kind,
    string Text,
    double Number,
    int Position);

/// <summary>安全表达式的词法分析器（只支持受控字符集）。</summary>
internal static class ExpressionLexer
{
    private static readonly string[] Operators =
    {
        "==", "!=", "<=", ">=", "&&", "||", "??",
        "<", ">", "!", "+", "-", "*", "/", "%",
    };

    public static List<ExpressionToken> Tokenize(string text)
    {
        var tokens = new List<ExpressionToken>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (char.IsDigit(ch) || (ch == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                tokens.Add(ReadNumber(text, ref i));
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                tokens.Add(ReadString(text, ref i));
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                var word = text[start..i];
                var kind = word is "true" or "false" or "null" or "in"
                    ? ExpressionTokenKind.Keyword
                    : ExpressionTokenKind.Identifier;
                tokens.Add(new ExpressionToken(kind, word, 0, start));
                continue;
            }

            if (ch is '(' or ')' or '[' or ']' or '.' or ',')
            {
                tokens.Add(new ExpressionToken(ExpressionTokenKind.Punctuation, ch.ToString(), 0, i));
                i++;
                continue;
            }

            var matched = false;
            foreach (var op in Operators)
            {
                if (i + op.Length <= text.Length && text.AsSpan(i, op.Length).SequenceEqual(op))
                {
                    tokens.Add(new ExpressionToken(ExpressionTokenKind.Operator, op, 0, i));
                    i += op.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                throw new WorkflowExpressionException($"不支持的字符“{ch}”。仅允许比较、逻辑、数值、字符串与白名单函数。", i);
            }
        }

        tokens.Add(new ExpressionToken(ExpressionTokenKind.End, string.Empty, 0, text.Length));
        return tokens;
    }

    private static ExpressionToken ReadNumber(string text, ref int i)
    {
        var start = i;
        var seenDot = false;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsDigit(ch))
            {
                i++;
                continue;
            }

            if (ch == '.' && !seenDot)
            {
                seenDot = true;
                i++;
                continue;
            }

            break;
        }

        var span = text[start..i];
        if (!double.TryParse(span, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new WorkflowExpressionException($"无法解析数值“{span}”。", start);
        }

        return new ExpressionToken(ExpressionTokenKind.Number, span, value, start);
    }

    private static ExpressionToken ReadString(string text, ref int i)
    {
        var quote = text[i];
        var start = i;
        i++;
        var builder = new System.Text.StringBuilder();
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                builder.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    _ => next,
                });
                i += 2;
                continue;
            }

            if (ch == quote)
            {
                i++;
                return new ExpressionToken(ExpressionTokenKind.String, builder.ToString(), 0, start);
            }

            builder.Append(ch);
            i++;
        }

        throw new WorkflowExpressionException("字符串缺少结束引号。", start);
    }
}
