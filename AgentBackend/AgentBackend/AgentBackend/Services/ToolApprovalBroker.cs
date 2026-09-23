using System.Collections.Concurrent;

namespace AgentBackend.Services;

/// <summary>将待审批工具调用绑定到发起它的 SignalR 连接。</summary>
public sealed class ToolApprovalBroker
{
    private sealed record PendingApproval(string ConnectionId, TaskCompletionSource<bool> Completion);
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();

    /// <summary>创建一个一次性审批请求并返回其 ID 和异步结果。</summary>
    public (string RequestId, Task<bool> Result) Begin(string connectionId)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingApproval(connectionId, completion);
        return (id, completion.Task);
    }

    /// <summary>仅允许原始 SignalR 连接对匹配请求作出一次审批决定。</summary>
    public bool Resolve(string connectionId, string requestId, bool approved) =>
        _pending.TryGetValue(requestId, out var pending) &&
        pending.ConnectionId == connectionId &&
        pending.Completion.TrySetResult(approved);

    /// <summary>等待用户响应；取消、断连或两分钟超时均按拒绝处理并清理请求。</summary>
    public async Task<bool> WaitAsync(string requestId, Task<bool> result, CancellationToken cancellationToken)
    {
        try { return await result.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken); }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return false; }
        finally { _pending.TryRemove(requestId, out _); }
    }
}
