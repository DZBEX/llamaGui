using LlamaLauncher.Application.Workflows.Executors;

namespace LlamaLauncher.Application.Workflows;

/// <summary>内置执行器工厂（DI 与测试共用；扩展节点只需在此追加实现）。</summary>
public static class WorkflowExecutors
{
    /// <summary>创建全部内置节点执行器（13 种节点类型）。</summary>
    public static IReadOnlyList<INodeExecutor> CreateDefault() => new INodeExecutor[]
    {
        new StartNodeExecutor(),
        new EndNodeExecutor(),
        new TransformNodeExecutor(),
        new ConditionNodeExecutor(),
        new LoopNodeExecutor(),
        new ParallelNodeExecutor(),
        new JoinNodeExecutor(),
        new ExceptionNodeExecutor(),
        new DecisionNodeExecutor(),
        new RulesNodeExecutor(),
        new AlgorithmNodeExecutor(),
        new ConfirmNodeExecutor(),
        new SubWorkflowNodeExecutor(),
    };
}
