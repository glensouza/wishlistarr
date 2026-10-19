using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using Wishlistarr.Api;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;
using Wishlistarr.Core;
using Wishlistarr.Data;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog(
    (context, services, logging) =>
    {
        DataDirectory directory = services.GetRequiredService<DataDirectory>();
        logging
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(directory.LogsPath, "wishlistarr-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true);
    },
    preserveStaticLogger: true);

builder.Services.AddSingleton(provider =>
{
    DataDirectory directory = DataDirectory.Resolve(
        provider.GetRequiredService<IConfiguration>(),
        provider.GetRequiredService<IHostEnvironment>().ContentRootPath);
    Directory.CreateDirectory(directory.Root);
    return directory;
});

builder.Services.AddDbContext<WishlistarrDbContext>((provider, options) =>
    options.UseWishlistarrSqlite(provider.GetRequiredService<DataDirectory>().DatabasePath));

builder.Services.AddDataProtection().SetApplicationName("Wishlistarr");
builder.Services.AddOptions<KeyManagementOptions>().Configure<DataDirectory, ILoggerFactory>(
    (options, directory, loggerFactory) =>
        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(directory.KeysPath), loggerFactory));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SystemStatus>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddApiKeySecurity());

WebApplication app = builder.Build();

using (IServiceScope scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Database.MigrateAsync();
    await app.Services.GetRequiredService<ApiKeyService>()
        .EnsureAsync(scope.ServiceProvider.GetRequiredService<SettingsStore>());
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapApiV1();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
