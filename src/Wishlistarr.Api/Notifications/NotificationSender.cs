using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;

namespace Wishlistarr.Api.Notifications;

public sealed record NotificationMessage(string Body, string? ApproveUrl = null, string? DenyUrl = null)
{
    public const int DiscordLimit = 2000;

    public string Text => $"{this.Body}{this.Links}";

    private string Links => this.ApproveUrl is null ? string.Empty : $"\nApprove: {this.ApproveUrl}\nDeny: {this.DenyUrl}";

    public string Fit(int limit)
    {
        int room = limit - this.Links.Length;
        return this.Text.Length <= limit || room < 2 ? this.Text : $"{this.Body[..(room - 1)]}…{this.Links}";
    }
}

public sealed class NotificationDeliveryException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class NotificationSender(IHttpClientFactory clients)
{
    public const string ClientName = "notifications";

    public async Task SendAsync(string provider, NotificationConfiguration config, NotificationMessage message, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (provider == "smtp")
            {
                using SmtpClient smtp = new(config.Host, config.Port)
                {
                    EnableSsl = config.EnableSsl,
                    UseDefaultCredentials = false,
                    Credentials = config.Username.Length == 0 ? null : new NetworkCredential(config.Username, config.Password),
                };
                using MailMessage mail = new(config.From, config.To, "Wishlistarr notification", message.Text)
                {
                    BodyEncoding = Encoding.UTF8,
                    SubjectEncoding = Encoding.UTF8,
                };
                await smtp.SendMailAsync(mail, timeout.Token);
                return;
            }

            using HttpRequestMessage request = new(HttpMethod.Post, config.Url);
            switch (provider)
            {
                case "ntfy":
                    request.Content = new StringContent(message.Text, Encoding.UTF8, "text/plain");
                    request.Headers.Add("Title", "Wishlistarr notification");
                    if (config.Token.Length > 0)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
                    }

                    break;
                case "discord":
                    UriBuilder webhook = new(config.Url);
                    Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(webhook.Query);
                    query["wait"] = "true";
                    webhook.Query = QueryString.Create(query).ToString();
                    request.RequestUri = webhook.Uri;
                    request.Content = JsonContent.Create(new { content = message.Fit(NotificationMessage.DiscordLimit), allowed_mentions = new { parse = Array.Empty<string>() }, flags = 4 });
                    break;
                case "apprise":
                    if (!NotificationSettings.TryAppriseEndpoint(config.Url, out bool stateful))
                    {
                        throw new NotificationDeliveryException("Use an Apprise /notify or /notify/{key} endpoint.");
                    }

                    Dictionary<string, string> payload = new()
                    {
                        ["title"] = "Wishlistarr notification",
                        ["body"] = message.Text,
                        ["format"] = "text",
                    };
                    if (!stateful && !string.IsNullOrWhiteSpace(config.Targets))
                    {
                        payload["urls"] = config.Targets;
                    }

                    request.Content = JsonContent.Create(payload);
                    break;
                default:
                    throw new NotificationDeliveryException("The notification provider is unsupported.");
            }

            using HttpClient client = clients.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || (provider == "apprise" && response.StatusCode != HttpStatusCode.OK))
            {
                throw new NotificationDeliveryException(Describe(response.StatusCode), RetryAfter(response));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or SmtpException or TaskCanceledException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            // Upstream exception text can contain credentials, addresses or signed links.
            throw new NotificationDeliveryException("The notification service could not be reached or rejected delivery.");
        }
    }

    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"The notification service rejected the saved credentials (HTTP {(int)status}).",
        HttpStatusCode.NotFound => "The notification endpoint was not found (HTTP 404). Check the saved URL.",
        HttpStatusCode.TooManyRequests => "The notification service is limiting how often messages can be sent (HTTP 429).",
        _ when (int)status is >= 300 and < 400 => $"The notification endpoint redirected (HTTP {(int)status}). Save the final URL instead.",
        _ => $"Notification delivery returned HTTP {(int)status}.",
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.StatusCode != HttpStatusCode.TooManyRequests ? null
        : response.Headers.RetryAfter?.Delta;
}
