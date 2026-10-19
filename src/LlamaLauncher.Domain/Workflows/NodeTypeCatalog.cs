namespace LlamaLauncher.Domain.Workflows;

/// <summary>节点端口描述。</summary>
public sealed class NodePortDescriptor
{
    public NodePortDescriptor(string name, string displayName, bool isBranch = false)
    {
        Name = name;
        DisplayName = displayName;
        IsBranch = isBranch;
    }

    /// <summary>端口名（连线 SourcePort/TargetPort 对应值）。</summary>
    public string Name { get; }

    /// <summary>显示名称。</summary>
    public string DisplayName { get; }

    /// <summary>是否为分支选择端口（出边需带 BranchId）。</summary>
    public bool IsBranch { get; }
}

/// <summary>节点类型元数据（节点库、校验器、执行器注册共用；类型 ID 稳定且唯一）。</summary>
public sealed class NodeTypeDescriptor
{
    public NodeTypeDescriptor(
        string typeId,
        string displayName,
        string category,
        string description,
        IReadOnlyList<NodePortDescriptor> inputPorts,
        IReadOnlyList<NodePortDescriptor> outputPorts,
        Func<object> defaultConfigFactory,
        bool hasDynamicBranches = false,
        bool supportsErrorPort = false,
        int version = 1)
    {
        TypeId = typeId;
        DisplayName = displayName;
        Category = category;
        Description = description;
        InputPorts = inputPorts;
        OutputPorts = outputPorts;
        DefaultConfigFactory = defaultConfigFactory;
        HasDynamicBranches = hasDynamicBranches;
        SupportsErrorPort = supportsErrorPort;
        Version = version;
    }

    /// <summary>稳定类型 ID（如 core.start）。</summary>
    public string TypeId { get; }

    /// <summary>类型版本。</summary>
    public int Version { get; }

    /// <summary>显示名称。</summary>
    public string DisplayName { get; }

    /// <summary>分类（节点库分组）。</summary>
    public string Category { get; }

    /// <summary>说明。</summary>
    public string Description { get; }

    /// <summary>输入端口。</summary>
    public IReadOnlyList<NodePortDescriptor> InputPorts { get; }

    /// <summary>输出端口（不含动态分支）。</summary>
    public IReadOnlyList<NodePortDescriptor> OutputPorts { get; }

    /// <summary>默认配置工厂。</summary>
    public Func<object> DefaultConfigFactory { get; }

    /// <summary>出端口是否由配置动态决定（条件/并行/规则/人工节点）。</summary>
    public bool HasDynamicBranches { get; }

    /// <summary>是否支持 error 出口（失败策略为错误分支时使用）。</summary>
    public bool SupportsErrorPort { get; }
}

/// <summary>内置节点类型目录（第一版全部 13 种节点）。</summary>
public static class NodeTypeCatalog
{
    public const string Start = "core.start";
    public const string End = "core.end";
    public const string Transform = "data.transform";
    public const string Condition = "flow.condition";
    public const string Loop = "flow.loop";
    public const string Parallel = "flow.parallel";
    public const string Join = "flow.join";
    public const string Exception = "flow.exception";
    public const string Decision = "model.decision";
    public const string Rules = "rules.check";
    public const string Algorithm = "algorithm.solve";
    public const string Confirm = "interact.confirm";
    public const string SubWorkflow = "compose.subworkflow";

    /// <summary>错误出口端口名（所有支持错误出口的节点共用）。</summary>
    public const string ErrorPort = "error";

    /// <summary>默认输入端口名。</summary>
    public const string DefaultInputPort = "in";

    /// <summary>默认输出端口名。</summary>
    public const string DefaultOutputPort = "out";

    private static readonly IReadOnlyList<NodePortDescriptor> NoPorts = Array.Empty<NodePortDescriptor>();
    private static readonly IReadOnlyList<NodePortDescriptor> InputOnly = new[] { new NodePortDescriptor(DefaultInputPort, "输入") };
    private static readonly IReadOnlyList<NodePortDescriptor> OutputOnly = new[] { new NodePortDescriptor(DefaultOutputPort, "输出") };
    private static readonly IReadOnlyList<NodePortDescriptor> InputOutput = new[]
    {
        new NodePortDescriptor(DefaultInputPort, "输入"),
    };

    /// <summary>全部内置节点类型（按分类排序后的固定顺序）。</summary>
    public static IReadOnlyList<NodeTypeDescriptor> All { get; } = new[]
    {
        new NodeTypeDescriptor(
            Start,
            "开始",
            "基础",
            "工作流入口：定义并校验输入变量，记录输入快照。",
            NoPorts,
            OutputOnly,
            () => new StartNodeConfig()),
        new NodeTypeDescriptor(
            End,
            "结束",
            "基础",
            "工作流出口：确定最终状态并映射输出。",
            InputOnly,
            NoPorts,
            () => new EndNodeConfig()),
        new NodeTypeDescriptor(
            Transform,
            "数据处理",
            "数据处理",
            "受控的数据处理管线：字段选择/重命名、转换、数组过滤映射、对象组装等。",
            InputOnly,
            OutputOnly,
            () => new TransformNodeConfig(),
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Condition,
            "条件分支",
            "控制流",
            "按受限安全表达式选择命名分支（含默认分支）。",
            InputOnly,
            NoPorts,
            () => new ConditionNodeConfig
            {
                Branches = new List<ConditionBranch>
                {
                    new() { Id = "yes", Name = "是" },
                    new() { Id = "default", Name = "默认", IsDefault = true },
                },
            },
            hasDynamicBranches: true,
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Loop,
            "循环",
            "控制流",
            "对数组逐项执行子工作流或决策模型模板；有最大迭代与超时限制。",
            InputOnly,
            OutputOnly,
            () => new LoopNodeConfig(),
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Parallel,
            "并行扇出",
            "控制流",
            "把执行流分成多个并行分支（受最大并发限制）。",
            InputOnly,
            NoPorts,
            () => new ParallelNodeConfig
            {
                Branches = new List<ParallelBranch>
                {
                    new() { Id = "b1", Name = "分支 1" },
                    new() { Id = "b2", Name = "分支 2" },
                },
            },
            hasDynamicBranches: true,
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Join,
            "并行汇合",
            "控制流",
            "等待多个并行分支到达后汇合（全部完成 / 收集错误）。",
            InputOnly,
            OutputOnly,
            () => new JoinNodeConfig(),
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Exception,
            "异常处理",
            "控制流",
            "接收上游节点异常上下文（类型/信息/节点），记录或重新抛出。",
            new[] { new NodePortDescriptor(ErrorPort, "错误") },
            OutputOnly,
            () => new ExceptionNodeConfig()),
        new NodeTypeDescriptor(
            Decision,
            "决策模型",
            "模型",
            "调用本地 llama-server 的 /v1/systemone 决策接口（复用启动器实例管理，不另起进程）。",
            InputOnly,
            OutputOnly,
            () => new DecisionNodeConfig(),
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Rules,
            "规则校验",
            "校验",
            "按多条规则校验数据，区分硬错误与警告，可停止/走 fail 分支/继续。",
            InputOnly,
            OutputOnly,
            () => new RulesNodeConfig
            {
                Rules = new List<RuleDefinition>
                {
                    new() { Id = "r1", Name = "规则 1", Condition = "true" },
                },
            },
            hasDynamicBranches: true,
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Algorithm,
            "算法求解",
            "算法",
            "调用已注册的求解器/业务算法（含内置示例求解器；接口可扩展）。",
            InputOnly,
            OutputOnly,
            () => new AlgorithmNodeConfig(),
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            Confirm,
            "人工确认",
            "交互",
            "暂停工作流等待人工批准/拒绝，可填写指定字段。",
            InputOnly,
            NoPorts,
            () => new ManualConfirmNodeConfig(),
            hasDynamicBranches: true,
            supportsErrorPort: true),
        new NodeTypeDescriptor(
            SubWorkflow,
            "子工作流",
            "组合",
            "调用已保存的其他工作流（含输入输出映射与递归保护）。",
            InputOnly,
            OutputOnly,
            () => new SubWorkflowNodeConfig(),
            supportsErrorPort: true),
    };

    /// <summary>分类顺序（节点库分组显示）。</summary>
    public static IReadOnlyList<string> Categories { get; } = new[]
    {
        "基础", "数据处理", "控制流", "模型", "校验", "算法", "交互", "组合",
    };

    /// <summary>按类型 ID 查找描述。</summary>
    public static NodeTypeDescriptor? Find(string? typeId)
        => string.IsNullOrEmpty(typeId) ? null : All.FirstOrDefault(d => d.TypeId == typeId);

    /// <summary>创建默认配置实例。</summary>
    public static object CreateDefaultConfig(string typeId)
        => Find(typeId)?.DefaultConfigFactory() ?? new object();

    /// <summary>创建带默认值的新节点。</summary>
    public static WorkflowNode CreateNode(string typeId, string id, double x, double y, string? name = null)
    {
        var descriptor = Find(typeId);
        var resolvedName = name
            ?? (descriptor is null ? "未知节点" : descriptor.DisplayName);
        return new WorkflowNode
        {
            Id = id,
            TypeId = typeId,
            TypeVersion = descriptor?.Version ?? 1,
            Name = resolvedName,
            X = x,
            Y = y,
            Config = WorkflowJson.ToConfigObject(CreateDefaultConfig(typeId)),
        };
    }
}
