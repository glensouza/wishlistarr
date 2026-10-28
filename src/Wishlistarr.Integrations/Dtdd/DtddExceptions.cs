using System.Net;

namespace Wishlistarr.Integrations.Dtdd;

public class DtddException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class DtddNotConfiguredException() : DtddException("The DoesTheDogDie API key is not configured.");
