using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebPostman.Api.Controllers;

[ApiController]
[Route("api/shares")]
public sealed class SharesController(ShareStore shares) : ControllerBase
{
    [HttpGet]
    public SharePage List(string? query, string? kind, bool? mine, int? offset, int? limit) => shares.List(
        query?.Length > 200 ? query[..200] : query, kind, mine == true ? AccountStore.UserId(User) : null,
        Math.Max(offset ?? 0, 0), Math.Clamp(limit ?? 30, 1, 50));

    [HttpGet("{id}")]
    public SharedItem Get(string id) => shares.Get(id);

    [HttpPost("preview"), Authorize]
    public async Task<IActionResult> Preview(ShareInput input)
    { var preview = await shares.PreviewAsync(AccountStore.UserId(User), input); return Ok(new { collection = preview.Collection, revision = preview.Revision }); }

    [HttpPost, Authorize]
    public Task<SharedItem> Publish(ShareInput input) => shares.PublishAsync(AccountStore.UserId(User), input);

    [HttpDelete("{id}"), Authorize]
    public IActionResult Delete(string id) { shares.Delete(id, AccountStore.UserId(User)); return NoContent(); }
}
