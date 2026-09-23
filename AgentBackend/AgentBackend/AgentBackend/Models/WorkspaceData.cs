namespace AgentBackend.Models;

public sealed class WorkspaceData
{
    public List<AgentProject> Projects { get; set; } = [];
    public List<AgentSession> Sessions { get; set; } = [];
    public List<StoredProviderProfile> Providers { get; set; } = [];
    public List<AgentSkill> Skills { get; set; } = [];
    public List<MemoryEntry> Memories { get; set; } = [];
    public AgentSettings Settings { get; set; } = new();
}

/// <summary>本地可管理并可注入模型上下文的技能条目。</summary>
public sealed class AgentSkill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>用户维护的本地记忆，可选择性注入后续对话。</summary>
public sealed class MemoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>本地界面显示偏好和受限 Harness/Loop 工作参数。</summary>
public sealed class AgentSettings
{
    public bool ShowReasoning { get; set; } = true;
    public bool ShowTokenUsage { get; set; } = true;
    public bool RequireToolApproval { get; set; } = true;
}

public sealed class AgentProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string WorkspacePath { get; set; } = string.Empty;
    public string WorkspacePathType { get; set; } = "directory";
}

public sealed class AgentMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long? ElapsedMilliseconds { get; set; }
    public string? GitDiff { get; set; }
}

/// <summary>本地保存的 Provider 配置；API Key 字段只保存 ASP.NET Data Protection 密文。</summary>
public sealed class StoredProviderProfile
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> Models { get; set; } = [];
    // Retained to read provider profiles written by earlier app versions.
    public string Model { get; set; } = string.Empty;
    public string ProtectedApiKey { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>返回客户端的 Provider 信息，不包含 API Key 本身。</summary>
public sealed record ProviderSummary(string Name, string BaseUrl, IReadOnlyList<string> Models, bool HasApiKey);

/// <summary>DeepSeek API 账号的可用余额与按币种汇总明细。</summary>
public sealed record ProviderBalance(bool IsAvailable, IReadOnlyList<ProviderBalanceInfo> BalanceInfos);

/// <summary>单一币种的总余额、赠金余额与充值余额。</summary>
public sealed record ProviderBalanceInfo(string Currency, string TotalBalance, string GrantedBalance, string ToppedUpBalance);

/// <summary>用于保存或更新 Provider 配置的请求对象。</summary>
public sealed record SaveProviderRequest(string Name, string? ExistingName, string BaseUrl, IReadOnlyList<string> Models, string? ApiKey);
