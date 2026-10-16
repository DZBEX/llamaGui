using System.Text.Json.Nodes;

namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>
/// 表达式与模板的变量作用域（统一数据模型）：
/// input（工作流输入）、vars（工作流变量）、up（上游节点输出）、meta（运行元数据）、error（错误上下文）。
/// </summary>
public static class WorkflowScope
{
    /// <summary>工作流输入根。</summary>
    public const string InputRoot = "input";

    /// <summary>工作流变量根。</summary>
    public const string VariablesRoot = "vars";

    /// <summary>上游节点输出根（up.&lt;nodeId&gt;.&lt;port&gt;...）。</summary>
    public const string UpstreamRoot = "up";

    /// <summary>运行元数据根（runId、workflowId、testMode、startedAt 等）。</summary>
    public const string MetaRoot = "meta";

    /// <summary>错误上下文根（type、message、nodeId、attempts）。</summary>
    public const string ErrorRoot = "error";

    /// <summary>创建空作用域。</summary>
    public static JsonObject CreateEmpty() => new()
    {
        [InputRoot] = new JsonObject(),
        [VariablesRoot] = new JsonObject(),
        [UpstreamRoot] = new JsonObject(),
        [MetaRoot] = new JsonObject(),
    };

    /// <summary>创建作用域（error 为 null 时不包含错误根）。</summary>
    public static JsonObject Create(
        JsonObject inputs,
        JsonObject variables,
        JsonObject upstream,
        JsonObject meta,
        JsonObject? error = null)
    {
        var scope = new JsonObject
        {
            [InputRoot] = inputs.DeepClone(),
            [VariablesRoot] = variables.DeepClone(),
            [UpstreamRoot] = upstream.DeepClone(),
            [MetaRoot] = meta.DeepClone(),
        };
        if (error is not null)
        {
            scope[ErrorRoot] = error.DeepClone();
        }

        return scope;
    }

    /// <summary>从作用域读取输入根。</summary>
    public static JsonObject GetInputs(JsonObject scope)
        => scope[InputRoot] as JsonObject ?? new JsonObject();

    /// <summary>从作用域读取变量根。</summary>
    public static JsonObject GetVariables(JsonObject scope)
        => scope[VariablesRoot] as JsonObject ?? new JsonObject();

    /// <summary>从作用域读取上游输出根。</summary>
    public static JsonObject GetUpstream(JsonObject scope)
        => scope[UpstreamRoot] as JsonObject ?? new JsonObject();
}
