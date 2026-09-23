using AgentBackend.Services;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.SignalR;

namespace AgentBackend.Hubs;

/// <summary>为前端提供按会话运行 Agent 的 SignalR 流式通道。</summary>
public sealed class AgentHub : Hub
{
    private readonly IAgentService _agentService;
    private readonly ToolApprovalBroker _approvalBroker;
    private readonly AgentRunRegistry _runRegistry;

    /// <summary>注入 Agent 应用服务。</summary>
    public AgentHub(IAgentService agentService, ToolApprovalBroker approvalBroker, AgentRunRegistry runRegistry)
    {
        _agentService = agentService;
        _approvalBroker = approvalBroker;
        _runRegistry = runRegistry;
    }

    /// <summary>运行指定会话中的用户提示，并向调用连接逐条推送统一 Agent 事件。</summary>
    public async Task StartRun(string sessionId, string prompt, string? providerName = null, string? modelName = null)
    {
        if (!Guid.TryParse(sessionId, out var parsedSessionId))
            throw new HubException("会话 ID 格式无效");

        var connectionId = Context.ConnectionId;
        using var runControl = _runRegistry.Begin(connectionId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted, runControl.CancellationToken);
        string? runId = null;
        var runStartedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var partialContent = new StringBuilder();
        var partialReasoning = new StringBuilder();
        var completed = false;
        try
        {
            await foreach (var agentEvent in _agentService.RunAsync(parsedSessionId, prompt, providerName, modelName, runControl, connectionId, linkedCancellation.Token))
            {
                runId ??= agentEvent.RunId;
                if (agentEvent.Type == "message.delta") partialContent.Append(agentEvent.Text);
                if (agentEvent.Type == "message.replace") { partialContent.Clear(); partialContent.Append(agentEvent.Text); }
                if (agentEvent.Type == "reasoning.delta") partialReasoning.Append(agentEvent.Text);
                if (agentEvent.Type == "reasoning.replace") { partialReasoning.Clear(); partialReasoning.Append(agentEvent.Text); }
                if (agentEvent.Type == "run.completed") completed = true;
                await Clients.Caller.SendAsync("agentEvent", agentEvent, linkedCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (runControl.CancellationToken.IsCancellationRequested)
        {
            if (runId is not null)
            {
                if (!completed) await _agentService.SaveInterruptedRunAsync(parsedSessionId, partialContent.ToString(), partialReasoning.ToString(), runStartedAt, stopwatch.ElapsedMilliseconds, Context.ConnectionAborted);
                await Clients.Caller.SendAsync("agentEvent", new AgentBackend.Models.AgentEvent("run.stopped", runId, ElapsedMilliseconds: stopwatch.ElapsedMilliseconds), Context.ConnectionAborted);
            }
        }
        catch (KeyNotFoundException exception)
        {
            throw new HubException(exception.Message);
        }
        finally { _runRegistry.End(connectionId, runControl); }
    }

    /// <summary>接收调用当前工具的用户批准或拒绝决定。</summary>
    public Task<bool> RespondToApproval(string requestId, bool approved) =>
        Task.FromResult(_approvalBroker.Resolve(Context.ConnectionId, requestId, approved));

    /// <summary>暂停当前运行；若其已暂停，再次调用则停止运行。</summary>
    public Task<string> PauseOrStop() => Task.FromResult(_runRegistry.PauseOrStop(Context.ConnectionId));
}
