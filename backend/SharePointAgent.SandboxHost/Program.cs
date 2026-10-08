using SharePointAgent.SandboxHost;

var builder = WebApplication.CreateBuilder(args);
builder.AddSandboxHost();

var app = builder.Build();
app.MapSandboxHost();

app.Run();
