using System.Net;

namespace Wishlistarr.Core.MediaServers;

public class MediaServerException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class MediaServerNotConfiguredException(string message) : MediaServerException(message);
