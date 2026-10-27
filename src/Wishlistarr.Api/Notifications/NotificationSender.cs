using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;

namespace Wishlistarr.Api.Notifications;

public sealed record NotificationMessage(string Body, string? ApproveUrl = null, string? DenyUrl = null)
{
    public string Text => this.ApproveUrl is null ? this.Body : $"{this.Body}\nApprove: {this.ApproveUrl}\nDeny: {this.DenyUrl}";
}

public sealed class NotificationDeliveryException(string message) : Exception(message);

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
                    request.Content = JsonContent.Create(new { content = message.Text, allowed_mentions = new { parse = Array.Empty<string>() }, flags = 4 });
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
                throw new NotificationDeliveryException($"Notification delivery returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or SmtpException or TaskCanceledException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            // Upstream exception text can contain credentials, addresses or signed links.
            throw new NotificationDeliveryException("The notification service could not be reached or rejected delivery.");
        }
    }
}
