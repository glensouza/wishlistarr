using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using Wishlistarr.Api;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Search;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Core;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Arr;
using Wishlistarr.Integrations.Plex;
using Wishlistarr.Integrations.Seerr;
using Wishlistarr.Integrations.Tmdb;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog(
    (context, services, logging) =>
    {
        DataDirectory directory = services.GetRequiredService<DataDirectory>();
        logging
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.With<WebhookTokenRedactor>()
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
builder.Services.AddBackgroundJobs(builder.Configuration);
builder.Services.AddWebhooks(builder.Configuration);
builder.Services.AddWishlistarrSecurity(builder.Configuration);
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddTmdb<SettingsTmdbApiKeyProvider>();
builder.Services.AddPlex<StoredAppSettings>();
builder.Services.AddArr<StoredAppSettings>();
builder.Services.AddSeerr<StoredAppSettings>();
builder.Services.AddSingleton<ItemLocks>();
builder.Services.AddScoped<LabelWriter>();
builder.Services.AddScoped<MediaRequests>();
builder.Services.AddTmdbDataRetention();
builder.Services.AddScoped<LibraryLookup>();
builder.Services.AddLibraryIndex();
builder.Services.AddScoped<KidRequestFlow>();
builder.Services.AddScoped<KidSearch>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddSecuritySchemes());

WebApplication app = builder.Build();

using (IServiceScope scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Database.MigrateAsync();
    await app.Services.GetRequiredService<ApiKeyService>()
        .EnsureAsync(scope.ServiceProvider.GetRequiredService<SettingsStore>());
    await scope.ServiceProvider.GetRequiredService<ParentAccounts>().LoadSetupStateAsync();
    await scope.ServiceProvider.GetRequiredService<WebhookTokens>().EnsureAllAsync();
}

app.UseKnownProxies();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWishlistarrSecurity();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
    .WithTags("System")
    .WithName("GetHealth")
    .WithSummary("Reports that the service is running.");
app.MapAuth();
app.MapKid();
app.MapWebhookReceiver();
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
