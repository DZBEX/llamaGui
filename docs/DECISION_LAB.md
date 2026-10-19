# 决策模型实验室（Decision Model Lab）

> 在启动器内调试决策模型（`/v1/systemone`）、可视化编排工作流、运行/断点调试、维护测试用例、
> 批量回归与运行历史追溯。**不下载模型**，只管理本地资源（沿用启动器既有实例管理，不新起进程）。

## 1. 导航与页面

左侧导航「决策模型实验室」包含五个子页：

| 子页 | 作用 |
| --- | --- |
| 模型调试 | 单次输入（state + questions）调用 `/v1/systemone`，查看结构化概率、原始响应、耗时与校验报告；可导出调试输入或保存为测试用例 |
| 工作流设计与调试 | 画布编辑（节点库拖放/连线/属性）、校验、运行/单步/断点/暂停/继续/停止、运行轨迹、节点详情与日志 |
| 测试用例 | 单场景用例：输入、模拟响应、断言（期望结果/容差/忽略字段），单次运行并显示断言结果 |
| 批量测试 | 选择用例批量执行、受控并发（1–4，默认 1）、暂停/继续/取消、统计与结果导出（JSON/CSV） |
| 运行历史 | 查询过滤、详情（节点/日志/输入输出/变量/模型上下文）、两次运行字段级比较、复制为新用例、导出诊断、按保留策略清理 |

## 2. 快速上手

1. 在「服务实例」页启动一个 llama-server 实例（决策模型需要 2026-10-02 之后的构建，例如 b11577，且模型为决策模型）。
2. 打开「决策模型实验室 → 模型调试」：选择实例 → 编辑 state 与 questions → 「运行」。
   - 运行前会校验 JSON 与问题结构；失败时给出具体原因与建议。
   - 可在「保存到用例」下拉选择工作流，把当前输入保存为测试用例（`input.state` / `input.questions`）。
3. 到「工作流设计与调试」：从左侧节点库拖入节点 → 连线（拖动节点右侧端口到目标节点）→
   在右侧「属性」面板配置 → 顶部「校验工作流」→ 「运行」（带播放图标）启动调试运行。
4. 到「测试用例」：为新用例配置模拟响应与断言 → 「运行用例」查看断言结果。
5. 到「批量测试」：勾选用例批量执行；到「运行历史」查看/比较/导出。

> 测试模式：设计器运行面板与用例均可切换 **本地模型（真实调用）** / **模拟（不调用模型，使用固定响应）**，
> 运行条与运行摘要会显示实际使用的模式，避免误判。

## 3. 工作流文件格式（版本化 JSON）

存储位置（数据目录 = 程序目录，可在设置中了解；下同）：

- 工作流定义：`workflows/<workflowId>.workflow.json`
- 版本快照（不可变）：`workflows/snapshots/<workflowId>/<yyyyMMdd-HHmmss>-<label>.snapshot.json`
- 测试用例：`workflows/testcases/<caseId>.testcase.json`
- 运行历史：SQLite `data/llamalauncher.db` 的 `WorkflowRuns` 表（含不可变定义快照与节点/日志 JSON）

定义结构（节选）：

```jsonc
{
  "schemaVersion": 1,
  "id": "wf-xxx", "name": "退款路由", "description": "…", "tags": ["demo"],
  "createdAt": "…", "updatedAt": "…",
  "inputs":  [ { "name": "message", "type": "string", "required": true, "defaultValueJson": null } ],
  "outputs": [ { "name": "route", "type": "string" } ],
  "defaults": { "maxNodeParallelism": 2, "defaultNodeTimeoutSeconds": 120, "maxRunSeconds": 1800,
                "defaultTestMode": "localModel", "maxLoopIterations": 100,
                "maxResponseBytes": 1048576, "maxNodeSnapshotChars": 262144 },
  "nodes": [
    { "id": "n1", "typeId": "core.start", "typeVersion": 1, "name": "开始", "x": 60, "y": 120,
      "failurePolicy": "terminate", "retry": { "maxAttempts": 1, "delayMilliseconds": 500, "condition": "never" },
      "inputMappings": [], "config": { } }
  ],
  "edges": [
    { "id": "e1", "sourceNodeId": "n1", "sourcePort": "out", "targetNodeId": "n2", "targetPort": "in",
      "branchId": null }
  ]
}
```

- 导入时会校验 schema 版本、ID 安全字符、未知节点类型（保留原始定义并报告，不静默丢弃）；
  文件大小上限 4 MB；导出为原子写入。
- 版本快照不可变；运行历史内嵌运行时刻的定义快照，编辑工作流不会改变历史。

### 表达式与模板（安全边界）

- **表达式**（条件分支、规则、映射、数据处理）：支持比较（`== != < <= > >=`）、逻辑（`&& || !`）、
  空合并（`??`）、`in`、数值运算与白名单函数（`len/count/first/last/isEmpty/lower/upper/trim/contains/startsWith/endsWith/indexOf/concat/matches/toNumber/toString/toBool/abs/min/max/round/floor/ceil/sum/avg/isNull/isNumber/isString/isArray/isObject/isBool/coalesce/get/keys/values`）。
  不执行任意 C#/脚本/命令；有长度、嵌套、操作步数与集合元素上限。
- **模板**：`{{ 路径 }}`（仅字段路径，如 `{{ input.message }}`、`{{ up.n2.out.route.choice }}`）；
  `\{{` 表示字面量。用于决策问题说明、规则消息、state 文本、数据处理模板等。
- 作用域根：`input`（工作流输入）、`vars`（变量）、`up`（上游输出：`up.<节点ID>.<端口>…`）、
  `meta`（运行元数据）、`error`（错误上下文，仅错误路径）、`doc`（数据处理节点内当前文档）。

## 4. 节点参考

| 类型 | 说明 | 关键配置 |
| --- | --- | --- |
| `core.start` 开始 | 校验并快照工作流输入 | 输入定义在「工作流属性」 |
| `core.end` 结束 | 输出映射与结束状态（成功/业务失败） | `status`、`outputMappings`、失败说明模板 |
| `data.transform` 数据处理 | 受控操作管线：Assign/Pick/Rename/Remove/Default/ToString/ToNumber/ToBoolean/Split/Join/Filter/Sort/Limit/Merge/ParseJson/Stringify/BuildObject/Template | `operations[]` |
| `flow.condition` 条件分支 | 命名分支 + 默认分支，表达式试算 | `branches[]`（恰好一个默认） |
| `model.decision` 决策模型 | 调用 `/v1/systemone`（复用实例管理） | 实例绑定、state 模板/JSON 模板、`questions[]`、超时、响应上限 |
| `rules.check` 规则校验 | 多条规则（命中=违规），错误/警告分级 | `rules[]`、`onFailure`（stop/branch/continue），branch 时出口 `pass`/`fail` |
| `flow.loop` 循环 | 数组逐项：子工作流 或 决策模板；防无限循环 | `itemsExpression`、`maxIterations`、`continueOnItemError`、`itemTimeoutSeconds` |
| `flow.parallel` 并行扇出 | 多分支并行（受运行最大并发与 `maxConcurrency` 限制） | `branches[]`、`sharedExpression` |
| `flow.join` 并行汇合 | 等待全部入边；AllComplete/CollectErrors/FailFast | `mode`、`expectedBranches` |
| `interact.confirm` 人工确认 | 本地交互式暂停；批准/拒绝分支 + 可填写字段 | `title/messageTemplate/fields[]` |
| `compose.subworkflow` 子工作流 | 调用已保存工作流（输入映射；递归与深度保护） | `workflowId`、`timeoutSeconds` |
| `flow.exception` 异常处理 | 接收 `error.*` 上下文；记录或重新抛出 | `action`、`recordLog` |
| `algorithm.solve` 算法求解 | 调用注册求解器（含内置示例：加权排序 Top-K） | `solverKey`、`inputJsonTemplate`、超时 |

所有节点通用：名称/备注、失败策略（终止/继续/错误分支）、节点超时、重试（次数/间隔/条件）、
输入映射、写入变量（把主输出保存到 `vars.<名称>`）、断点开关。

## 5. 运行与调试

- **校验**：运行前与「校验工作流」都会执行同一校验器（结构、连线、分支、映射、配置、子流程递归、
  模型/求解器可用性），错误阻止运行、警告提示。
- **运行轨迹**：节点边框按真实状态着色（等待/运行中/成功/失败/跳过/取消/断点/等待人工确认），
  执行过的连线高亮；右侧「运行」页含运行摘要、节点耗时表与选中节点详情（输入/输出/写入变量/模型诊断/错误）。
- **断点/单步**：节点属性勾选断点或用工具栏「切换断点」；「单步启动」或运行中「单步」在每个节点后暂停；
  「暂停/继续/停止」按真实状态显示（暂停在节点边界生效；停止会向活动节点传播取消并如实显示状态）。
- **从选中节点开始**：需要提供起始快照（`variables/upstream`），工具会跳过非目标链路并给出明确提示。
- **日志**：级别过滤、文本过滤、导出（运行记录内）；日志有容量上限并明确提示。

## 6. 测试用例、批量与回归

- 用例绑定工作流（可选版本快照标签），包含输入、模拟响应（节点 ID → 固定输出/分支/模拟错误）、
  断言（Equals/NotEquals/Contains/GreaterThan…/Exists/IsTrue/RegexMatch/ApproxEquals/JsonEquals）、
  忽略字段与级别（Error 使用例失败；Warning 仅提示）。
- 断言目标是安全表达式，作用域：`output`（工作流输出）、`vars`（最终变量）、`meta`、`records.<节点ID>.state/output/error/...`。
- 批量测试默认并发 1（避免耗尽本地模型资源），实时统计完成/通过/失败/取消与平均耗时，可导出 JSON/CSV。
- 「运行历史 → 对比」对两次运行做字段级差异（新增/删除/变化）与节点状态/耗时变化；忽略字段由用例配置。

## 7. 限制与未实现（诚实清单）

- **外部扩展模式**未实现：运行模式仅「本地模型」与「模拟」；自定义节点/算法通过代码注册（DI）扩展，
  不加载任意 DLL/脚本（信任边界见提示词与 `docs/SECURITY.md`）。
- 并行节点的分支级“立即取消其他分支”为 FailFast 语义（在汇合点失败并向运行传播）；不做节点内抢占。
- 从中间节点启动仅支持提供快照的受控方式，不自动推断上游输出。
- 画布为自绘控件（无第三方图形库）；小窗口下需缩放/平移查看全图。
- 运行历史保留策略默认 200 条（可在页面手动清理）。

## 8. 存储与迁移

- 数据库迁移 v2 新增 `WorkflowRuns` 表（含 `FinalVariablesJson`）。
- 工作流/用例目录随数据根目录移动（设置中的路径体系），便携部署下与程序目录同在。
- 运行历史与快照不会因工作流编辑而改变；删除工作流不删除快照与运行历史。

## 9. 相关测试

```powershell
dotnet test tests/LlamaLauncher.UnitTests -c Debug            # 领域/表达式/模板/校验器/引擎/断言/差异
dotnet test tests/LlamaLauncher.IntegrationTests -c Debug     # 文件存储/迁移/决策 HTTP 客户端/模型解析
dotnet test tests/LlamaLauncher.UiTests -c Debug              # 组合根 + 实验室渲染（artifacts/lab-preview/）
```
