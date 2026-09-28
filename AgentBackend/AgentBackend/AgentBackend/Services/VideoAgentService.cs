using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBackend.Models;
using AgentBackend.Repositories;
using Microsoft.AspNetCore.DataProtection;

namespace AgentBackend.Services;

/// <summary>把提示词注入内置 H3 模板，并通过本地 ComfyUI API 或远程 Comfy API Proxy 提交任务。</summary>
public sealed class VideoAgentService
{
    private const string DefaultBaseUrl = "http://127.0.0.1:8188";
    private const string PromptNodeId = "140:131";
    private const string WorkflowResource = "AgentBackend.workflows.minimax-h3-t2v-api.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IAgentRepository _repository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _tokenProtector;

    public VideoAgentService(IAgentRepository repository, IHttpClientFactory httpClientFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _tokenProtector = dataProtectionProvider.CreateProtector("Lucas.Agent.VideoApiToken.v1");
    }

    public async Task<VideoAgentConfigSummary> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken);
        return new VideoAgentConfigSummary(config.BaseUrl, !string.IsNullOrWhiteSpace(config.ProtectedApiToken));
    }

    public async Task<VideoAgentConfigSummary> SaveConfigAsync(SaveVideoAgentConfigRequest request, CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken);
        config.BaseUrl = ValidateBaseUrl(request.BaseUrl);
        if (!string.IsNullOrWhiteSpace(request.ApiToken))
            config.ProtectedApiToken = _tokenProtector.Protect(request.ApiToken.Trim());
        await _repository.SaveVideoAgentConfigAsync(config, cancellationToken);
        return new VideoAgentConfigSummary(config.BaseUrl, !string.IsNullOrWhiteSpace(config.ProtectedApiToken));
    }

    public async Task<JsonElement> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken);
        var path = IsLocal(config) ? "system_stats" : "api/v2/health";
        using var request = CreateApiRequest(HttpMethod.Get, BuildApiUri(config, path), config);
        using var response = await _httpClientFactory.CreateClient("VideoApi").SendAsync(request, cancellationToken);
        var result = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"H3 API 连接失败（HTTP {(int)response.StatusCode}）：{TrimError(result)}", null, response.StatusCode);
        return ParseJson(result);
    }

    public async Task<JsonElement> SubmitJobAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("视频提示词不能为空。");
        if (prompt.Length > 12000) throw new ArgumentException("视频提示词不能超过 12000 个字符。");

        var config = await ReadConfigAsync(cancellationToken);
        var workflow = await LoadWorkflowAsync(cancellationToken);
        if (workflow[PromptNodeId] is not JsonObject node || node["inputs"] is not JsonObject inputs || !inputs.ContainsKey("prompt"))
            throw new InvalidOperationException("内置 H3 模板结构不匹配，请更新 Lucas Agent。");
        inputs["prompt"] = prompt;
        if (workflow["140:133"] is JsonObject durationNode && durationNode["inputs"] is JsonObject durationInputs)
            durationInputs["value"] = 10;

        var local = IsLocal(config);
        var body = local
            ? new JsonObject { ["prompt"] = workflow, ["client_id"] = Guid.NewGuid().ToString("N") }
            : new JsonObject { ["workflow"] = workflow };
        var path = local ? "prompt" : "api/v2/jobs";
        using var request = CreateApiRequest(HttpMethod.Post, BuildApiUri(config, path), config);
        request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClientFactory.CreateClient("VideoApi").SendAsync(request, cancellationToken);
        var result = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(local ? "本机" : "远程")} H3 提交失败（HTTP {(int)response.StatusCode}）：{TrimError(result)}", null, response.StatusCode);
        return ParseJson(result);
    }

    public async Task<JsonElement> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken);
        var local = IsLocal(config);
        var validatedId = ValidateJobId(jobId);
        var path = local ? $"api/jobs/{validatedId}" : $"api/v2/jobs/{validatedId}";
        var uri = BuildApiUri(config, path);
        using var request = CreateApiRequest(HttpMethod.Get, uri, config);
        using var response = await _httpClientFactory.CreateClient("VideoApi").SendAsync(request, cancellationToken);
        var result = await response.Content.ReadAsStringAsync(cancellationToken);
        if (local && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var historyUri = BuildApiUri(config, $"history/{validatedId}");
            using var historyRequest = CreateApiRequest(HttpMethod.Get, historyUri, config);
            using var historyResponse = await _httpClientFactory.CreateClient("VideoApi").SendAsync(historyRequest, cancellationToken);
            result = await historyResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!historyResponse.IsSuccessStatusCode)
                throw new HttpRequestException($"读取本机 H3 任务失败（HTTP {(int)historyResponse.StatusCode}）：{TrimError(result)}", null, historyResponse.StatusCode);
            var history = JsonNode.Parse(result) as JsonObject;
            if (history is not null && history[jobId] is JsonObject completed)
                return JsonSerializer.SerializeToElement(completed, JsonOptions);
            return JsonSerializer.SerializeToElement(new { status = "running", prompt_id = jobId }, JsonOptions);
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"读取{(local ? "本机" : "远程")} H3 任务失败（HTTP {(int)response.StatusCode}）：{TrimError(result)}", null, response.StatusCode);
        return ParseJson(result);
    }

    public async Task<JsonElement> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken);
        var validatedId = ValidateJobId(jobId);
        var path = IsLocal(config) ? $"api/jobs/{validatedId}/cancel" : $"api/v2/jobs/{validatedId}/cancel";
        using var request = CreateApiRequest(HttpMethod.Post, BuildApiUri(config, path), config);
        using var response = await _httpClientFactory.CreateClient("VideoApi").SendAsync(request, cancellationToken);
        var result = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"终止 H3 任务失败（HTTP {(int)response.StatusCode}）：{TrimError(result)}", null, response.StatusCode);
        return ParseJson(result);
    }

    public async Task<HttpResponseMessage> OpenJobEventsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken);
        if (IsLocal(config)) throw new InvalidOperationException("本机 ComfyUI 暂不提供 H3 任务 SSE 进度流，请使用状态轮询。");
        var uri = BuildApiUri(config, $"api/v2/jobs/{ValidateJobId(jobId)}/events");
        var request = CreateApiRequest(HttpMethod.Get, uri, config);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        try
        {
            return await _httpClientFactory.CreateClient("VideoEvents").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private async Task<StoredVideoAgentConfig> ReadConfigAsync(CancellationToken cancellationToken)
    {
        var config = await _repository.GetVideoAgentConfigAsync(cancellationToken) ?? new StoredVideoAgentConfig();
        if (string.IsNullOrWhiteSpace(config.BaseUrl)) config.BaseUrl = DefaultBaseUrl;
        try { config.BaseUrl = ValidateBaseUrl(config.BaseUrl); }
        catch (ArgumentException) { config.BaseUrl = DefaultBaseUrl; }
        return config;
    }

    private static async Task<JsonObject> LoadWorkflowAsync(CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(WorkflowResource)
            ?? throw new InvalidOperationException("Lucas Agent 未包含 H3 工作流资源。");
        var workflow = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
            ?? throw new InvalidOperationException("内置 H3 工作流 JSON 格式无效。");
        return workflow;
    }

    private static bool IsLocal(StoredVideoAgentConfig config) => Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static string ValidateBaseUrl(string? value)
    {
        var text = value?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new ArgumentException("请填写有效的 H3 API 地址。");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("API 地址只填写协议、域名和端口，不要附加路径或查询参数。");
        if (uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp) return text;
        if (uri.Scheme == Uri.UriSchemeHttps) return text;
        throw new ArgumentException("本机地址使用 HTTP loopback；远程 H3 地址必须使用 HTTPS。");
    }

    private string UnprotectToken(StoredVideoAgentConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ProtectedApiToken))
            throw new InvalidOperationException("远程模式需要 Comfy API Proxy Token，请在视频设置中粘贴 Token 并保存。");
        return _tokenProtector.Unprotect(config.ProtectedApiToken);
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, Uri uri, StoredVideoAgentConfig config)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!IsLocal(config)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", UnprotectToken(config));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static Uri BuildApiUri(StoredVideoAgentConfig config, string resource) => new($"{ValidateBaseUrl(config.BaseUrl)}/{resource}");

    private static string ValidateJobId(string jobId)
    {
        if (jobId.Length is < 1 or > 100 || !Regex.IsMatch(jobId, "^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException("H3 任务 ID 格式无效。");
        return Uri.EscapeDataString(jobId);
    }

    private static JsonElement ParseJson(string content)
    {
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private static string TrimError(string content)
    {
        var text = content.Trim();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}
