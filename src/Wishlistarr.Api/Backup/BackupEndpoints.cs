using Microsoft.AspNetCore.Http.Features;
using Wishlistarr.Api.Jobs;

namespace Wishlistarr.Api.Backup;

public static class BackupEndpoints
{
    public static IServiceCollection AddBackups(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BackupOptions>()
            .Bind(configuration.GetSection(BackupOptions.SectionName))
            .Validate(options => options.IsValid(), "The Backups configuration section is invalid.")
            .ValidateOnStart();
        services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = BackupOptions.HardMaxUploadBytes);
        services.AddSingleton<BackupService>();
        services.AddBackgroundJob<DatabaseBackupJob>(
            DatabaseBackupJob.JobName,
            "Backs up the database and data protection key ring, then deletes backups beyond the retention count.",
            TimeSpan.FromDays(1));
        return services;
    }

    public static void MapBackups(this RouteGroupBuilder v1)
    {
        RouteGroupBuilder backups = v1.MapGroup("/backups").WithTags("Backups");

        backups.MapGet("/", (BackupService service) => service.List())
            .WithName("ListBackups")
            .WithSummary("Lists backup files. The files contain the database and the data protection key ring.")
            .Produces<BackupList>();

        backups.MapGet("/{name}/download", (string name, HttpContext http, BackupService service) =>
        {
            if (!BackupNames.IsBackupFileName(name))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The backup name is not valid.");
            }

            string? path = service.ResolveDownload(name);
            if (path is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The backup does not exist.");
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(path, "application/zip", name);
        })
            .WithName("DownloadBackup")
            .WithSummary("Downloads one backup zip. The zip contains secrets and is available to a parent only.")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // An IFormFile parameter adds an Accepts constraint. A request that does not match it
        // is not the endpoint, so authorization never runs and a kid can get a validation error.
        backups.MapPost("/restore", async (HttpContext http, BackupService service, CancellationToken ct) =>
        {
            IFormFile? file = null;
            if (http.Request.HasFormContentType)
            {
                try
                {
                    IFormCollection form = await http.Request.ReadFormAsync(ct);
                    file = form.Files.GetFile("file");
                }
                catch (InvalidDataException)
                {
                    file = null;
                }
                catch (IOException)
                {
                    file = null;
                }
            }

            if (file is null || file.Length == 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A backup zip is required.");
            }

            try
            {
                await using Stream stream = file.OpenReadStream();
                await service.StageRestoreAsync(stream, file.Length, ct);
                return Results.Accepted("/api/v1/backups", new RestoreAccepted(true));
            }
            catch (BackupRejectedException rejected)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: rejected.Title);
            }
        })
            .AddEndpointFilter((context, next) =>
            {
                IHttpMaxRequestBodySizeFeature? limit = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false })
                {
                    limit.MaxRequestBodySize = BackupOptions.HardMaxUploadBytes;
                }

                return next(context);
            })
            .DisableAntiforgery()
            .WithName("RestoreBackup")
            .WithSummary("Checks a backup zip and stages it. The files are swapped in on the next start, and the current data is kept as a backup.")
            .Produces<RestoreAccepted>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}

public sealed record RestoreAccepted(bool RestorePending);
