using Gateway.Customer;
using Microsoft.AspNetCore.Mvc;

namespace Gateway.Http;

public sealed class AccountsController(Accounts.AccountsClient accounts, Sessions.SessionsClient sessions) : AccountsControllerBase
{
    public override async Task<ActionResult<AccountView>> Account(long accountId)
    {
        var account = await accounts.GetAsync(new AccountRef { Id = accountId });
        return new AccountView { Id = account.Id, Email = account.Email };
    }

    public override async Task<IActionResult> Login(Credentials body)
    {
        var session = await sessions.SummonAsync(new Token { Value = body.Token });
        return Ok(session.AccountId);
    }
}
