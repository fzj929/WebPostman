using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace WebPostman.Api.Controllers;

[ApiController]
[Route("api/execute")]
public sealed class ExecuteController(RequestExecutionService execution) : ControllerBase
{
    [HttpPost, EnableRateLimiting("execute")]
    public async Task<IActionResult> Execute(ExecuteInput input, CancellationToken cancellationToken)
    {
        try { return Ok(await execution.ExecuteAsync(input, User, cancellationToken)); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return BadRequest(new { error = "请求超时。" }); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or ArgumentException or FormatException)
        { return BadRequest(new { error = ex.Message }); }
    }
}
