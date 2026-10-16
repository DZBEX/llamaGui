using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;
using Xunit;

namespace LlamaLauncher.UnitTests.Workflows;

/// <summary>安全表达式：语法、求值、类型检查与安全边界。</summary>
public sealed class WorkflowExpressionTests
{
    private static JsonObject Scope() => new()
    {
        ["input"] = new JsonObject
        {
            ["message"] = "Hello world",
            ["count"] = 3,
            ["ratio"] = 0.5,
            ["flag"] = true,
            ["tags"] = new JsonArray("a", "b", "c"),
            ["nums"] = new JsonArray(1, 2, 3),
            ["nested"] = new JsonObject { ["deep"] = new JsonArray(new JsonObject { ["value"] = 42 }) },
        },
        ["vars"] = new JsonObject { ["score"] = 10 },
        ["up"] = new JsonObject
        {
            ["n1"] = new JsonObject { ["out"] = new JsonObject { ["confidence"] = 0.9, ["label"] = "billing" } },
        },
        ["meta"] = new JsonObject { ["runId"] = "run-1" },
    };

    private static JsonNode? Eval(string expression, JsonObject? scope = null)
    {
        var parsed = WorkflowExpression.Parse(expression);
        return parsed.EvaluateToJson(scope ?? Scope());
    }

    private static string EvalText(string expression)
        => WorkflowValueConverter.ToDisplayText(WorkflowValueConverter.FromNode(Eval(expression)));

    [Fact]
    public void 支持字面量()
    {
        Assert.Equal("1", EvalText("1"));
        Assert.Equal("abc", EvalText("\"abc\""));
        Assert.Equal("true", EvalText("true"));
        Assert.Equal("null", Eval("null")?.ToJsonString() ?? "null");
    }

    [Fact]
    public void 解析纯路径()
    {
        var expression = WorkflowExpression.Parse("input.message");
        Assert.Equal(new[] { "input", "message" }, expression.PurePath);

        var complex = WorkflowExpression.Parse("len(input.message) > 3");
        Assert.Null(complex.PurePath);
    }

    [Fact]
    public void 路径取值与缺失返回空()
    {
        Assert.Equal("Hello world", EvalText("input.message"));
        Assert.Equal("42", EvalText("input.nested.deep[0].value"));
        Assert.Null(Eval("input.notExist"));
        Assert.Null(Eval("input.notExist.deeper"));
    }

    [Fact]
    public void 比较运算()
    {
        Assert.Equal("true", EvalText("input.count == 3"));
        Assert.Equal("true", EvalText("input.count != 4"));
        Assert.Equal("true", EvalText("input.count < 4"));
        Assert.Equal("true", EvalText("input.count >= 3"));
        Assert.Equal("true", EvalText("input.message == \"Hello world\""));
        Assert.Equal("true", EvalText("input.message < \"Z\""));
        Assert.Equal("true", EvalText("input.notExist == null"));
    }

    [Fact]
    public void 类型不匹配比较报错()
    {
        var ex = Assert.Throws<WorkflowExpressionException>(() => Eval("input.count < \"abc\""));
        Assert.Contains("同为数值或同为字符串", ex.Message);
    }

    [Fact]
    public void 逻辑运算与短路()
    {
        Assert.Equal("true", EvalText("input.flag && input.count == 3"));
        Assert.Equal("true", EvalText("isEmpty(input.notExist)"));
        Assert.Equal("true", EvalText("false || input.flag"));
        // 短路：右侧非法表达式不应求值
        Assert.Equal("false", EvalText("input.flag && input.count == 0 && (1 / 0 == 0)"));
    }

    [Fact]
    public void 逻辑运算需要布尔值()
    {
        var ex = Assert.Throws<WorkflowExpressionException>(() => Eval("input.count && true"));
        Assert.Contains("需要布尔值", ex.Message);
    }

    [Fact]
    public void 空合并运算符()
    {
        Assert.Equal("fallback", EvalText("input.notExist ?? \"fallback\""));
        Assert.Equal("Hello world", EvalText("input.message ?? \"fallback\""));
    }

    [Fact]
    public void in运算符()
    {
        Assert.Equal("true", EvalText("\"b\" in input.tags"));
        Assert.Equal("false", EvalText("\"z\" in input.tags"));
        Assert.Equal("true", EvalText("\"Hello\" in input.message"));
        Assert.Equal("false", EvalText("5 in input.tags"));
    }

    [Fact]
    public void 算术运算与字符串拼接()
    {
        Assert.Equal("4", EvalText("1 + 3"));
        Assert.Equal("-1", EvalText("2 - 3"));
        Assert.Equal("6", EvalText("2 * 3"));
        Assert.Equal("2", EvalText("6 / 3"));
        Assert.Equal("1", EvalText("7 % 3"));
        Assert.Equal("值: 3", EvalText("\"值: \" + input.count"));
        Assert.Equal("3.5", EvalText("input.count + input.ratio"));
    }

    [Fact]
    public void 除零报错()
    {
        var ex = Assert.Throws<WorkflowExpressionException>(() => Eval("input.count / 0"));
        Assert.Contains("除数为 0", ex.Message);
    }

    [Fact]
    public void 白名单函数()
    {
        Assert.Equal("11", EvalText("len(input.message)"));
        Assert.Equal("3", EvalText("count(input.tags)"));
        Assert.Equal("0", EvalText("count(input.notExist)"));
        Assert.Equal("a", EvalText("first(input.tags)"));
        Assert.Equal("c", EvalText("last(input.tags)"));
        Assert.Equal("true", EvalText("isEmpty(input.notExist)"));
        Assert.Equal("hello world", EvalText("lower(input.message)"));
        Assert.Equal("HELLO WORLD", EvalText("upper(input.message)"));
        Assert.Equal("Hello world", EvalText("trim(\"  Hello world  \")"));
        Assert.Equal("b", EvalText("substring(input.tags[1], 0, 1)"));
        Assert.Equal("true", EvalText("contains(input.message, \"world\")"));
        Assert.Equal("true", EvalText("contains(input.tags, \"b\")"));
        Assert.Equal("true", EvalText("startsWith(input.message, \"Hello\")"));
        Assert.Equal("4", EvalText("indexOf(input.message, \"o w\")"));
        Assert.Equal("ab", EvalText("concat(\"a\", \"b\")"));
        Assert.Equal("true", EvalText("matches(input.message, \"^Hello\")"));
        Assert.Equal("3", EvalText("toNumber(\"3\")"));
        Assert.Equal("3", EvalText("toString(input.count)"));
        Assert.Equal("true", EvalText("toBool(\"true\")"));
        Assert.Equal("3", EvalText("abs(-3)"));
        Assert.Equal("1", EvalText("min(3, 1, 2)"));
        Assert.Equal("3", EvalText("max(3, 1, 2)"));
        Assert.Equal("2", EvalText("round(1.6)"));
        Assert.Equal("1.67", EvalText("round(1.6667, 2)"));
        Assert.Equal("1", EvalText("floor(1.6)"));
        Assert.Equal("2", EvalText("ceil(1.2)"));
        Assert.Equal("6", EvalText("sum(input.nums)"));
        Assert.Equal("2", EvalText("avg(input.nums)"));
        Assert.Equal("true", EvalText("isNull(input.notExist)"));
        Assert.Equal("true", EvalText("isNumber(input.count)"));
        Assert.Equal("true", EvalText("isString(input.message)"));
        Assert.Equal("true", EvalText("isArray(input.tags)"));
        Assert.Equal("true", EvalText("isObject(up.n1.out)"));
        Assert.Equal("true", EvalText("isBool(input.flag)"));
        Assert.Equal("x", EvalText("coalesce(null, input.notExist, \"x\")"));
        Assert.Equal("billing", EvalText("get(up.n1.out, \"label\")"));
        Assert.Equal("a", EvalText("get(input.tags, \"0\")"));
        Assert.Equal("fallback", EvalText("get(up.n1.out, \"missing\", \"fallback\")"));
        Assert.Equal("[\"confidence\",\"label\"]", Eval("keys(up.n1.out)")!.ToJsonString());
    }

    [Fact]
    public void 未知函数与语法错误给出可用信息()
    {
        var unknown = Assert.Throws<WorkflowExpressionException>(() => Eval("badFunc(1)"));
        Assert.Contains("不支持的函数", unknown.Message);
        Assert.Contains("len", unknown.Message);

        var missingParen = Assert.Throws<WorkflowExpressionException>(() => Eval("(1 + 2"));
        Assert.Contains("缺少", missingParen.Message);

        var badChar = Assert.Throws<WorkflowExpressionException>(() => Eval("input.message & 1"));
        Assert.Contains("不支持的字符", badChar.Message);

        var trailing = Assert.Throws<WorkflowExpressionException>(() => Eval("1 2"));
        Assert.Contains("多余内容", trailing.Message);
    }

    [Fact]
    public void 非法字符集被拒绝()
    {
        // 不允许调用系统能力或副作用
        Assert.Throws<WorkflowExpressionException>(() => WorkflowExpression.Parse("System.IO.File.Delete(\"x\")"));
        Assert.Throws<WorkflowExpressionException>(() => WorkflowExpression.Parse("input.message; rm -rf /"));
    }

    [Fact]
    public void 嵌套过深被拒绝()
    {
        var deep = new string('(', 80) + "1" + new string(')', 80);
        var ex = Assert.Throws<WorkflowExpressionException>(() => WorkflowExpression.Parse(deep));
        Assert.Contains("嵌套过深", ex.Message);
    }

    [Fact]
    public void 超长表达式被拒绝()
    {
        var longExpression = "1 + " + new string('1', WorkflowLimits.MaxExpressionLength);
        var ex = Assert.Throws<WorkflowExpressionException>(() =>
        {
            WorkflowExpression.Parse(longExpression);
        });
        Assert.Contains("过长", ex.Message);
    }

    [Fact]
    public void 布尔求值接口()
    {
        var expression = WorkflowExpression.Parse("input.count > 2");
        Assert.True(expression.EvaluateBool(Scope()));

        var nonBool = WorkflowExpression.Parse("input.count");
        var ex = Assert.Throws<WorkflowExpressionException>(() => nonBool.EvaluateBool(Scope(), "规则条件"));
        Assert.Contains("规则条件需要布尔值", ex.Message);
    }

    [Fact]
    public void 类型错误包含位置信息()
    {
        var ex = Assert.Throws<WorkflowExpressionException>(() => Eval("1 + len(input.count)"));
        Assert.True(ex.Position >= 0);
        Assert.True(ex.IsRuntime);
    }
}
