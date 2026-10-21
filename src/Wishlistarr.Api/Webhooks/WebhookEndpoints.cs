using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;

namespace Wishlistarr.Api.Webhooks;

public static class WebhookEndpoints
{
    public static IServiceCollection AddWebhooks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .Validate(options => options.IsValid(), "The Webhooks configuration section is invalid.")
            .ValidateOnStart();
        services.AddScoped<WebhookTokens>();
        services.AddScoped<WebhookIntake>();
        foreach (WebhookSource source in Enum.GetValues<WebhookSource>())
        {
            services.AddScoped<IWebhookHandler>(provider => new RecordingWebhookHandler(
                source,
                provider.GetRequiredService<ILogger<RecordingWebhookHandler>>()));
        }

        services.AddBackgroundJob<WebhookEventProcessor>(
            WebhookEventProcessor.JobName,
            "Processes received webhook events.",
            TimeSpan.FromMinutes(1));
        return services;
    }

    public static void MapWebhookSettings(this RouteGroupBuilder v1)
    {
        v1.MapGet("/settings/webhooks", async (HttpRequest request, WebhookTokens tokens, CancellationToken ct) =>
        {
            List<WebhookEndpointView> views = [];
            foreach (WebhookSource source in Enum.GetValues<WebhookSource>())
            {
                views.Add(await ViewAsync(request, tokens, source, ct));
            }

            return Results.Ok(views);
        })
            .WithTags("Settings")
            .WithName("ListWebhookSettings")
            .WithSummary("Lists the inbound webhook address of each source. The addresses contain secret tokens.")
            .Produces<IReadOnlyList<WebhookEndpointView>>();

        v1.MapPost("/settings/webhooks/{source}/regenerate", async (
            string source,
            HttpRequest request,
            WebhookTokens tokens,
            CancellationToken ct) =>
        {
            if (!WebhookSources.TryParse(source, out WebhookSource parsed))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The webhook source does not exist.");
            }

            await tokens.RegenerateAsync(parsed, ct);
            return Results.Ok(await ViewAsync(request, tokens, parsed, ct));
        })
            .WithTags("Settings")
            .WithName("RegenerateWebhookToken")
            .WithSummary("Replaces the secret token of a webhook source. The previous address stops working.")
            .Produces<WebhookEndpointView>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public static void MapWebhookReceiver(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/webhooks/{source}/{token}", async (
            string source,
            string token,
            HttpRequest request,
            WebhookTokens tokens,
            WebhookIntake intake,
            IOptions<WebhookOptions> options,
            CancellationToken ct) =>
        {
            if (!WebhookSources.TryParse(source, out WebhookSource parsed))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The webhook source does not exist.");
            }

            if (!await tokens.IsValidAsync(parsed, token, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The webhook token is not valid.");
            }

            int limit = options.Value.MaxPayloadBytes;
            if (request.ContentLength > limit)
            {
                return TooLarge();
            }

            byte[]? body = await ReadBoundedAsync(request.Body, limit, ct);
            if (body is null)
            {
                return TooLarge();
            }

            ParsedWebhook? webhook = await WebhookPayloads.ParseAsync(parsed, request.ContentType, body, ct);
            if (webhook is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The webhook payload is not valid.");
            }

            return await intake.EnqueueAsync(parsed, webhook, ct) == WebhookIntakeResult.Accepted
                ? Results.Accepted(null, new WebhookReceipt("accepted"))
                : Results.Ok(new WebhookReceipt("duplicate"));
        })
            .AllowAnonymous()
            .WithTags("Webhooks")
            .WithName("ReceiveWebhook")
            .WithSummary("Receives a Plex, Sonarr, Radarr or Seerr webhook. Authenticated by the token in the address.")
            .Produces<WebhookReceipt>(StatusCodes.Status202Accepted)
            .Produces<WebhookReceipt>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    private static IResult TooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The webhook payload is too large.");

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<WebhookEndpointView> ViewAsync(
        HttpRequest request,
        WebhookTokens tokens,
        WebhookSource source,
        CancellationToken ct)
    {
        string token = await tokens.GetAsync(source, ct);
        string url = $"{request.Scheme}://{request.Host}{WebhookTokenRedactor.RoutePrefix}{source.Route()}/{token}";
        return new WebhookEndpointView(source.Route(), url);
    }
}

public sealed record WebhookEndpointView(string Source, string Url);

public sealed record WebhookReceipt(string Status);
