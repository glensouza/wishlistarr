using System.Net;

namespace Wishlistarr.Integrations.Tmdb;

public class TmdbException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class TmdbNotConfiguredException() : TmdbException("The TMDB API key is not configured.");
