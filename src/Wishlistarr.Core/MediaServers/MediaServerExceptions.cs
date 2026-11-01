using System.Net;

namespace Wishlistarr.Core.MediaServers;

public class MediaServerException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    public bool Resent { get; set; }
}

public sealed class MediaServerNotConfiguredException(string message) : MediaServerException(message);

public sealed class MediaServerItemChangedException(string message) : MediaServerException(message);
