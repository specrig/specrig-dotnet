using Gateway.Customer;
using Gateway.Notification;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddGrpcClient<Accounts.AccountsClient>(o => o.Address = new Uri(builder.Configuration["Ausweis:Address"]!));
builder.Services.AddGrpcClient<Sessions.SessionsClient>(o => o.Address = new Uri(builder.Configuration["Ausweis:Address"]!));
builder.Services.AddGrpcClient<Mail.MailClient>(o => o.Address = new Uri(builder.Configuration["Herald:Address"]!));

var app = builder.Build();
app.MapControllers();
app.Run();

public partial class Program;
