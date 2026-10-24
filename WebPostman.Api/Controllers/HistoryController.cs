using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebPostman.Api.Controllers;

[ApiController, Authorize]
[Route("api/history")]
public sealed class HistoryController(AccountStore accounts) : ControllerBase
{
    [HttpGet]
    public Task<HistoryPage> Get(int? offset, int? limit) => accounts.Workspace(AccountStore.UserId(User))
        .ReadHistoryAsync(Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, 100));
}
