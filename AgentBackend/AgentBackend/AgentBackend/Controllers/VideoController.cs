using AgentBackend.Models;
using AgentBackend.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentBackend.Controllers;

/// <summary>本机 ComfyUI H3 API 的轻量代理。</summary>
[ApiController]
[Route("api/video")]
public sealed class VideoController : ControllerBase
{
    private readonly VideoAgentService _videoAgentService;

    public VideoController(VideoAgentService videoAgentService) => _videoAgentService = videoAgentService;

    [HttpGet("config")]
    public async Task<ActionResult<VideoAgentConfigSummary>> GetConfig(CancellationToken cancellationToken)
        => Ok(await _videoAgentService.GetConfigAsync(cancellationToken));

    [HttpPut("config")]
    public async Task<ActionResult<VideoAgentConfigSummary>> SaveConfig([FromBody] SaveVideoAgentConfigRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _videoAgentService.SaveConfigAsync(request, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("health")]
    public async Task<IActionResult> CheckHealth(CancellationToken cancellationToken)
    {
        try { return Ok(await _videoAgentService.CheckHealthAsync(cancellationToken)); }
        catch (HttpRequestException exception) { return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message }); }
    }

    [HttpPost("jobs")]
    public async Task<IActionResult> SubmitJob([FromBody] SubmitVideoJobRequest request, CancellationToken cancellationToken)
    {
        try { return StatusCode(StatusCodes.Status201Created, await _videoAgentService.SubmitJobAsync(request.Prompt, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return BadRequest(new { error = exception.Message }); }
        catch (HttpRequestException exception) { return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message }); }
    }

    [HttpGet("jobs/{jobId}")]
    public async Task<IActionResult> GetJob(string jobId, CancellationToken cancellationToken)
    {
        try { return Ok(await _videoAgentService.GetJobAsync(jobId, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return BadRequest(new { error = exception.Message }); }
        catch (HttpRequestException exception) { return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message }); }
    }

    [HttpPost("jobs/{jobId}/cancel")]
    public async Task<IActionResult> CancelJob(string jobId, CancellationToken cancellationToken)
    {
        try { return Ok(await _videoAgentService.CancelJobAsync(jobId, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return BadRequest(new { error = exception.Message }); }
        catch (HttpRequestException exception) { return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message }); }
    }

    [HttpGet("jobs/{jobId}/events")]
    public async Task<IActionResult> StreamJobEvents(string jobId, CancellationToken cancellationToken)
    {
        HttpResponseMessage upstream;
        try { upstream = await _videoAgentService.OpenJobEventsAsync(jobId, cancellationToken); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return StatusCode(StatusCodes.Status501NotImplemented, new { error = exception.Message }); }
        catch (HttpRequestException exception) { return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message }); }

        using (upstream)
        using (upstream.RequestMessage)
        {
            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            Response.Headers["Cache-Control"] = "no-cache, no-transform";
            Response.Headers["X-Accel-Buffering"] = "no";
            if (!upstream.IsSuccessStatusCode || !Response.ContentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await upstream.Content.CopyToAsync(Response.Body, cancellationToken);
                return new EmptyResult();
            }

            await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        return new EmptyResult();
    }
}

public sealed record SubmitVideoJobRequest(string Prompt);
