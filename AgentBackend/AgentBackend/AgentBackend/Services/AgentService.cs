using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Diagnostics;
using AgentBackend.Models;
using AgentBackend.Repositories;
using Microsoft.AspNetCore.DataProtection;

namespace AgentBackend.Services;

/// <summary>实现工作区用例，并适配 OpenAI Chat Completions SSE 流式协议。</summary>
public sealed class AgentService : IAgentService
{
    private readonly IAgentRepository _repository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _providerKeyProtector;
    private readonly ToolApprovalBroker _approvalBroker;
    private readonly WorkspaceToolExecutor _toolExecutor;
    private readonly GitChangeTracker _gitChangeTracker;

    /// <summary>注入仓储、HTTP 客户端工厂和 API Key 数据保护服务。</summary>
    public AgentService(IAgentRepository repository, IHttpClientFactory httpClientFactory, IDataProtectionProvider dataProtectionProvider, ToolApprovalBroker approvalBroker, WorkspaceToolExecutor toolExecutor, GitChangeTracker gitChangeTracker)
    {
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _providerKeyProtector = dataProtectionProvider.CreateProtector("Lucas.Agent.ProviderApiKey.v1");
        _approvalBroker = approvalBroker;
        _toolExecutor = toolExecutor;
        _gitChangeTracker = gitChangeTracker;
    }

    /// <summary>获取项目列表。</summary>
    public Task<IReadOnlyList<AgentProject>> GetProjectsAsync(CancellationToken cancellationToken = default) => _repository.GetProjectsAsync(cancellationToken);

    /// <summary>校验项目名称并创建项目。</summary>
    public Task<AgentProject> CreateProjectAsync(string name, string workspacePath, string workspacePathType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("项目名称不能为空", nameof(name));
        var path = workspacePath?.Trim() ?? string.Empty;
        var type = workspacePathType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type is not ("directory" or "file")) throw new ArgumentException("工作区类型必须是 directory 或 file");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择有效的绝对路径");
        if (type == "directory" && !Directory.Exists(path)) throw new ArgumentException("所选工作目录不存在");
        if (type == "file" && !File.Exists(path)) throw new ArgumentException("所选工作文件不存在");
        return _repository.CreateProjectAsync(name.Trim(), Path.GetFullPath(path), type, cancellationToken);
    }

    /// <summary>检查项目名称后委托仓储更新项目。</summary>
    public Task<bool> RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default) =>
        _repository.RenameProjectAsync(projectId, ValidateDisplayName(name, "项目名称"), cancellationToken);

    /// <summary>获取按最近更新时间排序的会话列表。</summary>
    public Task<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken cancellationToken = default) => _repository.GetSessionsAsync(cancellationToken);

    /// <summary>创建一个空白会话。</summary>
    public Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) => _repository.CreateSessionAsync(cancellationToken);

    /// <summary>按 ID 获取会话。</summary>
    public Task<AgentSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) => _repository.GetSessionAsync(id, cancellationToken);

    /// <summary>将会话关联到项目，或在 projectId 为 null 时解除关联。</summary>
    public Task<bool> AssignSessionAsync(Guid sessionId, Guid? projectId, CancellationToken cancellationToken = default) => _repository.AssignSessionAsync(sessionId, projectId, cancellationToken);

    /// <summary>检查会话标题后委托仓储更新会话。</summary>
    public Task<bool> RenameSessionAsync(Guid sessionId, string title, CancellationToken cancellationToken = default) =>
        _repository.RenameSessionAsync(sessionId, ValidateDisplayName(title, "对话名称"), cancellationToken);

    /// <summary>验证工作区存在性和权限枚举，再保存会话执行上下文。</summary>
    public Task<bool> UpdateSessionExecutionContextAsync(Guid sessionId, Guid? projectId, string workspacePath, string workspacePathType, string permissionMode, CancellationToken cancellationToken = default)
    {
        var mode = permissionMode?.Trim() ?? string.Empty;
        if (mode is not ("approval" or "fullAccess")) throw new ArgumentException("权限模式必须是 approval 或 fullAccess");
        if (projectId.HasValue) return _repository.UpdateSessionExecutionContextAsync(sessionId, projectId, string.Empty, "directory", mode, cancellationToken);
        var path = workspacePath?.Trim() ?? string.Empty;
        if (path.Length == 0) return _repository.UpdateSessionExecutionContextAsync(sessionId, null, string.Empty, "directory", mode, cancellationToken);
        var type = workspacePathType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type is not ("directory" or "file")) throw new ArgumentException("工作区类型必须是 directory 或 file");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("工作区路径必须是绝对路径");
        if (type == "directory" && !Directory.Exists(path)) throw new ArgumentException("工作目录不存在");
        if (type == "file" && !File.Exists(path)) throw new ArgumentException("工作文件不存在");
        return _repository.UpdateSessionExecutionContextAsync(sessionId, null, Path.GetFullPath(path), type, mode, cancellationToken);
    }

    /// <summary>删除会话及其消息历史。</summary>
    public Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => _repository.DeleteSessionAsync(sessionId, cancellationToken);

    /// <summary>删除项目，并通过仓储将该项目会话移回最近列表。</summary>
    public Task<bool> DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => _repository.DeleteProjectAsync(projectId, cancellationToken);

    /// <summary>读取用户添加的 Provider 与模型列表，返回不含密钥的安全摘要。</summary>
    public async Task<IReadOnlyList<ProviderSummary>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _repository.GetProviderProfilesAsync(cancellationToken);
        return stored.OrderBy(profile => profile.Name)
            .Select(profile => new ProviderSummary(profile.Name, profile.BaseUrl, GetModelNames(profile), !string.IsNullOrWhiteSpace(profile.ProtectedApiKey)))
            .ToArray();
    }

    /// <summary>验证 Provider 地址与模型列表后加密 API Key，并持久化到本机数据文件。</summary>
    public async Task<ProviderSummary> SaveProviderAsync(SaveProviderRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("服务商名称不能为空", nameof(request));
        if (name.Length > 80) throw new ArgumentException("服务商名称不能超过 80 个字符", nameof(request));
        var validUrl = Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri);
        var isLoopbackHttp = validUrl && uri!.Scheme == Uri.UriSchemeHttp &&
            (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        if (!validUrl || (uri!.Scheme != Uri.UriSchemeHttps && !isLoopbackHttp))
            throw new ArgumentException("Base URL 必须是 HTTPS 地址（本机 localhost 可使用 HTTP）", nameof(request));
        var models = (request.Models ?? [])
            .Select(model => model?.Trim() ?? string.Empty)
            .Where(model => model.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (models.Count == 0) throw new ArgumentException("至少添加一个 AI 模型", nameof(request));

        var profiles = await _repository.GetProviderProfilesAsync(cancellationToken);
        var lookupName = string.IsNullOrWhiteSpace(request.ExistingName) ? name : request.ExistingName.Trim();
        var existing = profiles.FirstOrDefault(item => item.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase));
        if (profiles.Any(item => !item.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("已存在同名服务商配置", nameof(request));
        var encryptedKey = string.IsNullOrWhiteSpace(request.ApiKey)
            ? existing?.ProtectedApiKey ?? string.Empty
            : _providerKeyProtector.Protect(request.ApiKey.Trim());
        var profile = new StoredProviderProfile
        {
            Name = name,
            BaseUrl = request.BaseUrl.Trim().TrimEnd('/'),
            Models = models,
            Model = models[0],
            ProtectedApiKey = encryptedKey
        };
        await _repository.SaveProviderProfileAsync(profile, existing?.Name, cancellationToken);
        return new ProviderSummary(profile.Name, profile.BaseUrl, models, !string.IsNullOrWhiteSpace(profile.ProtectedApiKey));
    }

    /// <summary>调用 DeepSeek 的 /user/balance 端点并返回账号余额，不向客户端暴露 API Key。</summary>
    public async Task<ProviderBalance> GetProviderBalanceAsync(string name, CancellationToken cancellationToken = default)
    {
        var profiles = await _repository.GetProviderProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("找不到该服务商配置");
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri) ||
            !baseUri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("余额查询目前仅支持 Base URL 为 api.deepseek.com 的 DeepSeek 配置");
        if (string.IsNullOrWhiteSpace(profile.ProtectedApiKey))
            throw new InvalidOperationException("该服务商尚未保存 API Key");

        var apiKey = _providerKeyProtector.Unprotect(profile.ProtectedApiKey);
        var balanceUri = new UriBuilder(baseUri) { Path = "/user/balance", Query = string.Empty, Fragment = string.Empty }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, balanceUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClientFactory.CreateClient("ModelProvider").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "DeepSeek 拒绝了该 API Key，请检查密钥是否有效。"
                : $"DeepSeek 余额查询失败（HTTP {(int)response.StatusCode}）。";
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var isAvailable = root.TryGetProperty("is_available", out var available) && available.ValueKind == JsonValueKind.True;
        var balances = new List<ProviderBalanceInfo>();
        if (root.TryGetProperty("balance_infos", out var balanceInfos) && balanceInfos.ValueKind == JsonValueKind.Array)
        {
            foreach (var balance in balanceInfos.EnumerateArray())
            {
                balances.Add(new ProviderBalanceInfo(
                    ReadString(balance, "currency"),
                    ReadString(balance, "total_balance"),
                    ReadString(balance, "granted_balance"),
                    ReadString(balance, "topped_up_balance")));
            }
        }
        return new ProviderBalance(isAvailable, balances);
    }

    /// <summary>删除用户保存的 Provider 配置及关联的模型清单。</summary>
    public Task<bool> DeleteProviderProfileAsync(string name, CancellationToken cancellationToken = default) => _repository.DeleteProviderProfileAsync(name, cancellationToken);

    /// <summary>读取技能清单。</summary>
    public Task<IReadOnlyList<AgentSkill>> GetSkillsAsync(CancellationToken cancellationToken = default) => _repository.GetSkillsAsync(cancellationToken);

    /// <summary>校验技能字段并写入本地仓储。</summary>
    public Task<AgentSkill> SaveSkillAsync(AgentSkill skill, CancellationToken cancellationToken = default)
    {
        skill.Name = ValidateDisplayName(skill.Name, "技能名称");
        if (string.IsNullOrWhiteSpace(skill.Instructions)) throw new ArgumentException("技能指令不能为空");
        skill.Instructions = skill.Instructions.Trim();
        skill.Description = skill.Description?.Trim() ?? string.Empty;
        return _repository.SaveSkillAsync(skill, cancellationToken);
    }

    /// <summary>删除指定技能。</summary>
    public Task<bool> DeleteSkillAsync(Guid id, CancellationToken cancellationToken = default) => _repository.DeleteSkillAsync(id, cancellationToken);

    /// <summary>读取记忆条目列表。</summary>
    public Task<IReadOnlyList<MemoryEntry>> GetMemoriesAsync(CancellationToken cancellationToken = default) => _repository.GetMemoriesAsync(cancellationToken);

    /// <summary>校验记忆字段并写入本地仓储。</summary>
    public Task<MemoryEntry> SaveMemoryAsync(MemoryEntry memory, CancellationToken cancellationToken = default)
    {
        memory.Title = ValidateDisplayName(memory.Title, "记忆标题");
        if (string.IsNullOrWhiteSpace(memory.Content)) throw new ArgumentException("记忆内容不能为空");
        memory.Content = memory.Content.Trim();
        return _repository.SaveMemoryAsync(memory, cancellationToken);
    }

    /// <summary>删除指定记忆。</summary>
    public Task<bool> DeleteMemoryAsync(Guid id, CancellationToken cancellationToken = default) => _repository.DeleteMemoryAsync(id, cancellationToken);

    /// <summary>读取本地工作参数。</summary>
    public Task<AgentSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => _repository.GetSettingsAsync(cancellationToken);

    /// <summary>限制循环次数和工作区路径后保存本地设置。</summary>
    public async Task<AgentSettings> SaveSettingsAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        await _repository.SaveSettingsAsync(settings, cancellationToken);
        return settings;
    }

    /// <summary>保存用户消息，执行模型与本地工具往返，并流式返回文本、审批、Token 和耗时事件。</summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(Guid sessionId, string prompt, string? providerName = null, string? modelName = null, AgentRunControl? runControl = null, string? connectionId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = await _repository.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException("会话不存在");
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("提示内容不能为空", nameof(prompt));
        var previousInputTokens = session.Messages.Where(message => message.Role == "assistant").Sum(message => message.InputTokens ?? 0);
        var previousOutputTokens = session.Messages.Where(message => message.Role == "assistant").Sum(message => message.OutputTokens ?? 0);

        var userMessage = new AgentMessage { Role = "user", Content = prompt.Trim() };
        var assistantMessage = new AgentMessage { Role = "assistant", Content = string.Empty };
        await _repository.AddMessageAsync(sessionId, userMessage, cancellationToken);

        var provider = await ResolveProviderAsync(providerName, modelName, cancellationToken);
        var runId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        yield return AgentEvent.Started(runId, provider.Name, provider.Model);

        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            const string demoAnswer = "当前使用演示模式。请在后端环境变量中设置 Providers__Default__ApiKey，并配置 BaseUrl、Model，即可启用真实模型。";
            foreach (var chunk in SplitText(demoAnswer, 14))
            {
                assistantMessage.Content += chunk;
                yield return AgentEvent.Message(runId, chunk);
                await Task.Delay(18, cancellationToken);
            }
            await _repository.AddMessageAsync(sessionId, assistantMessage, cancellationToken);
            var demoInputTokens = EstimateTokens(prompt);
            var demoOutputTokens = EstimateTokens(assistantMessage.Content);
            assistantMessage.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            await _repository.UpdateUsageAsync(assistantMessage.Id, demoInputTokens, demoOutputTokens, assistantMessage.ElapsedMilliseconds.Value, null, cancellationToken);
            yield return AgentEvent.UsageUpdate(runId, new UsageSnapshot(previousInputTokens + demoInputTokens, previousOutputTokens + demoOutputTokens, previousInputTokens + previousOutputTokens + demoInputTokens + demoOutputTokens, true));
            yield return AgentEvent.Completed(runId, stopwatch.ElapsedMilliseconds);
            yield break;
        }

        var inputTokens = 0;
        var outputTokens = 0;
        var hasEstimatedUsage = false;
        var enabledSkills = (await _repository.GetSkillsAsync(cancellationToken)).Where(skill => skill.Enabled).ToArray();
        var enabledMemories = (await _repository.GetMemoriesAsync(cancellationToken)).Where(memory => memory.Enabled).ToArray();
        var contextParts = new List<string>();
        if (enabledSkills.Length > 0) contextParts.Add("已启用技能（遵循其指令）：\n" + string.Join("\n\n", enabledSkills.Select(skill => $"## {skill.Name}\n{skill.Instructions}")));
        if (enabledMemories.Length > 0) contextParts.Add("用户本地记忆（仅作背景信息）：\n" + string.Join("\n\n", enabledMemories.Select(memory => $"## {memory.Title}\n{memory.Content}")));
        var project = session.ProjectId is Guid projectId
            ? (await _repository.GetProjectsAsync(cancellationToken)).FirstOrDefault(item => item.Id == projectId)
            : null;
        var selectedWorkspacePath = project?.WorkspacePath ?? session.WorkspacePath;
        var selectedWorkspaceType = project?.WorkspacePathType ?? session.WorkspacePathType;
        var selectedFilePath = selectedWorkspaceType == "file" && !string.IsNullOrWhiteSpace(selectedWorkspacePath)
            ? Path.GetFullPath(selectedWorkspacePath)
            : null;
        if (project is not null)
            contextParts.Add(await BuildProjectContextAsync(project, session.PermissionMode, cancellationToken));
        else if (!string.IsNullOrWhiteSpace(session.WorkspacePath))
            contextParts.Add(await BuildProjectContextAsync(new AgentProject { Name = "当前会话工作区", WorkspacePath = session.WorkspacePath, WorkspacePathType = session.WorkspacePathType }, session.PermissionMode, cancellationToken));
        else
            contextParts.Add(session.PermissionMode == "fullAccess"
                ? "权限模式：完全访问。用户尚未选择工作区，因此不要访问本地文件或执行本地命令；先要求用户选择工作区。"
                : "权限模式：请求用户批准。未获批准前，不执行任何本地工具操作。");
        var workspaceRoot = await ResolveWorkspaceRootAsync(session, cancellationToken);
        var gitSnapshot = workspaceRoot is null ? null : await _gitChangeTracker.CaptureAsync(workspaceRoot, cancellationToken);
        var systemContext = contextParts.Count == 0 ? "你是 Lucas Agent，一个可以通过工具检查和修改用户所选工作区的本地智能助手。" : string.Join("\n\n", contextParts);
        systemContext += "\n\n语言规则（最高优先级）：请用简体中文思考和分析，并始终用简体中文输出面向用户的内容，包括进度说明、澄清问题、解释和最终回答；不要因为用户提示、上下文、项目文件或已启用技能使用英文而切换语言。只有用户明确要求其他语言时，才按要求切换。代码、命令、路径、API 名称、日志和引用原文可以保持原语言，并用中文解释。不要向用户展示隐藏推理。\n\n输出与工具协议：最终回答应简洁、清晰地用 Markdown 表达；不要输出工具调用协议标记（包括 DSML/DMSL，以及插入空格、全角符号或重复分隔符的变体）、序列化参数或伪造的工具结果。需要读取、写入或运行命令时，只能调用本请求提供的标准 function tools；不得把工具调用写进普通回答文本。若当前模型无法使用标准 function tools，应直接说明工具不可用，不要改用文本协议模拟调用。工具调用参数必须符合声明的 JSON Schema。\n\n代码分析证据规则：用户询问项目功能、架构、依赖或实现细节时，先读取能证明结论的源文件；每项具体结论都要能对应到实际读过的文件和实现。项目清单、依赖清单或 README 只能作为线索，不能单独证明功能已经实现。证据不足时明确标为推测或未知，不得补全猜测；总结时列出关键相对文件路径。描述模型兼容范围时，只说明代码实际实现的协议和已验证能力；OpenAI-compatible 不代表每个模型或服务商都支持流式响应、工具调用或思考字段。不得声称读取、运行或验证了实际没有读取、运行或验证的内容。\n\n只要用户的问题依赖当前项目现状，先通过工具读取相关文件，再据实际内容作答，不要只依赖旧上下文。需要修改项目或执行验证时，必须调用工具，而不是只把终端命令或文件操作步骤当作普通文字输出。Windows 命令使用 PowerShell；macOS 和 Linux 命令使用 Bash/Shell。文件工具只接受工作区内相对路径。仅在用户选择工作区时工具可用。";
        if (workspaceRoot is null)
            systemContext += "\n用户当前没有选择工作区。先前对话提到的路径只能作为历史上下文，不能视为仍有文件访问权限；如果用户要求继续在先前/当前路径执行，先请用户重新选择该工作区。";
        else
            systemContext += "\n用户已选择当前工作区。与项目有关的问题应根据此工作区的当前文件回答；用户明确要求在此路径继续执行时，使用当前授权工作区工具。";
        if (workspaceRoot is not null && selectedFilePath is null)
            systemContext += "\n修改工作区文件后，最终回答前必须由你根据项目结构选择并请求执行合适的验证命令（例如测试、构建或 lint）；验证命令须通过 run_command 工具发起。若验证返回非零退出码，应阅读结果，必要时修复并在剩余轮次内重新验证。若用户拒绝或环境无法运行，明确说明未验证，不得声称通过。";
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemContext } };
        foreach (var message in session.Messages.Where(item => item.Content.Length > 0))
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt.Trim() });

        var reasoningProtocolHidden = false;
        var workspaceChanged = false;
        var validationAttempted = false;
        var validationFailed = false;
        for (var iteration = 0; iteration < 13; iteration++)
        {
            int contentLengthBeforeIteration = assistantMessage.Content.Length;
            int reasoningLengthBeforeIteration = assistantMessage.Reasoning.Length;
            var toolCalls = new SortedDictionary<int, JsonObject>();
            var assistantContent = new StringBuilder();
            var assistantReasoning = new StringBuilder();
            var providerReasoningContent = new StringBuilder();
            var unsupportedProtocolInContent = false;
            var unsupportedContentHidden = false;
            var requestInputTokens = 0;
            var requestOutputTokens = 0;
            var requestHasProviderUsage = false;
            var validationOnlyIteration = iteration is >= 9 and <= 11 && workspaceChanged && (!validationAttempted || validationFailed) && selectedFilePath is null;
            JsonArray? availableTools = workspaceRoot is null || iteration == 12
                ? null
                    : validationOnlyIteration
                        ? CreateToolDefinitions(singleFileWorkspace: false)
                    : iteration < 9 ? CreateToolDefinitions(selectedFilePath is not null) : null;
            using var request = BuildRequest(provider, messages, availableTools);
            using var response = await _httpClientFactory.CreateClient("ModelProvider").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"模型服务返回 {(int)response.StatusCode}: {Truncate(errorBody, 900)}", null, response.StatusCode);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var reader = new StreamReader(stream))
            {
                while (true)
                {
                    if (runControl is not null) await runControl.WaitIfPausedAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].Trim();
                    if (data.Length == 0 || data == "[DONE]") continue;
                    using var json = JsonDocument.Parse(data);
                    var root = json.RootElement;
                    if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                    {
                        var reportedInputTokens = ReadInt(usageElement, "prompt_tokens");
                        var reportedOutputTokens = ReadInt(usageElement, "completion_tokens");
                        if (reportedInputTokens > 0 || reportedOutputTokens > 0)
                        {
                            // Streaming providers may send multiple cumulative usage snapshots; keep the latest
                            // value for this HTTP request and add it only once after the stream ends.
                            requestInputTokens = reportedInputTokens;
                            requestOutputTokens = reportedOutputTokens;
                            requestHasProviderUsage = true;
                            yield return AgentEvent.UsageUpdate(runId, new UsageSnapshot(
                                previousInputTokens + inputTokens + requestInputTokens,
                                previousOutputTokens + outputTokens + requestOutputTokens,
                                previousInputTokens + previousOutputTokens + inputTokens + outputTokens + requestInputTokens + requestOutputTokens,
                                hasEstimatedUsage));
                        }
                    }
                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].TryGetProperty("delta", out var deltaElement) ? deltaElement : default;
                    if (delta.ValueKind != JsonValueKind.Object) continue;
                    var hasProviderReasoningContent = TryReadText(delta, "reasoning_content", out var reasoning);
                    if (hasProviderReasoningContent)
                        providerReasoningContent.Append(reasoning);
                    if (hasProviderReasoningContent || TryReadText(delta, "reasoning", out reasoning))
                    {
                        assistantReasoning.Append(reasoning);
                        if (!reasoningProtocolHidden)
                        {
                            if (ContainsUnsupportedToolProtocol(assistantReasoning.ToString()))
                            {
                                reasoningProtocolHidden = true;
                                const string reasoningNotice = "检测到模型在思考流中输出内部工具协议，已隐藏该过程内容。";
                                assistantMessage.Reasoning = assistantMessage.Reasoning[..reasoningLengthBeforeIteration] + reasoningNotice;
                                yield return AgentEvent.ReplaceReasoning(runId, assistantMessage.Reasoning);
                            }
                            else
                            {
                                assistantMessage.Reasoning += reasoning;
                                yield return AgentEvent.Reasoning(runId, reasoning);
                            }
                        }
                    }
                    if (TryReadText(delta, "content", out var content))
                    {
                        assistantContent.Append(content);
                        if (!unsupportedContentHidden)
                        {
                            assistantMessage.Content += content;
                            yield return AgentEvent.Message(runId, content);
                            if (ContainsUnsupportedToolProtocol(assistantContent.ToString()))
                            {
                                unsupportedProtocolInContent = true;
                                unsupportedContentHidden = true;
                                assistantMessage.Content = assistantMessage.Content[..contentLengthBeforeIteration];
                                yield return AgentEvent.ReplaceMessage(runId, assistantMessage.Content);
                            }
                        }
                    }
                    if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fragment in calls.EnumerateArray())
                        {
                            var index = fragment.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex) ? parsedIndex : 0;
                            if (!toolCalls.TryGetValue(index, out var accumulated)) toolCalls[index] = accumulated = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["arguments"] = "" } };
                            if (TryReadText(fragment, "id", out var callId)) accumulated["id"] ??= callId;
                            if (fragment.TryGetProperty("function", out var function))
                            {
                                var functionObject = accumulated["function"]!.AsObject();
                                if (TryReadText(function, "name", out var name)) functionObject["name"] = (functionObject["name"]?.GetValue<string>() ?? "") + name;
                                if (TryReadText(function, "arguments", out var arguments)) functionObject["arguments"] = functionObject["arguments"]!.GetValue<string>() + arguments;
                            }
                        }
                    }
                }
            }
            if (requestHasProviderUsage)
            {
                inputTokens += requestInputTokens;
                outputTokens += requestOutputTokens;
            }
            else
            {
                hasEstimatedUsage = true;
                inputTokens += EstimateTokens(messages.ToJsonString());
                outputTokens += EstimateTokens(assistantContent.ToString() + assistantReasoning + string.Join("", toolCalls.Values.Select(call => call["function"]?["arguments"]?.GetValue<string>() ?? string.Empty)));
            }
            if (unsupportedProtocolInContent || ContainsUnsupportedToolProtocol(assistantContent.ToString()))
            {
                // DeepSeek models can expose their documented DSML tool-call envelope as content
                // instead of structured OpenAI tool_calls. Parse only complete, allow-listed calls.
                if (TryParseDeepSeekDsml(assistantContent.ToString(), toolCalls, out var visibleText))
                {
                    assistantMessage.Content = assistantMessage.Content[..contentLengthBeforeIteration] + visibleText;
                    yield return AgentEvent.ReplaceMessage(runId, assistantMessage.Content);
                }
                else if (toolCalls.Count == 0)
                {
                    // Unknown or malformed protocols are never treated as executable commands.
                    const string compatibilityNotice = "当前模型返回了不兼容的工具调用格式，本次没有执行其中的命令。请改用支持标准 function calling（tool_calls）的模型或服务商后重试。";
                    assistantMessage.Content = compatibilityNotice;
                    yield return AgentEvent.ReplaceMessage(runId, compatibilityNotice);
                    break;
                }
                else
                {
                    assistantMessage.Content = assistantMessage.Content[..contentLengthBeforeIteration];
                    yield return AgentEvent.ReplaceMessage(runId, assistantMessage.Content);
                }
            }
            if (toolCalls.Count == 0)
            {
                if (validationOnlyIteration && iteration is 9 or 10)
                {
                    var draft = assistantContent.ToString();
                    assistantMessage.Content = assistantMessage.Content[..contentLengthBeforeIteration];
                    yield return AgentEvent.ReplaceMessage(runId, assistantMessage.Content);
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = draft });
                    messages.Add(new JsonObject { ["role"] = "system", ["content"] = "你已修改工作区文件，但还没有调用验证命令。现在只需根据项目实际配置选择一个合适的测试、构建或 lint 命令，并通过 run_command 工具发起；不要重复编辑文件。若无法验证或用户拒绝，明确说明。" });
                    yield return AgentEvent.ToolStatus(runId, "修改后验证", "started", "正在要求模型选择适当的验证命令。");
                    continue;
                }
                break;
            }
            foreach (var call in toolCalls.Values)
            {
                // Some OpenAI-compatible streams omit these fields on malformed or
                // truncated tool-call deltas. Keep the assistant/tool result IDs
                // paired and make the serialized arguments valid JSON.
                if (string.IsNullOrWhiteSpace(call["id"]?.GetValue<string>()))
                    call["id"] = Guid.NewGuid().ToString("N");
                var function = call["function"]?.AsObject();
                if (function is not null && string.IsNullOrWhiteSpace(function["arguments"]?.GetValue<string>()))
                    function["arguments"] = "{}";
            }
            var assistantToolMessage = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = assistantContent.Length == 0 ? null : assistantContent.ToString(),
                ["tool_calls"] = new JsonArray(toolCalls.Values.Select(call => (JsonNode?)call.DeepClone()).ToArray())
            };
            if (providerReasoningContent.Length > 0)
                assistantToolMessage["reasoning_content"] = providerReasoningContent.ToString();
            messages.Add(assistantToolMessage);
            foreach (var call in toolCalls.Values)
            {
                if (runControl is not null) await runControl.WaitIfPausedAsync(cancellationToken);
                var callId = call["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                var function = call["function"]!.AsObject();
                var toolName = function["name"]?.GetValue<string>() ?? "";
                var arguments = function["arguments"]?.GetValue<string>() ?? "{}";
                var details = FormatToolDetails(toolName, arguments, workspaceRoot!);
                if (selectedFilePath is not null)
                    details += $"\n文件授权范围：仅可访问 {Path.GetRelativePath(workspaceRoot!, selectedFilePath)}；不允许运行终端命令。";
                yield return AgentEvent.ToolStatus(runId, toolName, "started", details);
                string toolResult;
                if (session.PermissionMode == "fullAccess")
                    toolResult = await ExecuteWorkspaceToolAsync(toolName, arguments, workspaceRoot!, cancellationToken, selectedFilePath);
                else if (string.IsNullOrWhiteSpace(connectionId))
                    toolResult = "工具调用未执行：没有可用于审批的桌面连接。";
                else
                {
                    var approval = _approvalBroker.Begin(connectionId);
                    yield return AgentEvent.ApprovalRequired(runId, approval.RequestId, toolName, details);
                    var approved = await _approvalBroker.WaitAsync(approval.RequestId, approval.Result, cancellationToken);
                    toolResult = approved
                        ? await ExecuteWorkspaceToolAsync(toolName, arguments, workspaceRoot!, cancellationToken, selectedFilePath)
                        : "用户拒绝或未批准了这次工具调用。不要重复尝试该操作；请告知用户并询问下一步。";
                }
                yield return AgentEvent.ToolStatus(runId, toolName, "completed", details);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = toolResult });
                if (toolName == "write_file" && toolResult.StartsWith("已写入 ", StringComparison.Ordinal))
                {
                    workspaceChanged = true;
                    validationAttempted = false;
                    validationFailed = false;
                }
                else if (toolName == "run_command" && workspaceChanged)
                {
                    validationAttempted = true;
                    validationFailed = !HasSuccessfulCommandExitCode(toolResult);
                }
            }
        }

        if (workspaceChanged && selectedFilePath is null && (!validationAttempted || validationFailed))
        {
            assistantMessage.Content += validationFailed
                ? "\n\n验证命令失败或未能正常完成；本轮改动尚未确认通过，请检查上方命令输出。"
                : "\n\n工作区文件已修改，但没有成功发起验证命令；本轮改动尚未验证。";
            yield return AgentEvent.ReplaceMessage(runId, assistantMessage.Content);
        }
        await _repository.AddMessageAsync(sessionId, assistantMessage, cancellationToken);
        var gitDiff = workspaceRoot is null ? null : await _gitChangeTracker.GetDiffAsync(gitSnapshot, workspaceRoot, cancellationToken);
        if (gitDiff is not null) yield return AgentEvent.GitDiff(runId, gitDiff);
        assistantMessage.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        await _repository.UpdateUsageAsync(assistantMessage.Id, inputTokens, outputTokens, assistantMessage.ElapsedMilliseconds.Value, gitDiff, cancellationToken);
        yield return AgentEvent.UsageUpdate(runId, new UsageSnapshot(previousInputTokens + inputTokens, previousOutputTokens + outputTokens, previousInputTokens + previousOutputTokens + inputTokens + outputTokens, hasEstimatedUsage));
        yield return AgentEvent.Completed(runId, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>仅当终端工具明确返回退出码零时，才将验证视为成功。</summary>
    internal static bool HasSuccessfulCommandExitCode(string toolResult)
    {
        var match = Regex.Match(toolResult, @"退出码：\s*(-?\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var exitCode) && exitCode == 0;
    }

    /// <summary>保存被用户停止的部分回答与思考，并写入估算 Token 和已用时。</summary>
    public async Task SaveInterruptedRunAsync(Guid sessionId, string content, string reasoning, DateTimeOffset runStartedAt, long elapsedMilliseconds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(reasoning)) return;
        var session = await _repository.GetSessionAsync(sessionId, cancellationToken);
        if (session is null || session.Messages.Any(message => message.Role == "assistant" && message.CreatedAt >= runStartedAt)) return;
        var message = new AgentMessage
        {
            Role = "assistant",
            Content = content,
            Reasoning = reasoning,
            ElapsedMilliseconds = elapsedMilliseconds
        };
        await _repository.AddMessageAsync(sessionId, message, cancellationToken);
        var inputTokens = EstimateTokens(string.Join("\n", session.Messages.Select(item => item.Content)));
        var outputTokens = EstimateTokens(content);
        await _repository.UpdateUsageAsync(message.Id, inputTokens, outputTokens, elapsedMilliseconds, null, cancellationToken);
    }

    /// <summary>读取项目选定文件或目录的有限结构信息，供模型判断技术栈及合适的验证方式。</summary>
    private static async Task<string> BuildProjectContextAsync(AgentProject project, string permissionMode, CancellationToken cancellationToken)
    {
        var parts = new List<string> { $"当前项目：{project.Name}\n资源类型：{(project.WorkspacePathType == "file" ? "指定文件" : "工作目录")}\n绝对路径：{project.WorkspacePath}" };
        parts.Add(permissionMode == "fullAccess"
            ? "权限模式：用户已选择 AI 完全访问。工作区文件工具限制在所选目录内；终端命令以当前桌面用户权限运行，操作系统不会自动把命令限制在工作区内。涉及破坏性操作或外部副作用时仍需谨慎并明确告知用户。"
            : "权限模式：请求用户批准。任何本地读写或命令操作前，必须先向用户说明具体动作并等待批准。");
        if (permissionMode != "fullAccess")
        {
            parts.Add(project.WorkspacePathType == "file"
                ? "当前为请求批准模式，不会预先读取所选文件。仅可列出、读取或写入用户指定的这个文件；不能访问同目录的其他文件，也不能运行终端命令。需要使用本地工具时必须逐次展示具体动作并等待桌面端明确批准。"
                : "当前为请求批准模式，不会预先读取此工作区中的文件或目录清单。需要使用本地工具时必须逐次向用户展示具体动作并等待桌面端明确批准；拒绝时不得执行。");
            return string.Join("\n\n", parts);
        }
        if (project.WorkspacePathType == "file")
        {
            if (!File.Exists(project.WorkspacePath)) return string.Join("\n\n", parts) + "\n所选工作文件当前不存在。不要声称已检查或执行项目。";
            var content = await File.ReadAllTextAsync(project.WorkspacePath, cancellationToken);
            parts.Add($"唯一授权文件 {Path.GetFileName(project.WorkspacePath)}（最多读取 8,000 字符）：\n{content[..Math.Min(content.Length, 8000)]}");
            parts.Add("当前工作区是单文件模式：只能列出、读取或写入这个指定文件，不能访问同目录其他文件，也不能运行终端命令。用户需要目录级操作时，请让其改选工作目录。");
            return string.Join("\n\n", parts);
        }
        var manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "package.json", "pnpm-lock.yaml", "yarn.lock", "package-lock.json", "*.sln", "*.slnx", "*.csproj",
            "Cargo.toml", "pyproject.toml", "requirements.txt", "go.mod", "pom.xml", "build.gradle", "Gemfile", "composer.json"
        };
        try
        {
            var directory = project.WorkspacePathType == "file" ? Path.GetDirectoryName(project.WorkspacePath) : project.WorkspacePath;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return string.Join("\n", parts) + "\n工作区路径当前不可读取。不要声称已检查或执行项目。";
            var files = Directory.EnumerateFileSystemEntries(directory).Take(80).ToArray();
            parts.Add("工作区顶层条目（最多 80 项）：\n" + string.Join("\n", files.Select(Path.GetFileName)));
            var selectedFile = project.WorkspacePathType == "file" ? project.WorkspacePath : null;
            var contextFiles = files.Where(path => manifests.Any(pattern => pattern.StartsWith("*.", StringComparison.Ordinal) ? Path.GetFileName(path).EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) : Path.GetFileName(path).Equals(pattern, StringComparison.OrdinalIgnoreCase))).ToList();
            if (selectedFile is not null) contextFiles.Insert(0, selectedFile);
            foreach (var path in contextFiles.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path)) continue;
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                parts.Add($"文件摘要来源 {Path.GetFileName(path)}（截取最多 8,000 字符）：\n{content[..Math.Min(content.Length, 8000)]}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            parts.Add($"读取项目结构失败：{exception.Message}");
        }
        parts.Add("请根据实际项目文件和用户任务自行判断需要的检查/测试方式；有工具权限时通过工具真实执行。没有实际运行工具时，不得声称验证已通过。");
        return string.Join("\n\n", parts);
    }

    /// <summary>根据配置构建聊天补全的 JSON POST 请求和 Bearer 认证头。</summary>
    private static HttpRequestMessage BuildRequest(ProviderOptions provider, JsonArray messages, JsonArray? tools)
    {
        var body = new JsonObject { ["model"] = provider.Model, ["stream"] = true, ["messages"] = messages.DeepClone() };
        if (tools is not null) { body["tools"] = tools.DeepClone(); body["tool_choice"] = "auto"; }
        var payload = body.ToJsonString();
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : $"{baseUrl}/chat/completions";
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    /// <summary>根据会话项目关联或单独选择的路径确定工具工作目录。</summary>
    private async Task<string?> ResolveWorkspaceRootAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var project = session.ProjectId is Guid projectId
            ? (await _repository.GetProjectsAsync(cancellationToken)).FirstOrDefault(item => item.Id == projectId)
            : null;
        var path = project?.WorkspacePath ?? session.WorkspacePath;
        var type = project?.WorkspacePathType ?? session.WorkspacePathType;
        if (string.IsNullOrWhiteSpace(path)) return null;
        var root = type == "file" ? Path.GetDirectoryName(path) : path;
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? Path.GetFullPath(root) : null;
    }

    /// <summary>定义 Agent 可请求的工作区文件列表、读取、写入及系统终端工具。</summary>
    private static JsonArray CreateToolDefinitions(bool singleFileWorkspace)
    {
        var tools = JsonNode.Parse("""
    [
      {"type":"function","function":{"name":"list_files","description":"列出工作区内的文件相对路径；可选指定目录。","parameters":{"type":"object","properties":{"path":{"type":"string","description":"相对目录路径，默认为工作区根目录"}},"additionalProperties":false}}},
      {"type":"function","function":{"name":"read_file","description":"读取工作区中的文本文件。","parameters":{"type":"object","properties":{"path":{"type":"string","description":"工作区内相对文件路径"}},"required":["path"],"additionalProperties":false}}},
      {"type":"function","function":{"name":"write_file","description":"在工作区内创建或覆盖文本文件。","parameters":{"type":"object","properties":{"path":{"type":"string","description":"工作区内相对文件路径"},"content":{"type":"string","description":"完整文件内容"}},"required":["path","content"],"additionalProperties":false}}},
      {"type":"function","function":{"name":"run_command","description":"在工作区目录运行命令。Windows 必须使用 PowerShell 语法；macOS/Linux 使用 Bash/Shell 语法。命令以当前桌面用户权限运行。","parameters":{"type":"object","properties":{"command":{"type":"string","description":"当前系统可执行的命令"}},"required":["command"],"additionalProperties":false}}}
    ]
    """)!.AsArray();

        if (singleFileWorkspace) tools.RemoveAt(3);
        return tools;
    }

    /// <summary>检测模型正文中疑似未结构化输出的工具协议标记。</summary>
    private static bool ContainsUnsupportedToolProtocol(string content)
    {
        // Some model gateways insert spaces or full-width separators between visible token parts.
        var normalized = Regex.Replace(content, @"[\s\u200B-\u200D\uFEFF]", string.Empty)
            .Replace('｜', '|')
            .Replace('＜', '<')
            .Replace('＞', '>');
        return Regex.IsMatch(
            normalized,
            @"<\|+(?:DSML|DMSL)\|+(?:CALLS|FUNCTION_CALLS|TOOL_CALLS|INVOKE)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>把 DeepSeek DSML 内容中的完整工具调用转换为内部 OpenAI tool_calls 结构。</summary>
    private static bool TryParseDeepSeekDsml(string content, SortedDictionary<int, JsonObject> toolCalls, out string visibleText)
    {
        var normalized = content.Replace('｜', '|').Replace('＜', '<').Replace('＞', '>');
        var block = Regex.Match(normalized,
            @"<\|+DSML\|+(?:function_calls|tool_calls|toolcalls)\s*>(?<body>.*?)</\|+DSML\|+(?:function_calls|tool_calls|toolcalls)\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        visibleText = normalized;
        if (!block.Success) return false;

        var parsedCalls = new List<JsonObject>();
        var invokeMatches = Regex.Matches(block.Groups["body"].Value,
            @"<\|+DSML\|+invoke\s+name=""(?<name>[^""]+)""\s*>(?<parameters>.*?)</\|+DSML\|+invoke\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (invokeMatches.Count == 0) return false;

        var allowedTools = new HashSet<string>(["list_files", "read_file", "write_file", "run_command"], StringComparer.Ordinal);
        var body = block.Groups["body"].Value;
        var nextBodyIndex = 0;
        foreach (Match invoke in invokeMatches)
        {
            if (!string.IsNullOrWhiteSpace(body[nextBodyIndex..invoke.Index])) return false;
            nextBodyIndex = invoke.Index + invoke.Length;
            var name = invoke.Groups["name"].Value.Trim();
            if (!allowedTools.Contains(name)) return false;
            var arguments = new JsonObject();
            var parameterText = invoke.Groups["parameters"].Value;
            var parameterMatches = Regex.Matches(parameterText,
                @"<\|+DSML\|+parameter\s+name=""(?<name>[^""]+)""\s+string=""(?<string>true|false)""\s*>(?<value>.*?)</\|+DSML\|+parameter\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (parameterMatches.Count != Regex.Matches(parameterText, @"<\|+DSML\|+parameter\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count)
                return false;
            foreach (Match parameter in parameterMatches)
            {
                var parameterName = parameter.Groups["name"].Value;
                var rawValue = parameter.Groups["value"].Value.Trim();
                if (arguments.ContainsKey(parameterName)) return false;
                try
                {
                    arguments[parameterName] = parameter.Groups["string"].Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                        ? JsonValue.Create(rawValue)
                        : JsonNode.Parse(rawValue);
                }
                catch (JsonException)
                {
                    return false;
                }
            }
            parsedCalls.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() }
            });
        }

        if (!string.IsNullOrWhiteSpace(body[nextBodyIndex..]) ||
            invokeMatches.Count != Regex.Matches(body, @"<\|+DSML\|+invoke\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count)
            return false;
        foreach (var call in parsedCalls) toolCalls[toolCalls.Count] = call;
        visibleText = content.Remove(block.Index, block.Length).Trim();
        return true;
    }

    /// <summary>按工具类型生成审批弹窗中展示的动作摘要及真实工作目录。</summary>
    private static string FormatToolDetails(string toolName, string argumentsJson, string workspaceRoot)
    {
        try
        {
            using var json = JsonDocument.Parse(argumentsJson);
            var details = toolName switch
            {
                "run_command" => $"工作目录：{workspaceRoot}\n终端：{(OperatingSystem.IsWindows() ? "PowerShell" : "Bash/Shell")}\n命令：{json.RootElement.GetProperty("command").GetString()}",
                "write_file" => $"工作目录：{workspaceRoot}\n写入文件：{json.RootElement.GetProperty("path").GetString()}",
                "read_file" => $"工作目录：{workspaceRoot}\n读取文件：{json.RootElement.GetProperty("path").GetString()}",
                _ => $"工作目录：{workspaceRoot}\n列出文件：{(json.RootElement.TryGetProperty("path", out var path) ? path.GetString() : ".")}"
            };
            return details;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return $"工具：{toolName}\n参数：{Truncate(argumentsJson, 2000)}";
        }
    }

    /// <summary>执行工作区工具，并把参数、路径或文件系统异常转换为模型可处理的工具结果。</summary>
    private async Task<string> ExecuteWorkspaceToolAsync(string toolName, string arguments, string workspaceRoot, CancellationToken cancellationToken, string? selectedFilePath = null)
    {
        try { return await _toolExecutor.ExecuteAsync(toolName, arguments, workspaceRoot, cancellationToken, selectedFilePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            return $"工具执行失败：{exception.Message}";
        }
    }

    /// <summary>从本地 Provider 清单解析指定服务商、模型与解密后的 API Key。</summary>
    private async Task<ProviderOptions> ResolveProviderAsync(string? providerName, string? modelName, CancellationToken cancellationToken)
    {
        var profiles = await _repository.GetProviderProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("找不到该服务商配置，请先在模型菜单中添加服务商和模型");
        var selectedModel = GetModelNames(profile).FirstOrDefault(item => item.Equals(modelName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("该模型不属于所选服务商，请刷新模型列表后重试");
        return new ProviderOptions
        {
            Name = profile.Name,
            BaseUrl = profile.BaseUrl,
            Model = selectedModel,
            ApiKey = string.IsNullOrWhiteSpace(profile.ProtectedApiKey) ? string.Empty : _providerKeyProtector.Unprotect(profile.ProtectedApiKey),
            Enabled = true
        };
    }

    /// <summary>读取新格式模型集合，并兼容旧工作区中单模型字段。</summary>
    private static IReadOnlyList<string> GetModelNames(StoredProviderProfile profile) =>
        profile.Models.Count > 0 ? profile.Models : string.IsNullOrWhiteSpace(profile.Model) ? [] : [profile.Model];

    /// <summary>验证用户可见名称非空且不超过 100 个字符。</summary>
    private static string ValidateDisplayName(string value, string fieldName)
    {
        var name = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"{fieldName}不能为空");
        if (name.Length > 100) throw new ArgumentException($"{fieldName}不能超过 100 个字符");
        return name;
    }

    /// <summary>读取 JSON 对象中的整数 Token 统计，缺失时返回零。</summary>
    private static int ReadInt(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result) ? result : 0;

    /// <summary>读取 JSON 字符串属性；缺失或格式不符时返回空字符串。</summary>
    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>读取增量字段中的字符串内容。</summary>
    private static bool TryReadText(JsonElement element, string propertyName, out string text)
    {
        text = string.Empty;
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && (text = value.GetString() ?? string.Empty).Length > 0;
    }

    /// <summary>用 Unicode 字符数近似估算 Token；仅在 Provider 未返回 usage 时使用。</summary>
    private static int EstimateTokens(string text) => Math.Max(1, (int)Math.Ceiling(text.EnumerateRunes().Count() * 0.65));

    /// <summary>按 Unicode 字符边界拆分演示响应文本。</summary>
    private static IEnumerable<string> SplitText(string text, int size)
    {
        for (var index = 0; index < text.Length; index += size) yield return text.Substring(index, Math.Min(size, text.Length - index));
    }

    /// <summary>限制上游错误消息长度，避免把过大的服务响应传入事件流。</summary>
    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
