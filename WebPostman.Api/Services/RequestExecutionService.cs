using System.Security.Claims;

namespace WebPostman.Api;

public sealed class RequestExecutionService(RequestExecutor executor, AccountStore accounts)
{
    public async Task<ExecuteResult> ExecuteAsync(ExecuteInput input, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        InputValidation.Execute(input);
        var result = await executor.ExecuteAsync(input, cancellationToken);
        var snapshot = input.Request with { Auth = input.EffectiveAuth, Files = null };
        var history = new HistoryEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            input.Request.Method, RequestExecutor.SafeDisplayUrl(input.Request.Url), result.Status, result.DurationMs,
            snapshot, input.EffectiveAuth, input.Variables, input.CollectionId);
        if (user.Identity?.IsAuthenticated == true) await accounts.Workspace(AccountStore.UserId(user)).AddHistoryAsync(history);
        return result with { History = history };
    }
}
