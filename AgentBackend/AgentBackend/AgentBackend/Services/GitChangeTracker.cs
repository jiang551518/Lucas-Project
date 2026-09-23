using System.Diagnostics;
using System.Text;

namespace AgentBackend.Services;

/// <summary>使用临时 Git 索引快照比较单次 Agent 运行前后的工作树变更。</summary>
public sealed class GitChangeTracker
{
    private const int MaxDiffCharacters = 120_000;

    /// <summary>对指定工作目录创建临时树对象快照；非 Git 仓库或 Git 不可用时返回 null。</summary>
    public async Task<GitWorkspaceSnapshot?> CaptureAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var rootResult = await RunGitAsync(workspaceRoot, null, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (rootResult.ExitCode != 0) return null;
        var repositoryRoot = rootResult.StandardOutput.Trim();
        if (repositoryRoot.Length == 0) return null;
        var indexPath = Path.Combine(Path.GetTempPath(), $"lucas-agent-index-{Guid.NewGuid():N}");
        try
        {
            var head = await RunGitAsync(repositoryRoot, indexPath, ["rev-parse", "--verify", "HEAD"], cancellationToken);
            var readTree = head.ExitCode == 0
                ? await RunGitAsync(repositoryRoot, indexPath, ["read-tree", "HEAD"], cancellationToken)
                : await RunGitAsync(repositoryRoot, indexPath, ["read-tree", "--empty"], cancellationToken);
            if (readTree.ExitCode != 0) return null;
            var add = await RunGitAsync(repositoryRoot, indexPath, ["add", "-A", "--", "."], cancellationToken);
            if (add.ExitCode != 0) return null;
            var tree = await RunGitAsync(repositoryRoot, indexPath, ["write-tree"], cancellationToken);
            return tree.ExitCode == 0 && tree.StandardOutput.Trim() is { Length: > 0 } hash
                ? new GitWorkspaceSnapshot(repositoryRoot, hash)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            TryDelete(indexPath);
            TryDelete(indexPath + ".lock");
        }
    }

    /// <summary>比較兩個位於同一 Git 倉庫的工作樹快照，回傳可讀的統一差異。</summary>
    public async Task<string?> GetDiffAsync(GitWorkspaceSnapshot? before, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (before is null) return null;
        var after = await CaptureAsync(workspaceRoot, cancellationToken);
        if (after is null || !PathEquals(before.RepositoryRoot, after.RepositoryRoot)) return null;
        var result = await RunGitAsync(before.RepositoryRoot, null,
            ["diff", "--no-ext-diff", "--no-color", "--no-renames", "--unified=3", before.TreeHash, after.TreeHash, "--"], cancellationToken);
        if (result.ExitCode != 0) return null;
        var diff = result.StandardOutput;
        return diff.Length <= MaxDiffCharacters ? diff : diff[..MaxDiffCharacters] + "\n…（差异内容已截断）";
    }

    /// <summary>以参数列表而非 shell 字符串启动 Git，避免对路径和参数进行命令拼接。</summary>
    private static async Task<GitCommandResult> RunGitAsync(string workingDirectory, string? indexPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (indexPath is not null) startInfo.Environment["GIT_INDEX_FILE"] = indexPath;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return new GitCommandResult(-1, "", "无法启动 Git。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return new GitCommandResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>以 Windows 不区分大小写、Unix 区分大小写的语义比较仓库根路径。</summary>
    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>尽力删除临时索引文件，不影响 Git 仓库原始索引。</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>标识某一时刻 Git 仓库工作树内容的根路径与树对象哈希。</summary>
public sealed record GitWorkspaceSnapshot(string RepositoryRoot, string TreeHash);

/// <summary>封装 Git 进程退出状态及标准输出、标准错误。</summary>
internal sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
