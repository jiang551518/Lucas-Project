using AgentBackend.Models;

namespace AgentBackend.Services;

/// <summary>应用服务接口：封装项目、会话和模型运行用例。</summary>
public interface IAgentService
{
    /// <summary>返回工作区中的项目列表。</summary>
    Task<IReadOnlyList<AgentProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    /// <summary>校验名称并创建项目。</summary>
    Task<AgentProject> CreateProjectAsync(string name, string workspacePath, string workspacePathType, CancellationToken cancellationToken = default);
    /// <summary>校验并更新项目名称。</summary>
    Task<bool> RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default);
    /// <summary>返回最近更新的会话列表。</summary>
    Task<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken cancellationToken = default);
    /// <summary>创建一个空白会话。</summary>
    Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default);
    /// <summary>读取单个会话；不存在时返回 null。</summary>
    Task<AgentSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>将会话移动到项目或解除项目关联。</summary>
    Task<bool> AssignSessionAsync(Guid sessionId, Guid? projectId, CancellationToken cancellationToken = default);
    /// <summary>校验并更新会话标题。</summary>
    Task<bool> RenameSessionAsync(Guid sessionId, string title, CancellationToken cancellationToken = default);
    /// <summary>验证并保存会话工作区和 AI 权限模式。</summary>
    Task<bool> UpdateSessionExecutionContextAsync(Guid sessionId, Guid? projectId, string workspacePath, string workspacePathType, string permissionMode, CancellationToken cancellationToken = default);
    /// <summary>删除会话和该会话的完整消息历史。</summary>
    Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    /// <summary>删除项目并把会话移回最近列表，不级联删除聊天。</summary>
    Task<bool> DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    /// <summary>读取已配置模型 Provider 的安全摘要。</summary>
    Task<IReadOnlyList<ProviderSummary>> GetProvidersAsync(CancellationToken cancellationToken = default);
    /// <summary>验证并安全保存 OpenAI-compatible Provider 及其模型列表。</summary>
    Task<ProviderSummary> SaveProviderAsync(SaveProviderRequest request, CancellationToken cancellationToken = default);
    /// <summary>使用已保存的 DeepSeek API Key 查询账号余额。</summary>
    Task<ProviderBalance> GetProviderBalanceAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>删除本地保存的 Provider 配置及其模型列表。</summary>
    Task<bool> DeleteProviderProfileAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>读取技能清单。</summary>
    Task<IReadOnlyList<AgentSkill>> GetSkillsAsync(CancellationToken cancellationToken = default);
    /// <summary>校验并保存技能。</summary>
    Task<AgentSkill> SaveSkillAsync(AgentSkill skill, CancellationToken cancellationToken = default);
    /// <summary>删除技能。</summary>
    Task<bool> DeleteSkillAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>读取本地记忆清单。</summary>
    Task<IReadOnlyList<MemoryEntry>> GetMemoriesAsync(CancellationToken cancellationToken = default);
    /// <summary>校验并保存记忆。</summary>
    Task<MemoryEntry> SaveMemoryAsync(MemoryEntry memory, CancellationToken cancellationToken = default);
    /// <summary>删除记忆。</summary>
    Task<bool> DeleteMemoryAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>读取界面显示和 Harness/Loop 设置。</summary>
    Task<AgentSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    /// <summary>验证并保存界面显示和 Harness/Loop 设置。</summary>
    Task<AgentSettings> SaveSettingsAsync(AgentSettings settings, CancellationToken cancellationToken = default);
    /// <summary>执行指定 Provider 与模型的请求，并以统一事件格式流式返回结果。</summary>
    IAsyncEnumerable<AgentEvent> RunAsync(Guid sessionId, string prompt, string? providerName = null, string? modelName = null, AgentRunControl? runControl = null, string? connectionId = null, CancellationToken cancellationToken = default);
    /// <summary>停止运行后保存已生成的部分回答，避免中断内容只留在前端内存。</summary>
    Task SaveInterruptedRunAsync(Guid sessionId, string content, string reasoning, DateTimeOffset runStartedAt, long elapsedMilliseconds, CancellationToken cancellationToken = default);
}
