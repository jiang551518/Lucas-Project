using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentBackend.Services;

/// <summary>在明确选择的本地工作区内执行模型请求的文件与命令工具。</summary>
public sealed class WorkspaceToolExecutor
{
    private const int MaxFileCharacters = 120_000;
    private const int MaxCommandOutputCharacters = 16_000;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs", "target", ".idea"
    };

    /// <summary>根据 OpenAI 工具参数分派文件读取、文件写入或跨平台终端命令。</summary>
    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, string workspaceRoot, CancellationToken cancellationToken, string? selectedFilePath = null)
    {
        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = Path.GetFullPath(workspaceRoot);
        var selectedFile = selectedFilePath is null ? null : Path.GetFullPath(selectedFilePath);
        return toolName switch
        {
            "list_files" => ListFiles(root, GetOptionalString(arguments.RootElement, "path"), selectedFile),
            "read_file" => await ReadFileAsync(root, GetRequiredString(arguments.RootElement, "path"), cancellationToken, selectedFile),
            "write_file" => await WriteFileAsync(root, GetRequiredString(arguments.RootElement, "path"), GetRequiredString(arguments.RootElement, "content"), cancellationToken, selectedFile),
            "run_command" when selectedFile is not null => throw new UnauthorizedAccessException("当前工作区只授权了单个文件，不能运行终端命令。请选择工作目录后再执行命令。"),
            "run_command" => await RunCommandAsync(root, GetRequiredString(arguments.RootElement, "command"), cancellationToken),
            _ => throw new InvalidOperationException($"未知的 Agent 工具：{toolName}")
        };
    }

    /// <summary>列出工作区内有限数量的文件相对路径，并跳过常见依赖与构建目录。</summary>
    private static string ListFiles(string root, string? relativePath, string? selectedFile)
    {
        if (selectedFile is not null)
        {
            EnsureSelectedFile(root, selectedFile, relativePath);
            EnsureNoReparsePoint(root, selectedFile);
            if (!File.Exists(selectedFile)) throw new FileNotFoundException("所选工作文件不存在。", selectedFile);
            return Path.GetRelativePath(root, selectedFile);
        }

        var start = string.IsNullOrWhiteSpace(relativePath) ? root : ResolvePath(root, relativePath);
        if (!Directory.Exists(start)) throw new DirectoryNotFoundException("请求的目录不存在。");
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0 && results.Count < 500)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(entry))) pending.Push(entry);
                }
                else results.Add(Path.GetRelativePath(root, entry));
                if (results.Count >= 500) break;
            }
        }
        return results.Count == 0 ? "工作区中没有找到文件。" : string.Join("\n", results);
    }

    /// <summary>读取工作区内文本文件，拒绝越界路径并限制返回字符数。</summary>
    private static async Task<string> ReadFileAsync(string root, string relativePath, CancellationToken cancellationToken, string? selectedFile)
    {
        EnsureSelectedFile(root, selectedFile, relativePath);
        var path = selectedFile ?? ResolvePath(root, relativePath);
        EnsureNoReparsePoint(root, path);
        if (!File.Exists(path)) throw new FileNotFoundException("请求的工作区文件不存在。", relativePath);
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return content.Length <= MaxFileCharacters ? content : content[..MaxFileCharacters] + "\n…（内容已截断）";
    }

    /// <summary>在工作区内创建或覆盖指定文本文件，并返回其相对路径和写入大小。</summary>
    private static async Task<string> WriteFileAsync(string root, string relativePath, string content, CancellationToken cancellationToken, string? selectedFile)
    {
        EnsureSelectedFile(root, selectedFile, relativePath);
        var path = selectedFile ?? ResolvePath(root, relativePath);
        EnsureNoReparsePoint(root, path);
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("无效的文件路径。");
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoint(root, path);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
        return $"已写入 {Path.GetRelativePath(root, path)}（{content.Length} 个字符）。";
    }

    /// <summary>在选定目录运行当前操作系统的 PowerShell 或 Bash，并收集有限输出。</summary>
    private static async Task<string> RunCommandAsync(string root, string command, CancellationToken cancellationToken)
    {
        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "powershell.exe" : "/bin/bash",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (isWindows)
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("--noprofile");
            startInfo.ArgumentList.Add("--norc");
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动本机终端进程。");
            var perStreamLimit = (MaxCommandOutputCharacters - 80) / 2;
            var stdoutTask = ReadBoundedOutputAsync(process.StandardOutput, perStreamLimit);
            var stderrTask = ReadBoundedOutputAsync(process.StandardError, perStreamLimit);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (!cancellationToken.IsCancellationRequested) return "命令运行超过 2 分钟，已终止。";
                throw;
            }
            var output = await stdoutTask;
            var error = await stderrTask;
            var truncationNotice = output.Truncated || error.Truncated ? "\n…（命令输出已达到长度上限，剩余输出已丢弃）" : string.Empty;
            return $"退出码：{process.ExitCode}\n标准输出：\n{output.Text}\n标准错误：\n{error.Text}{truncationNotice}";
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return isWindows
                ? $"无法启动 PowerShell（powershell.exe）：{exception.Message}"
                : $"无法启动 Bash（/bin/bash）：{exception.Message}";
        }
    }

    /// <summary>持续排空终端流但只保留限定字符，避免大输出耗尽桌面应用内存。</summary>
    internal static async Task<BoundedOutput> ReadBoundedOutputAsync(TextReader reader, int maximumCharacters)
    {
        var buffer = new char[2048];
        var retained = new StringBuilder(Math.Min(maximumCharacters, buffer.Length));
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (count == 0) break;
            var remaining = maximumCharacters - retained.Length;
            if (remaining > 0) retained.Append(buffer, 0, Math.Min(count, remaining));
            if (count > remaining) truncated = true;
        }
        return new BoundedOutput(retained.ToString(), truncated);
    }

    /// <summary>验证单文件工作区中的路径是否恰好指向用户选中的文件。</summary>
    private static void EnsureSelectedFile(string root, string? selectedFile, string? requestedPath)
    {
        if (selectedFile is null) return;
        var selectedRelativePath = Path.GetRelativePath(root, selectedFile);
        string requestedFullPath;
        try { requestedFullPath = requestedPath is null ? selectedFile : ResolvePath(root, requestedPath); }
        catch (ArgumentException exception)
        {
            throw new UnauthorizedAccessException("当前工作区只授权访问所选文件。", exception);
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(requestedFullPath), Path.GetFullPath(selectedFile), comparison))
            throw new UnauthorizedAccessException($"当前只授权访问所选文件：{selectedRelativePath}");
    }

    /// <summary>解析相对路径并拒绝绝对路径或逃逸工作区根目录的路径。</summary>
    private static string ResolvePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath)) throw new ArgumentException("工具路径必须是工作区内的相对路径。");
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            throw new UnauthorizedAccessException("工具路径不能超出所选工作区。");
        return fullPath;
    }

    /// <summary>拒绝沿途经过符号链接或重解析点，防止文件工具借链接越出工作区。</summary>
    private static void EnsureNoReparsePoint(string root, string target)
    {
        var current = root;
        var relative = Path.GetRelativePath(root, target);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("工作区根目录不能是符号链接或重解析点。");
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("工具不能访问符号链接或重解析点目标。");
        }
    }

    /// <summary>读取 JSON 对象中的非空必需字符串参数。</summary>
    private static string GetRequiredString(JsonElement element, string name) =>
        GetOptionalString(element, name) is { Length: > 0 } value ? value : throw new ArgumentException($"缺少必需参数 {name}。");

    /// <summary>读取 JSON 对象中的可选字符串参数。</summary>
    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>表示被保留的有限终端输出及是否丢弃了额外内容。</summary>
internal sealed record BoundedOutput(string Text, bool Truncated);
