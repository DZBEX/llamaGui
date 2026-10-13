namespace LlamaLauncher.Domain;

/// <summary>模型文件当前可用性状态。</summary>
public enum ModelFileStatus
{
    Unknown = 0,
    Ok = 1,
    Missing = 2,
    InvalidExtension = 3,
    Empty = 4,
    Unreadable = 5,
    InvalidFormat = 6,
}

/// <summary>GGUF 元数据解析状态。</summary>
public enum MetadataParseStatus
{
    NotParsed = 0,
    Parsed = 1,
    Failed = 2,
}

/// <summary>服务实例状态机状态。</summary>
public enum InstanceState
{
    Unknown = 0,
    Stopped = 1,
    Starting = 2,
    LoadingModel = 3,
    Ready = 4,
    Stopping = 5,
    Failed = 6,
    ExitedUnexpectedly = 7,
}

/// <summary>llama.cpp 安装记录状态。</summary>
public enum InstallationStatus
{
    Unknown = 0,
    Available = 1,
    Unhealthy = 2,
    Missing = 3,
}

/// <summary>安装来源类型。</summary>
public enum InstallationSourceKind
{
    OfficialRelease = 0,
    ManualImport = 1,
    ExternalPath = 2,
}

/// <summary>聊天消息角色。</summary>
public enum ChatRole
{
    System = 0,
    User = 1,
    Assistant = 2,
}

/// <summary>聊天消息持久化状态。</summary>
public enum MessageStatus
{
    Pending = 0,
    Streaming = 1,
    Completed = 2,
    Interrupted = 3,
    Failed = 4,
}

/// <summary>更新操作所处阶段。</summary>
public enum UpdateStage
{
    Checking = 0,
    Available = 1,
    Downloading = 2,
    Verifying = 3,
    Extracting = 4,
    Probing = 5,
    ReadyToSwitch = 6,
    Active = 7,
    Cancelled = 8,
    Failed = 9,
    RolledBack = 10,
    NotAvailable = 11,
}

/// <summary>更新操作类型。</summary>
public enum UpdateOperationKind
{
    Check = 0,
    Download = 1,
    Install = 2,
    SwitchDefault = 3,
    Rollback = 4,
    Cleanup = 5,
    Import = 6,

    /// <summary>CUDA 运行时辅助包（cudart）安装。</summary>
    RuntimePack = 7,
}

/// <summary>llama.cpp 构建后端类型（由资产名/设备探测推断）。</summary>
public enum BackendKind
{
    Unknown = 0,
    Cpu = 1,
    Cuda = 2,
    Vulkan = 3,
    Sycl = 4,
    Rocm = 5,
    OpenVino = 6,
    Other = 7,
}

/// <summary>更新通道。</summary>
public enum UpdateChannel
{
    Stable = 0,
    Prerelease = 1,
}

/// <summary>监听地址范围。</summary>
public enum AddressScope
{
    Invalid = 0,
    Localhost = 1,
    Lan = 2,
    Public = 3,
}

/// <summary>校验问题级别。</summary>
public enum ValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>健康检查状态类别。</summary>
public enum HealthStatusKind
{
    Unknown = 0,
    Loading = 1,
    Ready = 2,
    Error = 3,
}

/// <summary>聊天请求错误分类。</summary>
public enum ChatErrorKind
{
    None = 0,
    Connection = 1,
    Timeout = 2,
    Unauthorized = 3,
    NotFound = 4,
    RateLimited = 5,
    ServerError = 6,
    Protocol = 7,
    Cancelled = 8,
    BadRequest = 9,
    Unknown = 10,
}
