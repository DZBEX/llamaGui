using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows;

/// <summary>
/// 工作流校验器：结构、连线、可达性、分支、映射、配置与子流程递归。
/// 返回错误/警告/信息三级结果，供设计器与运行前检查使用。
/// </summary>
public sealed class WorkflowValidator
{
    private readonly IReadOnlyDictionary<string, INodeExecutor> _executors;

    public WorkflowValidator(IEnumerable<INodeExecutor> executors)
        => _executors = executors.ToDictionary(e => e.TypeId, StringComparer.Ordinal);

    /// <summary>执行完整校验。</summary>
    public WorkflowValidationReport Validate(WorkflowDefinition definition, WorkflowValidationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var effectiveContext = CopyContext(context, definition);
        var issues = new List<WorkflowIssue>();

        ValidateMetadata(definition, issues);
        ValidateVariables(definition, issues);
        var descriptors = ValidateNodes(definition, issues);
        ValidateEdges(definition, descriptors, issues);
        ValidateGraph(definition, issues);
        ValidateNodesDeep(definition, effectiveContext, issues);
        ValidateSubWorkflowRecursion(definition, effectiveContext, issues);

        return new WorkflowValidationReport(issues);
    }

    /// <summary>仅校验单个节点配置（属性面板实时提示用）。</summary>
    public IReadOnlyList<WorkflowIssue> ValidateNode(WorkflowDefinition definition, WorkflowNode node)
    {
        var issues = new List<WorkflowIssue>();
        ValidateNodeBase(definition, node, issues);
        if (NodeTypeCatalog.Find(node.TypeId) is null)
        {
            return issues;
        }

        if (_executors.TryGetValue(node.TypeId, out var executor))
        {
            issues.AddRange(executor.ValidateConfig(node, CopyContext(null, definition)));
        }

        CheckMappings(definition, node, node.InputMappings, "输入映射", issues);
        return issues;
    }

    private static WorkflowValidationContext CopyContext(WorkflowValidationContext? source, WorkflowDefinition definition)
        => new()
        {
            Definition = definition,
            WorkflowExists = source?.WorkflowExists,
            WorkflowLoader = source?.WorkflowLoader,
            Models = source?.Models,
            AlgorithmKeys = source?.AlgorithmKeys,
            ForRun = source?.ForRun ?? false,
        };

    private static void ValidateMetadata(WorkflowDefinition definition, List<WorkflowIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-030", "工作流名称不能为空。"));
        }

        if (definition.SchemaVersion > WorkflowDefinition.CurrentSchemaVersion)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-SCHEMA-001",
                $"schema 版本 {definition.SchemaVersion} 高于支持的 {WorkflowDefinition.CurrentSchemaVersion}。"));
        }

        var defaults = definition.Defaults;
        if (defaults.MaxNodeParallelism is < 1 or > 8)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-031", "最大节点并发数需在 1–8 之间。"));
        }

        if (defaults.DefaultNodeTimeoutSeconds is < 1 or > 86_400)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-032", "默认节点超时需在 1–86400 秒之间。"));
        }

        if (defaults.MaxRunSeconds is < 1 or > 86_400)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-033", "最大运行时长需在 1–86400 秒之间。"));
        }

        if (defaults.MaxLoopIterations is < 1 or > 1_000)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-034", "默认循环最大迭代次数需在 1–1000 之间。"));
        }
    }

    private static void ValidateVariables(WorkflowDefinition definition, List<WorkflowIssue> issues)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in definition.Inputs)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-035", "工作流输入存在空名称。"));
                continue;
            }

            if (!names.Add(variable.Name))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-036",
                    $"工作流输入“{variable.Name}”重复定义。"));
            }

            if (variable.DefaultValueJson is not null && WorkflowJson.TryParse(variable.DefaultValueJson, out var error) is null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-037",
                    $"输入“{variable.Name}”的默认值不是有效 JSON：{error}"));
            }
        }

        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in definition.Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.Name))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-038", "工作流输出存在空名称。"));
            }
            else if (!outputNames.Add(output.Name))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, "WF-VAL-039", $"工作流输出“{output.Name}”重复定义。"));
            }
        }
    }

    private static Dictionary<string, NodeTypeDescriptor?> ValidateNodes(
        WorkflowDefinition definition,
        List<WorkflowIssue> issues)
    {
        var descriptors = new Dictionary<string, NodeTypeDescriptor?>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-040", $"节点“{node.Name}”缺少 ID。"));
            }
            else if (!ids.Add(node.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-004", $"节点 ID“{node.Id}”重复。", node.Id));
            }

            var descriptor = NodeTypeCatalog.Find(node.TypeId);
            descriptors[node.Id] = descriptor;
            if (descriptor is null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-NODE-UNKNOWN",
                    $"节点“{node.Name}”使用了未知类型“{node.TypeId}”。",
                    node.Id,
                    hint: "安装提供该类型的扩展，或替换为受支持的节点。"));
                continue;
            }

            if (node.TypeVersion > descriptor.Version)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF-NODE-VERSION",
                    $"节点“{node.Name}”的类型版本（{node.TypeVersion}）高于当前支持版本（{descriptor.Version}）。",
                    node.Id));
            }

            if (string.IsNullOrWhiteSpace(node.Name))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, "WF-VAL-041", $"节点“{node.Id}”没有显示名称。", node.Id));
            }
        }

        return descriptors;
    }

    private static void ValidateEdges(
        WorkflowDefinition definition,
        Dictionary<string, NodeTypeDescriptor?> descriptors,
        List<WorkflowIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in definition.Edges)
        {
            if (!string.IsNullOrWhiteSpace(edge.Id) && !ids.Add(edge.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-042", $"连线 ID“{edge.Id}”重复。", edgeId: edge.Id));
            }

            var source = definition.FindNode(edge.SourceNodeId);
            var target = definition.FindNode(edge.TargetNodeId);
            if (source is null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-005",
                    $"连线 {edge.Id} 的源节点“{edge.SourceNodeId}”不存在。",
                    edgeId: edge.Id));
                continue;
            }

            if (target is null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-005",
                    $"连线 {edge.Id} 的目标节点“{edge.TargetNodeId}”不存在。",
                    edgeId: edge.Id));
                continue;
            }

            if (edge.SourceNodeId == edge.TargetNodeId)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-043",
                    $"节点“{source.Name}”存在自环连线。",
                    source.Id,
                    edge.Id));
            }

            var signature = $"{edge.SourceNodeId}|{edge.SourcePort}|{edge.TargetNodeId}|{edge.TargetPort}|{edge.BranchId}";
            if (!signatures.Add(signature))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-007",
                    $"存在重复连线（{source.Name} → {target.Name}）。",
                    source.Id,
                    edge.Id));
            }

            var sourceDescriptor = descriptors.GetValueOrDefault(source.Id);
            if (sourceDescriptor is not null)
            {
                var knownPort = string.Equals(edge.SourcePort, NodeTypeCatalog.DefaultOutputPort, StringComparison.Ordinal)
                    || string.Equals(edge.SourcePort, NodeTypeCatalog.ErrorPort, StringComparison.Ordinal)
                    || sourceDescriptor.OutputPorts.Any(p => p.Name == edge.SourcePort)
                    || (sourceDescriptor.HasDynamicBranches && !string.IsNullOrWhiteSpace(edge.BranchId));
                if (!knownPort)
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-008",
                        $"连线 {edge.Id} 使用了节点“{source.Name}”上不存在的输出端口“{edge.SourcePort}”。",
                        source.Id,
                        edge.Id));
                }
            }

            var targetDescriptor = descriptors.GetValueOrDefault(target.Id);
            if (targetDescriptor is not null)
            {
                var knownTarget = string.Equals(edge.TargetPort, NodeTypeCatalog.DefaultInputPort, StringComparison.Ordinal)
                    || string.Equals(edge.TargetPort, NodeTypeCatalog.ErrorPort, StringComparison.Ordinal)
                    || targetDescriptor.InputPorts.Any(p => p.Name == edge.TargetPort);
                if (!knownTarget)
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-008",
                        $"连线 {edge.Id} 使用了节点“{target.Name}”上不存在的输入端口“{edge.TargetPort}”。",
                        target.Id,
                        edge.Id));
                }
            }
        }
    }

    private static void ValidateGraph(WorkflowDefinition definition, List<WorkflowIssue> issues)
    {
        var startNodes = definition.Nodes.Where(n => n.TypeId == NodeTypeCatalog.Start).ToList();
        if (startNodes.Count == 0)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-001", "工作流缺少开始节点。"));
        }
        else if (startNodes.Count > 1)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-002",
                $"存在 {startNodes.Count} 个开始节点，只允许一个。",
                startNodes[0].Id));
        }

        foreach (var start in startNodes)
        {
            if (definition.IncomingEdges(start.Id).Any())
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-044", "开始节点不允许有入边。", start.Id));
            }
        }

        var endNodes = definition.Nodes.Where(n => n.TypeId == NodeTypeCatalog.End).ToList();
        if (endNodes.Count == 0)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-003", "工作流缺少结束节点，无法确定输出与最终状态。"));
        }

        foreach (var end in endNodes)
        {
            if (!definition.IncomingEdges(end.Id).Any())
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, "WF-VAL-024", "结束节点没有任何入边，永远不会执行。", end.Id));
            }
        }

        // 环路检测（Kahn）：工作流有向图必须是无环图，循环必须通过循环节点（子工作流）表达。
        var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            // 重复 ID 已在前面的检查中报错；此处按最后一个定义计算，避免校验过程本身崩溃。
            indegree[node.Id] = 0;
        }
        foreach (var edge in definition.Edges)
        {
            if (indegree.ContainsKey(edge.TargetNodeId) && definition.FindNode(edge.SourceNodeId) is not null)
            {
                indegree[edge.TargetNodeId]++;
            }
        }

        var queue = new Queue<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id))
            {
                continue;
            }

            foreach (var edge in definition.OutgoingEdges(id))
            {
                if (!indegree.ContainsKey(edge.TargetNodeId))
                {
                    continue;
                }

                if (--indegree[edge.TargetNodeId] == 0)
                {
                    queue.Enqueue(edge.TargetNodeId);
                }
            }
        }

        var cycleNodes = definition.Nodes.Where(n => !visited.Contains(n.Id)).ToList();
        if (cycleNodes.Count > 0)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-023",
                $"节点图中存在环路（涉及 {string.Join("、", cycleNodes.Select(n => n.Name).Take(5))}），请改用循环节点表达重复执行。",
                cycleNodes[0].Id));
        }

        // 可达性：从开始节点 BFS。
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        if (startNodes.Count > 0)
        {
            var bfs = new Queue<string>();
            bfs.Enqueue(startNodes[0].Id);
            reachable.Add(startNodes[0].Id);
            while (bfs.Count > 0)
            {
                var id = bfs.Dequeue();
                foreach (var edge in definition.OutgoingEdges(id))
                {
                    if (definition.FindNode(edge.TargetNodeId) is not null && reachable.Add(edge.TargetNodeId))
                    {
                        bfs.Enqueue(edge.TargetNodeId);
                    }
                }
            }
        }

        foreach (var node in definition.Nodes)
        {
            var hasEdges = definition.IncomingEdges(node.Id).Any() || definition.OutgoingEdges(node.Id).Any();
            if (!hasEdges && node.TypeId != NodeTypeCatalog.Start)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF-VAL-006",
                    $"节点“{node.Name}”没有任何连线（孤立节点），不会参与运行。",
                    node.Id));
            }
            else if (startNodes.Count > 0 && !reachable.Contains(node.Id))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF-VAL-022",
                    $"节点“{node.Name}”从开始节点不可达，不会被执行。",
                    node.Id));
            }
        }
    }

    private void ValidateNodesDeep(
        WorkflowDefinition definition,
        WorkflowValidationContext context,
        List<WorkflowIssue> issues)
    {
        foreach (var node in definition.Nodes)
        {
            ValidateNodeBase(definition, node, issues);
            if (NodeTypeCatalog.Find(node.TypeId) is null)
            {
                continue;
            }

            if (_executors.TryGetValue(node.TypeId, out var executor))
            {
                issues.AddRange(executor.ValidateConfig(node, context));
            }
            else
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-045",
                    $"节点“{node.Name}”的类型“{node.TypeId}”没有可用的执行器实现。",
                    node.Id));
            }

            CheckMappings(definition, node, node.InputMappings, "输入映射", issues);
            ValidateBranchEdges(definition, node, issues);
        }
    }

    private static void ValidateNodeBase(WorkflowDefinition definition, WorkflowNode node, List<WorkflowIssue> issues)
    {
        if (node.TimeoutSeconds is { } timeout && (timeout < 1 || timeout > 86_400))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-029",
                $"节点“{node.Name}”的超时需在 1–86400 秒之间。",
                node.Id));
        }

        if (node.Retry.MaxAttempts is < 1 or > 5)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-028",
                $"节点“{node.Name}”的重试次数需在 1–5 之间。",
                node.Id));
        }

        if (node.Retry.DelayMilliseconds is < 0 or > 60_000)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-028",
                $"节点“{node.Name}”的重试间隔需在 0–60000 毫秒之间。",
                node.Id));
        }

        if (node.FailurePolicy == NodeFailurePolicy.ErrorBranch
            && !definition.OutgoingEdges(node.Id).Any(e => e.SourcePort == NodeTypeCatalog.ErrorPort))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-025",
                $"节点“{node.Name}”的失败策略为“错误分支”，但没有任何来自 error 出口的连线。",
                node.Id,
                hint: "从该节点的 error 端口连到异常处理节点，或改为“终止/继续”。"));
        }

        if (node.FailurePolicy != NodeFailurePolicy.ErrorBranch
            && definition.OutgoingEdges(node.Id).Any(e => e.SourcePort == NodeTypeCatalog.ErrorPort))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Warning,
                "WF-VAL-046",
                $"节点“{node.Name}”连接了 error 出口，但失败策略不是“错误分支”，该连线不会被执行。",
                node.Id));
        }
    }

    /// <summary>分支类节点的出边必须标注有效分支且每个分支都有出口。</summary>
    private static void ValidateBranchEdges(WorkflowDefinition definition, WorkflowNode node, List<WorkflowIssue> issues)
    {
        var branchIds = GetBranchIds(node);
        if (branchIds is null)
        {
            return;
        }

        var outgoing = definition.OutgoingEdges(node.Id)
            .Where(e => e.SourcePort != NodeTypeCatalog.ErrorPort)
            .ToList();
        foreach (var edge in outgoing)
        {
            if (string.IsNullOrWhiteSpace(edge.BranchId))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-019",
                    $"节点“{node.Name}”是分支节点，但存在未标注分支的连线；运行时空分支连线不会被执行。",
                    node.Id,
                    edge.Id,
                    "请为该连线选择分支（例如 yes/default、approved/rejected）。"));
            }
            else if (!branchIds.Contains(edge.BranchId) && !edge.BranchId.StartsWith("__", StringComparison.Ordinal))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-047",
                    $"连线标注的分支“{edge.BranchId}”在节点“{node.Name}”中不存在。",
                    node.Id,
                    edge.Id));
            }
        }

        var used = new HashSet<string>(outgoing.Select(e => e.BranchId ?? string.Empty), StringComparer.Ordinal);
        foreach (var branchId in branchIds)
        {
            if (!used.Contains(branchId))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF-VAL-018",
                    $"节点“{node.Name}”的分支“{branchId}”没有出口连线。",
                    node.Id));
            }
        }
    }

    /// <summary>返回分支节点的有效分支集合；非分支节点返回 null。</summary>
    internal static HashSet<string>? GetBranchIds(WorkflowNode node)
    {
        switch (node.TypeId)
        {
            case NodeTypeCatalog.Condition:
            {
                var config = WorkflowJson.TryGetConfig<ConditionNodeConfig>(node.Config, out _);
                return config is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(config.Branches.Select(b => b.Id).Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            }

            case NodeTypeCatalog.Parallel:
            {
                var config = WorkflowJson.TryGetConfig<ParallelNodeConfig>(node.Config, out _);
                return config is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(config.Branches.Select(b => b.Id).Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            }

            case NodeTypeCatalog.Confirm:
                return new HashSet<string>(new[] { "approved", "rejected" }, StringComparer.Ordinal);

            case NodeTypeCatalog.Rules:
            {
                var config = WorkflowJson.TryGetConfig<RulesNodeConfig>(node.Config, out _);
                return config is { OnFailure: RuleFailureAction.Branch }
                    ? new HashSet<string>(new[] { "fail", "pass" }, StringComparer.Ordinal)
                    : null;
            }

            default:
                return null;
        }
    }

    /// <summary>映射检查：目标重复、必填缺失、来源表达式与静态路径。</summary>
    private static void CheckMappings(
        WorkflowDefinition definition,
        WorkflowNode node,
        IReadOnlyList<WorkflowDataMapping> mappings,
        string purpose,
        List<WorkflowIssue> issues)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Target))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-048", $"节点“{node.Name}”的{purpose}存在空目标字段。", node.Id));
                continue;
            }

            if (!targets.Add(mapping.Target))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, "WF-VAL-021", $"节点“{node.Name}”的{purpose}目标“{mapping.Target}”重复。", node.Id));
            }

            if (string.IsNullOrWhiteSpace(mapping.Source))
            {
                if (mapping.Required && string.IsNullOrWhiteSpace(mapping.DefaultValueJson))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-049",
                        $"节点“{node.Name}”的{purpose}：必填字段“{mapping.Target}”既没有来源表达式也没有默认值。",
                        node.Id));
                }

                continue;
            }

            if (!WorkflowExpression.TryParse(mapping.Source, out var expression, out var parseError))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-011",
                    $"节点“{node.Name}”的{purpose}来源表达式无效：{parseError}",
                    node.Id));
                continue;
            }

            if (mapping.Convert is { Length: > 0 } convert
                && convert.Trim().ToLowerInvariant() is not ("number" or "string" or "boolean" or "json"))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-050",
                    $"节点“{node.Name}”的{purpose}使用了不支持的转换“{convert}”（可用：number、string、boolean、json）。",
                    node.Id));
            }

            if (mapping.DefaultValueJson is not null && WorkflowJson.TryParse(mapping.DefaultValueJson, out var defaultError) is null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-051",
                    $"节点“{node.Name}”的{purpose}默认值不是有效 JSON：{defaultError}",
                    node.Id));
            }

            CheckPurePath(definition, node, expression!.PurePath, purpose, issues);
        }
    }

    /// <summary>对纯路径做静态来源检查（input/vars/up 根）。</summary>
    internal static void CheckPurePath(
        WorkflowDefinition definition,
        WorkflowNode node,
        IReadOnlyList<string>? path,
        string purpose,
        List<WorkflowIssue> issues)
    {
        if (path is null || path.Count == 0)
        {
            return;
        }

        var root = path[0];
        switch (root)
        {
            case WorkflowScope.InputRoot:
                if (path.Count >= 2 && !definition.Inputs.Any(v => v.Name == path[1]))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-020",
                        $"节点“{node.Name}”的{purpose}引用了不存在的工作流输入“{path[1]}”。",
                        node.Id,
                        hint: "在“工作流输入”中补充该输入，或修正来源路径。"));
                }

                break;

            case WorkflowScope.VariablesRoot:
                if (path.Count >= 2 && !definition.Nodes.Any(n => n.OutputVariable == path[1]))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Warning,
                        "WF-VAL-052",
                        $"节点“{node.Name}”的{purpose}引用了变量“{path[1]}”，但没有任何节点声明写入该变量。",
                        node.Id));
                }

                break;

            case WorkflowScope.UpstreamRoot:
                if (path.Count >= 2)
                {
                    var sourceNode = definition.FindNode(path[1]);
                    if (sourceNode is null)
                    {
                        issues.Add(new WorkflowIssue(
                            WorkflowIssueSeverity.Error,
                            "WF-VAL-053",
                            $"节点“{node.Name}”的{purpose}引用了不存在的上游节点“{path[1]}”。",
                            node.Id));
                    }
                    else if (path.Count >= 3)
                    {
                        var descriptor = NodeTypeCatalog.Find(sourceNode.TypeId);
                        var port = path[2];
                        if (descriptor is not null
                            && port is not ("out" or "error" or "raw")
                            && descriptor.OutputPorts.All(p => p.Name != port))
                        {
                            issues.Add(new WorkflowIssue(
                                WorkflowIssueSeverity.Warning,
                                "WF-VAL-054",
                                $"节点“{node.Name}”的{purpose}引用了上游节点“{sourceNode.Name}”上不常见的输出端口“{port}”。",
                                node.Id));
                        }
                    }
                }

                break;

            case "item":
            case "index":
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF-VAL-055",
                    $"节点“{node.Name}”的{purpose}使用了“{root}”，该变量只在循环节点内部有效。",
                    node.Id));
                break;
        }
    }

    /// <summary>子工作流递归引用检测（含间接引用）。</summary>
    private static void ValidateSubWorkflowRecursion(
        WorkflowDefinition definition,
        WorkflowValidationContext context,
        List<WorkflowIssue> issues)
    {
        if (context.WorkflowLoader is null)
        {
            return;
        }

        foreach (var node in definition.Nodes)
        {
            var references = CollectSubWorkflowReferences(node);
            foreach (var reference in references)
            {
                if (string.Equals(reference, definition.Id, StringComparison.Ordinal))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-017",
                        $"节点“{node.Name}”直接引用了自身，形成递归。",
                        node.Id));
                    continue;
                }

                if (HasCycle(reference, definition.Id, context.WorkflowLoader, new HashSet<string>(StringComparer.Ordinal)))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-017",
                        $"节点“{node.Name}”引用的子工作流“{reference}”会（间接）调用当前工作流，形成递归。",
                        node.Id));
                }
            }
        }
    }

    private static IEnumerable<string> CollectSubWorkflowReferences(WorkflowNode node)
    {
        switch (node.TypeId)
        {
            case NodeTypeCatalog.SubWorkflow:
            {
                var config = WorkflowJson.TryGetConfig<SubWorkflowNodeConfig>(node.Config, out _);
                if (!string.IsNullOrWhiteSpace(config?.WorkflowId))
                {
                    yield return config!.WorkflowId;
                }

                break;
            }

            case NodeTypeCatalog.Loop:
            {
                var config = WorkflowJson.TryGetConfig<LoopNodeConfig>(node.Config, out _);
                if (config is { Mode: LoopMode.SubWorkflow } && !string.IsNullOrWhiteSpace(config.SubWorkflowId))
                {
                    yield return config.SubWorkflowId!;
                }

                break;
            }
        }
    }

    private static bool HasCycle(
        string workflowId,
        string targetId,
        Func<string, WorkflowDefinition?> loader,
        HashSet<string> visited)
    {
        if (!visited.Add(workflowId))
        {
            return false;
        }

        var child = loader(workflowId);
        if (child is null)
        {
            return false;
        }

        foreach (var node in child.Nodes)
        {
            foreach (var reference in CollectSubWorkflowReferences(node))
            {
                if (string.Equals(reference, targetId, StringComparison.Ordinal))
                {
                    return true;
                }

                if (HasCycle(reference, targetId, loader, visited))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
