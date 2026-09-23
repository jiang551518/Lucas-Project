namespace AgentBackend.Models;

/// <summary>Agent 运行期间统一推送给桌面端的事件信封。</summary>
public sealed record AgentEvent(
    string Type,
    string RunId,
    string? Text = null,
    string? Provider = null,
    string? Model = null,
    UsageSnapshot? Usage = null,
    string? RequestId = null,
    string? ToolName = null,
    long? ElapsedMilliseconds = null)
{
    /// <summary>创建运行开始事件，携带 Provider 与模型标识。</summary>
    public static AgentEvent Started(string runId, string provider, string model) =>
        new("run.started", runId, Provider: provider, Model: model);

    /// <summary>创建一段增量思考文本事件。</summary>
    public static AgentEvent Reasoning(string runId, string text) =>
        new("reasoning.delta", runId, Text: text);

    /// <summary>替换思考流内容，例如撤下模型泄漏的内部工具协议。</summary>
    public static AgentEvent ReplaceReasoning(string runId, string text) =>
        new("reasoning.replace", runId, Text: text);

    /// <summary>创建一段增量回答文本事件。</summary>
    public static AgentEvent Message(string runId, string text) =>
        new("message.delta", runId, Text: text);

    /// <summary>替换当前回答文本，例如撤下不兼容模型泄漏的内部工具协议。</summary>
    public static AgentEvent ReplaceMessage(string runId, string text) =>
        new("message.replace", runId, Text: text);

    /// <summary>创建 Token 使用量更新事件。</summary>
    public static AgentEvent UsageUpdate(string runId, UsageSnapshot usage) =>
        new("usage.update", runId, Usage: usage);

    /// <summary>创建运行完成事件。</summary>
    public static AgentEvent Completed(string runId, long elapsedMilliseconds) =>
        new("run.completed", runId, ElapsedMilliseconds: elapsedMilliseconds);

    /// <summary>创建需要用户确认的本地工具请求事件。</summary>
    public static AgentEvent ApprovalRequired(string runId, string requestId, string toolName, string details) =>
        new("tool.approval_required", runId, Text: details, RequestId: requestId, ToolName: toolName);

    /// <summary>创建工具执行状态事件。</summary>
    public static AgentEvent ToolStatus(string runId, string toolName, string status, string details) =>
        new($"tool.{status}", runId, Text: details, ToolName: toolName);

    /// <summary>创建携带本轮 Agent 代码改动的 Git 差异事件。</summary>
    public static AgentEvent GitDiff(string runId, string diff) =>
        new("run.git_diff", runId, Text: diff);
}

/// <summary>一次模型请求的输入、输出和总 Token 数。</summary>
public sealed record UsageSnapshot(
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    bool Estimated);
