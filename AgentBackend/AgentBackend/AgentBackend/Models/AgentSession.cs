namespace AgentBackend.Models;

public sealed class AgentSession
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Status { get; set; } = "created";

    public string? InitialPrompt { get; set; }

    public string Title { get; set; } = "新对话";

    public Guid? ProjectId { get; set; }

    /// <summary>当前会话选择的工作区路径；为空时使用关联项目路径。</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>当前会话工作区是目录还是单个文件。</summary>
    public string WorkspacePathType { get; set; } = "directory";

    /// <summary>本地工具权限策略：approval 或 fullAccess。</summary>
    public string PermissionMode { get; set; } = "approval";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AgentMessage> Messages { get; set; } = [];
}
