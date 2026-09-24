using System.Text;
using AgentBackend.Services;
using Xunit;

namespace AgentBackend.Tests;

/// <summary>验证工作区文件隔离及终端输出上限。</summary>
public sealed class WorkspaceToolExecutorTests
{
    /// <summary>单文件工作区只向模型列出被用户选中的文件。</summary>
    [Fact]
    public async Task ListFiles_WhenSingleFileSelected_ReturnsOnlySelectedFile()
    {
        using var workspace = TemporaryWorkspace.Create();
        var selectedFile = workspace.WriteFile("selected.txt", "selected");
        workspace.WriteFile("sibling-secret.txt", "must not be listed");

        var result = await new WorkspaceToolExecutor().ExecuteAsync(
            "list_files", "{}", workspace.Path, CancellationToken.None, selectedFile);

        Assert.Equal("selected.txt", result);
    }

    /// <summary>单文件工作区拒绝读取同目录中的其他文件。</summary>
    [Fact]
    public async Task ReadFile_WhenSingleFileSelected_RejectsSiblingFile()
    {
        using var workspace = TemporaryWorkspace.Create();
        var selectedFile = workspace.WriteFile("selected.txt", "selected");
        workspace.WriteFile("sibling.txt", "secret");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WorkspaceToolExecutor().ExecuteAsync(
            "read_file", "{\"path\":\"sibling.txt\"}", workspace.Path, CancellationToken.None, selectedFile));
    }

    /// <summary>单文件工作区允许更新选中文件，但拒绝写入兄弟文件或通过父路径越界。</summary>
    [Fact]
    public async Task WriteFile_WhenSingleFileSelected_OnlyUpdatesSelectedFile()
    {
        using var workspace = TemporaryWorkspace.Create();
        var selectedFile = workspace.WriteFile("selected.txt", "before");
        workspace.WriteFile("sibling.txt", "keep");
        var executor = new WorkspaceToolExecutor();

        await executor.ExecuteAsync("write_file", "{\"path\":\"selected.txt\",\"content\":\"after\"}", workspace.Path, CancellationToken.None, selectedFile);

        Assert.Equal("after", await File.ReadAllTextAsync(selectedFile));
        Assert.Equal("keep", await File.ReadAllTextAsync(System.IO.Path.Combine(workspace.Path, "sibling.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(
            "write_file", "{\"path\":\"../outside.txt\",\"content\":\"unsafe\"}", workspace.Path, CancellationToken.None, selectedFile));
    }

    /// <summary>单文件工作区拒绝终端命令，避免命令绕过所选文件范围。</summary>
    [Fact]
    public async Task RunCommand_WhenSingleFileSelected_IsRejected()
    {
        using var workspace = TemporaryWorkspace.Create();
        var selectedFile = workspace.WriteFile("selected.txt", "selected");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WorkspaceToolExecutor().ExecuteAsync(
            "run_command", "{\"command\":\"echo unsafe\"}", workspace.Path, CancellationToken.None, selectedFile));
    }

    /// <summary>大量终端输出会被排空但只保留配置的最大字符数。</summary>
    [Fact]
    public async Task ReadBoundedOutputAsync_WhenInputExceedsLimit_TruncatesRetainedText()
    {
        var source = new string('x', 100_000);
        using var reader = new StringReader(source);

        var result = await WorkspaceToolExecutor.ReadBoundedOutputAsync(reader, 128);

        Assert.Equal(128, result.Text.Length);
        Assert.True(result.Truncated);
    }

    /// <summary>为每个工作区测试创建并在结束时删除独立临时目录。</summary>
    private sealed class TemporaryWorkspace : IDisposable
    {
        /// <summary>获取此测试使用的临时目录绝对路径。</summary>
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lucas-agent-test-{Guid.NewGuid():N}");

        /// <summary>创建临时目录并返回测试夹具。</summary>
        public static TemporaryWorkspace Create()
        {
            var workspace = new TemporaryWorkspace();
            Directory.CreateDirectory(workspace.Path);
            return workspace;
        }

        /// <summary>在临时工作区中写入 UTF-8 测试文件。</summary>
        public string WriteFile(string name, string content)
        {
            var filePath = System.IO.Path.Combine(Path, name);
            File.WriteAllText(filePath, content, Encoding.UTF8);
            return filePath;
        }

        /// <summary>删除本测试创建的临时工作区。</summary>
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
