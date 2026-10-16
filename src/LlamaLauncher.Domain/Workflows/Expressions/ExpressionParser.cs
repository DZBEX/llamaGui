namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>安全表达式的递归下降语法分析器（受限语法，无动态执行）。</summary>
internal sealed class ExpressionParser
{
    private const int MaxDepth = 64;

    private readonly List<ExpressionToken> _tokens;
    private int _index;
    private int _depth;

    private ExpressionParser(List<ExpressionToken> tokens) => _tokens = tokens;

    public static ExpressionNode Parse(string text)
    {
        var tokens = ExpressionLexer.Tokenize(text);
        var parser = new ExpressionParser(tokens);
        var node = parser.ParseExpression();
        parser.Expect(ExpressionTokenKind.End, "表达式末尾存在多余内容。");
        return node;
    }

    private ExpressionToken Current => _tokens[_index];

    private ExpressionToken Advance() => _tokens[_index++];

    private bool MatchOperator(string op)
    {
        if (Current.Kind == ExpressionTokenKind.Operator && Current.Text == op)
        {
            _index++;
            return true;
        }

        return false;
    }

    private bool MatchKeyword(string keyword)
    {
        if (Current.Kind == ExpressionTokenKind.Keyword && Current.Text == keyword)
        {
            _index++;
            return true;
        }

        return false;
    }

    private bool MatchPunctuation(char ch)
    {
        if (Current.Kind == ExpressionTokenKind.Punctuation && Current.Text.Length == 1 && Current.Text[0] == ch)
        {
            _index++;
            return true;
        }

        return false;
    }

    private void Expect(ExpressionTokenKind kind, string message)
    {
        if (Current.Kind != kind)
        {
            throw new WorkflowExpressionException(message, Current.Position);
        }

        _index++;
    }

    private void EnterDepth(int position)
    {
        _depth++;
        if (_depth > MaxDepth)
        {
            throw new WorkflowExpressionException($"表达式嵌套过深（超过 {MaxDepth} 层）。", position);
        }
    }

    private void ExitDepth() => _depth--;

    private ExpressionNode ParseExpression()
    {
        EnterDepth(Current.Position);
        try
        {
            return ParseCoalesce();
        }
        finally
        {
            ExitDepth();
        }
    }

    private ExpressionNode ParseCoalesce()
    {
        var left = ParseOr();
        while (MatchOperator("??"))
        {
            var op = _tokens[_index - 1];
            var right = ParseOr();
            left = new BinaryNode("??", left, right, op.Position);
        }

        return left;
    }

    private ExpressionNode ParseOr()
    {
        var left = ParseAnd();
        while (MatchOperator("||"))
        {
            var op = _tokens[_index - 1];
            var right = ParseAnd();
            left = new BinaryNode("||", left, right, op.Position);
        }

        return left;
    }

    private ExpressionNode ParseAnd()
    {
        var left = ParseEquality();
        while (MatchOperator("&&"))
        {
            var op = _tokens[_index - 1];
            var right = ParseEquality();
            left = new BinaryNode("&&", left, right, op.Position);
        }

        return left;
    }

    private ExpressionNode ParseEquality()
    {
        var left = ParseRelational();
        while (true)
        {
            if (MatchOperator("=="))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("==", left, ParseRelational(), op.Position);
                continue;
            }

            if (MatchOperator("!="))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("!=", left, ParseRelational(), op.Position);
                continue;
            }

            if (MatchKeyword("in"))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("in", left, ParseRelational(), op.Position);
                continue;
            }

            return left;
        }
    }

    private ExpressionNode ParseRelational()
    {
        var left = ParseAdditive();
        while (true)
        {
            string? op = null;
            foreach (var candidate in new[] { "<=", ">=", "<", ">" })
            {
                if (MatchOperator(candidate))
                {
                    op = candidate;
                    break;
                }
            }

            if (op is null)
            {
                return left;
            }

            var token = _tokens[_index - 1];
            left = new BinaryNode(op, left, ParseAdditive(), token.Position);
        }
    }

    private ExpressionNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            if (MatchOperator("+"))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("+", left, ParseMultiplicative(), op.Position);
                continue;
            }

            if (MatchOperator("-"))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("-", left, ParseMultiplicative(), op.Position);
                continue;
            }

            return left;
        }
    }

    private ExpressionNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            if (MatchOperator("*"))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("*", left, ParseUnary(), op.Position);
                continue;
            }

            if (MatchOperator("/"))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("/", left, ParseUnary(), op.Position);
                continue;
            }

            if (MatchOperator("%"))
            {
                var op = _tokens[_index - 1];
                left = new BinaryNode("%", left, ParseUnary(), op.Position);
                continue;
            }

            return left;
        }
    }

    private ExpressionNode ParseUnary()
    {
        if (MatchOperator("!"))
        {
            var op = _tokens[_index - 1];
            EnterDepth(op.Position);
            try
            {
                return new UnaryNode("!", ParseUnary(), op.Position);
            }
            finally
            {
                ExitDepth();
            }
        }

        if (MatchOperator("-"))
        {
            var op = _tokens[_index - 1];
            EnterDepth(op.Position);
            try
            {
                return new UnaryNode("-", ParseUnary(), op.Position);
            }
            finally
            {
                ExitDepth();
            }
        }

        return ParsePostfix();
    }

    private ExpressionNode ParsePostfix()
    {
        var primary = ParsePrimary();
        var segments = new List<PathSegment>();
        if (primary is PathNode pathNode)
        {
            segments.AddRange(pathNode.Segments);
        }

        while (true)
        {
            if (Current.Kind == ExpressionTokenKind.Punctuation && Current.Text == ".")
            {
                var dot = Advance();
                if (Current.Kind != ExpressionTokenKind.Identifier && Current.Kind != ExpressionTokenKind.Keyword)
                {
                    throw new WorkflowExpressionException("“.” 之后需要字段名。", Current.Position);
                }

                var nameToken = Advance();
                segments.Add(new PropertySegment(nameToken.Text, dot.Position));
                continue;
            }

            if (Current.Kind == ExpressionTokenKind.Punctuation && Current.Text == "[")
            {
                var bracket = Advance();
                if (Current.Kind != ExpressionTokenKind.Number)
                {
                    throw new WorkflowExpressionException("索引必须是字面量整数。", Current.Position);
                }

                var numberToken = Advance();
                if (numberToken.Number < 0 || numberToken.Number != Math.Floor(numberToken.Number))
                {
                    throw new WorkflowExpressionException("索引必须是非负整数。", numberToken.Position);
                }

                Expect(ExpressionTokenKind.Punctuation, "缺少“]”。");
                segments.Add(new IndexSegment((int)numberToken.Number, bracket.Position));
                continue;
            }

            break;
        }

        if (segments.Count == 0)
        {
            return primary;
        }

        if (primary is not PathNode)
        {
            // 只有纯路径允许继续取字段（函数/字面量结果只能整体使用，保持语法可预测、可静态校验）。
            throw new WorkflowExpressionException(
                "只有路径可以直接取字段（如 a.b.c）；函数或字面量结果请整体使用，或配合 get()/coalesce() 处理。",
                primary.Position);
        }

        return new PathNode(segments, primary.Position);
    }

    private ExpressionNode ParsePrimary()
    {
        switch (Current.Kind)
        {
            case ExpressionTokenKind.Number:
                return new LiteralNode(Advance().Number, _tokens[_index - 1].Position);

            case ExpressionTokenKind.String:
                return new LiteralNode(Advance().Text, _tokens[_index - 1].Position);

            case ExpressionTokenKind.Keyword:
            {
                var token = Advance();
                return token.Text switch
                {
                    "true" => new LiteralNode(true, token.Position),
                    "false" => new LiteralNode(false, token.Position),
                    "null" => new LiteralNode(null, token.Position),
                    _ => throw new WorkflowExpressionException($"意外的关键字“{token.Text}”。", token.Position),
                };
            }

            case ExpressionTokenKind.Punctuation when Current.Text == "(":
            {
                Advance();
                var expr = ParseExpression();
                if (!MatchPunctuation(')'))
                {
                    throw new WorkflowExpressionException("缺少“)”。", Current.Position);
                }

                return expr;
            }

            case ExpressionTokenKind.Identifier:
            {
                var token = Advance();
                if (Current.Kind == ExpressionTokenKind.Punctuation && Current.Text == "(")
                {
                    Advance();
                    var args = new List<ExpressionNode>();
                    if (!(Current.Kind == ExpressionTokenKind.Punctuation && Current.Text == ")"))
                    {
                        while (true)
                        {
                            args.Add(ParseExpression());
                            if (MatchPunctuation(','))
                            {
                                continue;
                            }

                            break;
                        }
                    }

                    if (!MatchPunctuation(')'))
                    {
                        throw new WorkflowExpressionException("函数调用缺少“)”。", Current.Position);
                    }

                    return new CallNode(token.Text, args, token.Position);
                }

                if (token.Text is "and" or "or" or "not")
                {
                    throw new WorkflowExpressionException($"请使用 && / || / ! 表示逻辑运算，而不是“{token.Text}”。", token.Position);
                }

                return new PathNode(new List<PathSegment> { new PropertySegment(token.Text, token.Position) }, token.Position);
            }

            default:
                throw new WorkflowExpressionException("表达式在此处不完整。", Current.Position);
        }
    }
}
