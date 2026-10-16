namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>表达式 AST 节点。</summary>
internal abstract class ExpressionNode
{
    protected ExpressionNode(int position) => Position = position;

    public int Position { get; }
}

/// <summary>字面量。</summary>
internal sealed class LiteralNode : ExpressionNode
{
    public LiteralNode(object? value, int position)
        : base(position)
        => Value = value;

    public object? Value { get; }
}

/// <summary>路径段（属性或索引）。</summary>
internal abstract class PathSegment
{
    protected PathSegment(int position) => Position = position;

    public int Position { get; }
}

/// <summary>属性段。</summary>
internal sealed class PropertySegment : PathSegment
{
    public PropertySegment(string name, int position)
        : base(position)
        => Name = name;

    public string Name { get; }
}

/// <summary>字面量整数索引段。</summary>
internal sealed class IndexSegment : PathSegment
{
    public IndexSegment(int index, int position)
        : base(position)
        => Index = index;

    public int Index { get; }
}

/// <summary>路径取值（如 input.message、up.node1.out.intent、vars.x）。</summary>
internal sealed class PathNode : ExpressionNode
{
    public PathNode(List<PathSegment> segments, int position)
        : base(position)
        => Segments = segments;

    public List<PathSegment> Segments { get; }
}

/// <summary>一元运算。</summary>
internal sealed class UnaryNode : ExpressionNode
{
    public UnaryNode(string op, ExpressionNode operand, int position)
        : base(position)
    {
        Op = op;
        Operand = operand;
    }

    public string Op { get; }

    public ExpressionNode Operand { get; }
}

/// <summary>二元运算。</summary>
internal sealed class BinaryNode : ExpressionNode
{
    public BinaryNode(string op, ExpressionNode left, ExpressionNode right, int position)
        : base(position)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    public string Op { get; }

    public ExpressionNode Left { get; }

    public ExpressionNode Right { get; }
}

/// <summary>白名单函数调用。</summary>
internal sealed class CallNode : ExpressionNode
{
    public CallNode(string name, List<ExpressionNode> args, int position)
        : base(position)
    {
        Name = name;
        Args = args;
    }

    public string Name { get; }

    public List<ExpressionNode> Args { get; }
}
