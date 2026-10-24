using Gateway.Notification;
using Microsoft.AspNetCore.Mvc;

namespace Gateway.Http;

[Route("api/[controller]")]
public abstract class Listing : ControllerBase
{
    [HttpGet]
    public IActionResult All() => Ok();
}

public sealed class LettersController(Mail.MailClient mail) : Listing
{
    [HttpPost("{to}")]
    public async Task<IActionResult> Send(string to, [FromBody] string body) =>
        Ok((await mail.SendAsync(new Letter { To = to, Body = body })).Id);

    [HttpGet, Route("~/letters/count")]
    public IActionResult Count() => Ok(0);

    [NonAction]
    [HttpGet("hidden")]
    public IActionResult Hidden() => Ok();
}

public sealed class HealthController : ControllerBase
{
    [HttpGet("healthz")]
    public IActionResult Healthz() => Ok();
}
