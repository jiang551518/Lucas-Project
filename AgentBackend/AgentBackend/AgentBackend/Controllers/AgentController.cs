using AgentBackend.Models;
using AgentBackend.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentBackend.Controllers;

/// <summary>提供项目与 Agent 会话的 HTTP 管理 API。</summary>
[ApiController]
[Route("api")]
public sealed class AgentController : ControllerBase
{
    private readonly IAgentService _agentService;

    /// <summary>通过应用服务处理控制器请求。</summary>
    public AgentController(IAgentService agentService) => _agentService = agentService;

    /// <summary>返回用户添加的服务商和模型清单，但不会泄露 API Key。</summary>
    [HttpGet("providers")]
    public async Task<ActionResult<IReadOnlyList<ProviderSummary>>> GetProviders(CancellationToken cancellationToken)
        => Ok(await _agentService.GetProvidersAsync(cancellationToken));

    /// <summary>安全保存或重命名 OpenAI-compatible 服务商及其模型清单。</summary>
    [HttpPut("providers")]
    public async Task<ActionResult<ProviderSummary>> SaveProvider([FromBody] SaveProviderRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _agentService.SaveProviderAsync(request, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    /// <summary>删除用户添加的 Provider 配置和其下全部模型。</summary>
    [HttpDelete("providers/{name}")]
    public async Task<IActionResult> DeleteProvider(string name, CancellationToken cancellationToken)
        => await _agentService.DeleteProviderProfileAsync(name, cancellationToken) ? NoContent() : NotFound();

    /// <summary>返回工作区项目列表。</summary>
    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<AgentProject>>> GetProjects(CancellationToken cancellationToken)
        => Ok(await _agentService.GetProjectsAsync(cancellationToken));

    /// <summary>创建项目，并拒绝空白名称。</summary>
    [HttpPost("projects")]
    public async Task<ActionResult<AgentProject>> CreateProject([FromBody] CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { error = "项目名称不能为空" });
        AgentProject project;
        try { project = await _agentService.CreateProjectAsync(request.Name, request.WorkspacePath, request.WorkspacePathType, cancellationToken); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        return Created("/api/projects", project);
    }

    /// <summary>更新指定项目的可见名称。</summary>
    [HttpPut("projects/{id:guid}/name")]
    public async Task<IActionResult> RenameProject(Guid id, [FromBody] RenameWorkspaceItemRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _agentService.RenameProjectAsync(id, request.Name, cancellationToken);
            return updated ? NoContent() : NotFound(new { error = "项目不存在" });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    /// <summary>返回按更新时间倒序的会话列表。</summary>
    [HttpGet("agent/sessions")]
    public async Task<ActionResult<IReadOnlyList<AgentSession>>> GetSessions(CancellationToken cancellationToken)
        => Ok(await _agentService.GetSessionsAsync(cancellationToken));

    /// <summary>创建新的空白会话。</summary>
    [HttpPost("agent/sessions")]
    public async Task<ActionResult<AgentSession>> CreateSession(CancellationToken cancellationToken)
    {
        var session = await _agentService.CreateSessionAsync(cancellationToken);
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, session);
    }

    /// <summary>按 ID 获取会话及完整消息记录。</summary>
    [HttpGet("agent/sessions/{id:guid}")]
    public async Task<ActionResult<AgentSession>> GetSession(Guid id, CancellationToken cancellationToken)
    {
        var session = await _agentService.GetSessionAsync(id, cancellationToken);
        return session is null ? NotFound() : Ok(session);
    }

    /// <summary>将会话关联到指定项目，或传 null 解除关联。</summary>
    [HttpPut("agent/sessions/{id:guid}/project")]
    public async Task<IActionResult> AssignSession(Guid id, [FromBody] AssignSessionRequest request, CancellationToken cancellationToken)
    {
        var updated = await _agentService.AssignSessionAsync(id, request.ProjectId, cancellationToken);
        return updated ? NoContent() : NotFound(new { error = "会话或项目不存在" });
    }

    /// <summary>更新指定会话的可见标题。</summary>
    [HttpPut("agent/sessions/{id:guid}/title")]
    public async Task<IActionResult> RenameSession(Guid id, [FromBody] RenameWorkspaceItemRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _agentService.RenameSessionAsync(id, request.Name, cancellationToken);
            return updated ? NoContent() : NotFound(new { error = "会话不存在" });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    /// <summary>设置会话工作区和 AI 权限模式。</summary>
    [HttpPut("agent/sessions/{id:guid}/execution-context")]
    public async Task<IActionResult> UpdateExecutionContext(Guid id, [FromBody] UpdateExecutionContextRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _agentService.UpdateSessionExecutionContextAsync(id, request.ProjectId, request.WorkspacePath, request.WorkspacePathType, request.PermissionMode, cancellationToken);
            return updated ? NoContent() : NotFound(new { error = "会话或项目不存在" });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    /// <summary>删除会话及其全部消息。</summary>
    [HttpDelete("agent/sessions/{id:guid}")]
    public async Task<IActionResult> DeleteSession(Guid id, CancellationToken cancellationToken)
        => await _agentService.DeleteSessionAsync(id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>删除项目并把其下对话移回最近列表。</summary>
    [HttpDelete("projects/{id:guid}")]
    public async Task<IActionResult> DeleteProject(Guid id, CancellationToken cancellationToken)
        => await _agentService.DeleteProjectAsync(id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>获取本地技能列表。</summary>
    [HttpGet("skills")]
    public async Task<ActionResult<IReadOnlyList<AgentSkill>>> GetSkills(CancellationToken cancellationToken) => Ok(await _agentService.GetSkillsAsync(cancellationToken));

    /// <summary>新增或更新本地技能。</summary>
    [HttpPut("skills")]
    public async Task<ActionResult<AgentSkill>> SaveSkill([FromBody] AgentSkill skill, CancellationToken cancellationToken)
    {
        try { return Ok(await _agentService.SaveSkillAsync(skill, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    /// <summary>删除本地技能。</summary>
    [HttpDelete("skills/{id:guid}")]
    public async Task<IActionResult> DeleteSkill(Guid id, CancellationToken cancellationToken) => await _agentService.DeleteSkillAsync(id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>获取本地记忆条目。</summary>
    [HttpGet("memories")]
    public async Task<ActionResult<IReadOnlyList<MemoryEntry>>> GetMemories(CancellationToken cancellationToken) => Ok(await _agentService.GetMemoriesAsync(cancellationToken));

    /// <summary>新增或更新本地记忆条目。</summary>
    [HttpPut("memories")]
    public async Task<ActionResult<MemoryEntry>> SaveMemory([FromBody] MemoryEntry memory, CancellationToken cancellationToken)
    {
        try { return Ok(await _agentService.SaveMemoryAsync(memory, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    /// <summary>删除本地记忆条目。</summary>
    [HttpDelete("memories/{id:guid}")]
    public async Task<IActionResult> DeleteMemory(Guid id, CancellationToken cancellationToken) => await _agentService.DeleteMemoryAsync(id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>获取本地显示偏好与 Harness/Loop 参数。</summary>
    [HttpGet("settings")]
    public async Task<ActionResult<AgentSettings>> GetSettings(CancellationToken cancellationToken) => Ok(await _agentService.GetSettingsAsync(cancellationToken));

    /// <summary>保存本地显示偏好与 Harness/Loop 参数。</summary>
    [HttpPut("settings")]
    public async Task<ActionResult<AgentSettings>> SaveSettings([FromBody] AgentSettings settings, CancellationToken cancellationToken) => Ok(await _agentService.SaveSettingsAsync(settings, cancellationToken));
}

/// <summary>创建项目所需的请求体。</summary>
public sealed record CreateProjectRequest(string Name, string WorkspacePath, string WorkspacePathType);

/// <summary>更新会话所属项目所需的请求体，null 表示解除关联。</summary>
public sealed record AssignSessionRequest(Guid? ProjectId);

/// <summary>重命名项目或会话所需的请求体。</summary>
public sealed record RenameWorkspaceItemRequest(string Name);

/// <summary>会话工作区及本地工具权限设置请求。</summary>
public sealed record UpdateExecutionContextRequest(Guid? ProjectId, string WorkspacePath, string WorkspacePathType, string PermissionMode);
