namespace Gateway.Http
{
    [System.CodeDom.Compiler.GeneratedCode("NSwag", "14.6.3.0")]
    public abstract class AccountsControllerBase : Microsoft.AspNetCore.Mvc.ControllerBase
    {
        [Microsoft.AspNetCore.Mvc.HttpGet, Microsoft.AspNetCore.Mvc.Route("admin/accounts/{accountId:long}")]
        public abstract System.Threading.Tasks.Task<Microsoft.AspNetCore.Mvc.ActionResult<AccountView>> Account(long accountId);

        [Microsoft.AspNetCore.Mvc.HttpPost, Microsoft.AspNetCore.Mvc.Route("accounts/login")]
        public abstract System.Threading.Tasks.Task<Microsoft.AspNetCore.Mvc.IActionResult> Login([Microsoft.AspNetCore.Mvc.FromBody] Credentials body);
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "11.5.2.0")]
    public partial class AccountView
    {
        public long Id { get; set; }

        public string Email { get; set; } = "";
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "11.5.2.0")]
    public partial class Credentials
    {
        public string Token { get; set; } = "";
    }
}
