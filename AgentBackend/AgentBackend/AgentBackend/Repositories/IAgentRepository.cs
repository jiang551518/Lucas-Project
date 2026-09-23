using AgentBackend.Models;

namespace AgentBackend.Repositories;

public interface IAgentRepository
{
    /// <summary>读取项目集合。</summary>
    Task<IReadOnlyList<AgentProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    /// <summary>创建项目。</summary>
    Task<AgentProject> CreateProjectAsync(string name, string workspacePath, string workspacePathType, CancellationToken cancellationToken = default);
    /// <summary>更新项目名称。</summary>
    Task<bool> RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default);
    /// <summary>读取会话集合。</summary>
    Task<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken cancellationToken = default);
    /// <summary>创建空白会话。</summary>
    Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default);
    /// <summary>设置会话的项目 ID；传 null 时解除关联。</summary>
    Task<bool> AssignSessionAsync(Guid sessionId, Guid? projectId, CancellationToken cancellationToken = default);
    /// <summary>更新会话标题。</summary>
    Task<bool> RenameSessionAsync(Guid sessionId, string title, CancellationToken cancellationToken = default);
    /// <summary>保存会话工作区和 AI 权限模式。</summary>
    Task<bool> UpdateSessionExecutionContextAsync(Guid sessionId, Guid? projectId, string workspacePath, string workspacePathType, string permissionMode, CancellationToken cancellationToken = default);
    /// <summary>追加消息到会话。</summary>
    Task AddMessageAsync(Guid sessionId, AgentMessage message, CancellationToken cancellationToken = default);
    /// <summary>保存消息 Token 用量与本轮运行耗时。</summary>
    Task UpdateUsageAsync(Guid messageId, int inputTokens, int outputTokens, long elapsedMilliseconds, string? gitDiff, CancellationToken cancellationToken = default);
    /// <summary>删除会话及该会话下的全部消息。</summary>
    Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    /// <summary>删除项目并解除其下所有会话的项目关联；不删除会话。</summary>
    Task<bool> DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    /// <summary>读取本地 Provider 配置记录，其中 API Key 为密文。</summary>
    Task<IReadOnlyList<StoredProviderProfile>> GetProviderProfilesAsync(CancellationToken cancellationToken = default);
    /// <summary>新建或更新本地 Provider 配置。</summary>
    Task SaveProviderProfileAsync(StoredProviderProfile profile, string? existingName = null, CancellationToken cancellationToken = default);
    /// <summary>移除指定 Provider 的本地覆盖配置。</summary>
    Task<bool> DeleteProviderProfileAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>读取本地技能列表。</summary>
    Task<IReadOnlyList<AgentSkill>> GetSkillsAsync(CancellationToken cancellationToken = default);
    /// <summary>新增或更新本地技能。</summary>
    Task<AgentSkill> SaveSkillAsync(AgentSkill skill, CancellationToken cancellationToken = default);
    /// <summary>删除指定本地技能。</summary>
    Task<bool> DeleteSkillAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>读取本地记忆条目列表。</summary>
    Task<IReadOnlyList<MemoryEntry>> GetMemoriesAsync(CancellationToken cancellationToken = default);
    /// <summary>新增或更新本地记忆条目。</summary>
    Task<MemoryEntry> SaveMemoryAsync(MemoryEntry memory, CancellationToken cancellationToken = default);
    /// <summary>删除指定本地记忆条目。</summary>
    Task<bool> DeleteMemoryAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>读取本地界面、Harness 与 Loop 设置。</summary>
    Task<AgentSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    /// <summary>保存本地界面、Harness 与 Loop 设置。</summary>
    Task SaveSettingsAsync(AgentSettings settings, CancellationToken cancellationToken = default);
    /// <summary>创建会话并可选保存初始提示。</summary>
    Task<AgentSession> CreateSessionAsync(
        string? initialPrompt,
        CancellationToken cancellationToken = default);

    /// <summary>通过 ID 获取会话。</summary>
    Task<AgentSession?> GetSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
