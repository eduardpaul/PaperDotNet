using JasperFx;
using PaperDotNet.Host;
using PaperDotNet.Host.Cli;

// One binary: `paperdotnet` serves the API; `paperdotnet <command>` runs an admin command.
if (args is ["healthcheck", ..])
{
    return await HealthProbe.RunAsync(args);
}

var isCodegen = args is ["codegen", ..];
var isCli = AdminCli.IsCommand(args);
var builder = WebApplication.CreateBuilder(isCli || isCodegen ? [] : args);
if (isCodegen) { builder.Configuration["Messaging:RuntimeCompilation"] = "true"; }
builder.AddPaperDotNet(runBootstrap: !isCli && !isCodegen);

var app = builder.Build();
if (isCodegen)
{
    return await app.RunJasperFxCommands(args);
}
if (isCli)
{
    return await AdminCli.RunAsync(app.Services, args);
}

app.UsePaperDotNet();
await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
