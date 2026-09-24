using AgentBackend.Services;
using Xunit;

namespace AgentBackend.Tests;

/// <summary>验证 Harness 对终端结果的成功判定规则。</summary>
public sealed class AgentServiceTests
{
    /// <summary>仅明确的零退出码表示修改后的验证命令通过。</summary>
    [Theory]
    [InlineData("退出码：0\n标准输出：通过", true)]
    [InlineData("退出码：1\n标准错误：测试失败", false)]
    [InlineData("命令运行超过 2 分钟，已终止。", false)]
    [InlineData("无法启动 PowerShell：拒绝访问", false)]
    public void HasSuccessfulCommandExitCode_RequiresExplicitZeroExitCode(string result, bool expected)
    {
        Assert.Equal(expected, AgentService.HasSuccessfulCommandExitCode(result));
    }
}
