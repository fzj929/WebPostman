using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebPostman.Api.Controllers;

[ApiController, Authorize]
[Route("api/mocks")]
public sealed class MocksController(MockServerService mocks) : ControllerBase
{
    private string Owner => AccountStore.UserId(User);
    [HttpGet] public Task<List<MockServer>> List() => mocks.ListAsync(Owner);
    [HttpPost] public Task<MockServer> Create(MockServerInput input) => mocks.CreateAsync(Owner, input);
    [HttpGet("{id}")] public Task<MockServer> Get(string id) => mocks.GetAsync(Owner, id);
    [HttpPut("{id}")] public Task<MockServer> Update(string id, MockServerInput input) => mocks.UpdateAsync(Owner, id, input);
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(string id) { await mocks.DeleteAsync(Owner, id); return NoContent(); }
    [HttpPost("{id}/start")] public Task<MockServer> Start(string id) => mocks.StartAsync(Owner, id);
    [HttpPost("{id}/stop")] public Task<MockServer> Stop(string id) => mocks.StopAsync(Owner, id);
    [HttpGet("{id}/logs")] public Task<List<MockCall>> Logs(string id) => mocks.LogsAsync(Owner, id);
    [HttpPost("{id}/rules")] public Task<MockServer> CreateRule(string id, MockRuleInput input) => mocks.SaveRuleAsync(Owner, id, null, input);
    [HttpPut("{id}/rules/{ruleId}")] public Task<MockServer> UpdateRule(string id, string ruleId, MockRuleInput input) => mocks.SaveRuleAsync(Owner, id, ruleId, input);
    [HttpDelete("{id}/rules/{ruleId}")] public Task<MockServer> DeleteRule(string id, string ruleId, long revision) => mocks.DeleteRuleAsync(Owner, id, ruleId, revision);
}
