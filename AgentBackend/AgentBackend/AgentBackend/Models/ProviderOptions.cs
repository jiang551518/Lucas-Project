namespace AgentBackend.Models;

/// <summary>当前接入的 OpenAI-compatible 模型服务配置。</summary>
public sealed class ProviderOptions
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}
