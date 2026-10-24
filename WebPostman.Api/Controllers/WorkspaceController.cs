using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebPostman.Api.Controllers;

[ApiController, Authorize]
[Route("api/workspace")]
public sealed class WorkspaceController(AccountStore accounts) : ControllerBase
{
    private WorkspaceStore Store => accounts.Workspace(AccountStore.UserId(User));

    [HttpGet]
    public Task<Workspace> Get() => Store.ReadAsync();

    [HttpPut]
    public async Task<IActionResult> Put(Workspace workspace)
    { InputValidation.Workspace(workspace); return Ok(new { revision = await Store.WriteAsync(workspace) }); }

    [HttpPatch]
    public async Task<IActionResult> Patch(WorkspaceChanges changes)
    { InputValidation.Changes(changes); return Ok(new { revision = await Store.PatchAsync(changes) }); }
}
