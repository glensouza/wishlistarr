using Wishlistarr.Core.Requests;

namespace Wishlistarr.Data;

public sealed class MediaRequestSeason
{
    public int MediaRequestId { get; set; }

    public MediaRequest? MediaRequest { get; set; }

    public int Number { get; set; }

    public bool Requested { get; set; }

    public SeasonAvailability Availability { get; set; }

    public int AvailableEpisodes { get; set; }
}
