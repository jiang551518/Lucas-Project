using System.Collections.Concurrent;

namespace AgentBackend.Services;

/// <summary>协调单个 SignalR Agent 运行的暂停与停止请求。</summary>
public sealed class AgentRunRegistry
{
    private readonly ConcurrentDictionary<string, AgentRunControl> _active = new();

    /// <summary>为当前连接注册新的 Agent 运行控制器。</summary>
    public AgentRunControl Begin(string connectionId)
    {
        var control = new AgentRunControl();
        if (!_active.TryAdd(connectionId, control)) throw new InvalidOperationException("当前连接已有运行中的 Agent 任务。");
        return control;
    }

    /// <summary>清理当前连接对应的已完成运行控制器。</summary>
    public void End(string connectionId, AgentRunControl control) =>
        _active.TryRemove(new KeyValuePair<string, AgentRunControl>(connectionId, control));

    /// <summary>首次调用暂停运行，再次调用取消当前运行；返回执行后的状态。</summary>
    public string PauseOrStop(string connectionId)
    {
        if (!_active.TryGetValue(connectionId, out var control)) return "idle";
        return control.TogglePauseOrStop();
    }
}

/// <summary>提供协作式暂停闸门和整轮运行取消令牌。</summary>
public sealed class AgentRunControl : IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource _resume = NewSignal();
    private bool _paused;
    private bool _stopRequested;
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>获取用于取消模型请求和本地命令的运行令牌。</summary>
    public CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>返回是否已进入暂停状态。</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }

    /// <summary>暂停当前运行；若已经暂停，则请求停止。</summary>
    public string TogglePauseOrStop()
    {
        lock (_gate)
        {
            if (_stopRequested) return "stopping";
            if (!_paused)
            {
                _paused = true;
                _resume = NewSignal();
                return "paused";
            }
            _stopRequested = true;
            _cancellation.Cancel();
            _resume.TrySetResult();
            return "stopping";
        }
    }

    /// <summary>在模型流或工具往返的安全检查点等待恢复；停止请求会以取消结束等待。</summary>
    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate) wait = _paused ? _resume.Task : Task.CompletedTask;
        await wait.WaitAsync(cancellationToken);
    }

    /// <summary>解除暂停状态并让运行继续。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused || _stopRequested) return;
            _paused = false;
            _resume.TrySetResult();
        }
    }

    /// <summary>释放运行控制器持有的取消资源。</summary>
    public void Dispose() => _cancellation.Dispose();

    /// <summary>创建可异步等待的信号源。</summary>
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
