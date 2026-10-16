using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using Xunit;

namespace LlamaLauncher.UnitTests.Workflows;

/// <summary>受限模板：占位符校验、渲染、缺失与转义。</summary>
public sealed class WorkflowTemplateTests
{
    private static JsonObject Scope() => new()
    {
        ["input"] = new JsonObject { ["message"] = "double charged", ["count"] = 2 },
        ["vars"] = new JsonObject(),
        ["up"] = new JsonObject(),
        ["meta"] = new JsonObject(),
        ["item"] = new JsonObject { ["id"] = "A-1" },
    };

    [Fact]
    public void 渲染基本占位符()
    {
        var text = WorkflowTemplate.Render(
            "客户说：{{ input.message }}（第 {{ input.count }} 次）",
            Scope(),
            out var missing);
        Assert.Equal("客户说：double charged（第 2 次）", text);
        Assert.Empty(missing);
    }

    [Fact]
    public void 非字符串值以紧凑JSON插入()
    {
        var scope = Scope();
        scope["input"]!["obj"] = new JsonObject { ["a"] = 1 };
        var text = WorkflowTemplate.Render("{{ input.obj }}", scope, out _);
        Assert.Equal("{\"a\":1}", text);
    }

    [Fact]
    public void 缺失占位符输出为空并记录()
    {
        var text = WorkflowTemplate.Render("X{{ input.notExist }}Y", Scope(), out var missing);
        Assert.Equal("XY", text);
        Assert.Equal(new[] { "input.notExist" }, missing);
    }

    [Fact]
    public void 校验输出占位符路径()
    {
        var error = WorkflowTemplate.Validate("a {{ input.message }} b {{ vars.x }}", out var paths);
        Assert.Null(error);
        Assert.Equal(new[] { "input.message", "vars.x" }, paths);
    }

    [Fact]
    public void 拒绝运算与函数()
    {
        var error = WorkflowTemplate.Validate("{{ len(input.message) }}", out _);
        Assert.NotNull(error);
        Assert.Contains("只支持字段路径", error);

        var error2 = WorkflowTemplate.Validate("{{ input.count + 1 }}", out _);
        Assert.NotNull(error2);
    }

    [Fact]
    public void 拒绝未闭合占位符()
    {
        var error = WorkflowTemplate.Validate("{{ input.message", out _);
        Assert.NotNull(error);
        Assert.Contains("缺少", error);
    }

    [Fact]
    public void 转义字面量()
    {
        var text = WorkflowTemplate.Render(@"价格：\{{ 100 }}", Scope(), out _);
        Assert.Equal("价格：{{ 100 }}", text);
    }
}
