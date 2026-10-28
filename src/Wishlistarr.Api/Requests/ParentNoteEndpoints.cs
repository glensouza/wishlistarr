using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public static class ParentNoteEndpoints
{
    public const int MaxLength = 2000;

    public static void MapParentNotes(this RouteGroupBuilder v1)
    {
        v1.MapGet("/titles/{kind}/{tmdbId}/note", async (string kind, int tmdbId, WishlistarrDbContext db, CancellationToken ct) =>
        {
            if (!TryKey(kind, tmdbId, out MediaKind mediaKind))
            {
                return Invalid();
            }

            ParentNote? note = await db.ParentNotes.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Kind == mediaKind && row.TmdbId == tmdbId, ct);
            return Results.Ok(new ParentNoteResponse(mediaKind, tmdbId, note?.Text));
        })
            .WithTags("Titles")
            .WithName("GetParentNote")
            .WithSummary("Returns the parent note for a movie or show. The same note is shared by every kid profile. A missing note is returned as null.")
            .Produces<ParentNoteResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        v1.MapPut("/titles/{kind}/{tmdbId}/note", async (string kind, int tmdbId, ParentNoteBody? body, WishlistarrDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (!TryKey(kind, tmdbId, out MediaKind mediaKind))
            {
                return Invalid();
            }

            if (body is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A note is required.");
            }

            string text = body.Note?.Trim() ?? string.Empty;
            if (text.Length > MaxLength)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"A parent note can be at most {MaxLength} characters.");
            }

            ParentNote? existing = await db.ParentNotes.SingleOrDefaultAsync(row => row.Kind == mediaKind && row.TmdbId == tmdbId, ct);
            if (text.Length == 0)
            {
                if (existing is not null)
                {
                    db.ParentNotes.Remove(existing);
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok(new ParentNoteResponse(mediaKind, tmdbId, null));
            }

            if (existing is null)
            {
                db.ParentNotes.Add(new ParentNote { Kind = mediaKind, TmdbId = tmdbId, Text = text, UpdatedAt = time.GetUtcNow() });
            }
            else
            {
                existing.Text = text;
                existing.UpdatedAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new ParentNoteResponse(mediaKind, tmdbId, text));
        })
            .WithTags("Titles")
            .WithName("PutParentNote")
            .WithSummary("Saves the parent note for a movie or show, shared by every kid profile. An empty note deletes the stored note. Notes longer than 2000 characters are rejected.")
            .Produces<ParentNoteResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static bool TryKey(string kind, int tmdbId, out MediaKind mediaKind)
    {
        mediaKind = default;
        if (tmdbId <= 0)
        {
            return false;
        }

        if (kind == "movie")
        {
            mediaKind = MediaKind.Movie;
            return true;
        }

        if (kind == "show")
        {
            mediaKind = MediaKind.Show;
            return true;
        }

        return false;
    }

    private static IResult Invalid() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A movie or show and a positive TMDB id are required.");
}

public sealed record ParentNoteBody(string? Note);

public sealed record ParentNoteResponse(MediaKind Kind, int TmdbId, string? Note);
