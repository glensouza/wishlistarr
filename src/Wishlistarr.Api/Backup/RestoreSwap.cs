using System.Text.Json;
using System.Text.Json.Serialization;
using Wishlistarr.Core;

namespace Wishlistarr.Api.Backup;

internal enum RestoreStep
{
    JournalWritten,
    DatabaseRetired,
    KeysRetired,
    DatabasePlaced,
    KeysPlaced,
    Committed,
}

internal enum RestorePhase
{
    Swapping,
    Committed,
}

internal sealed record RestoreJournal(RestorePhase Phase, bool HadDatabase, bool HadKeys);

// The journal is written before anything moves and is the only record of which side of the swap
// each file is on. Startup reads it to roll a half-done swap back, or to finish cleaning up after
// a committed one, so the database and the key ring always end up as a matching pair.
internal sealed class RestoreSwap(DataDirectory directory, ILogger logger, Action<RestoreStep>? afterStep)
{
    private const string KeysName = "keys";

    private static readonly string[] DatabaseSuffixes = ["-wal", "-shm", "-journal"];

    private static readonly JsonSerializerOptions JournalJson = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool InProgress => File.Exists(directory.RestoreJournalPath);

    private string JournalTempPath => directory.RestoreJournalPath + ".tmp";

    private string StagedDatabase => Path.Combine(directory.RestorePendingPath, BackupNames.DatabaseEntryName);

    private string StagedKeys => Path.Combine(directory.RestorePendingPath, KeysName);

    private string PreviousDatabase => Path.Combine(directory.RestorePreviousPath, DataDirectory.DatabaseFileName);

    private string PreviousKeys => Path.Combine(directory.RestorePreviousPath, KeysName);

    public void Run()
    {
        if (this.InProgress || Directory.Exists(directory.RestorePreviousPath))
        {
            throw new InvalidOperationException("An earlier restore was not cleaned up, so the pending restore was not applied.");
        }

        RestoreJournal journal = new(
            RestorePhase.Swapping,
            File.Exists(directory.DatabasePath),
            Directory.Exists(directory.KeysPath));
        this.WriteJournal(journal);
        try
        {
            this.Step(RestoreStep.JournalWritten);
            Directory.CreateDirectory(directory.RestorePreviousPath);
            foreach (string suffix in DatabaseSuffixes)
            {
                MoveFileIfExists(directory.DatabasePath + suffix, this.PreviousDatabase + suffix);
            }

            MoveFileIfExists(directory.DatabasePath, this.PreviousDatabase);
            this.Step(RestoreStep.DatabaseRetired);

            if (Directory.Exists(directory.KeysPath))
            {
                Directory.Move(directory.KeysPath, this.PreviousKeys);
            }

            this.Step(RestoreStep.KeysRetired);

            if (File.Exists(directory.DatabasePath) || DatabaseSuffixes.Any(suffix => File.Exists(directory.DatabasePath + suffix)))
            {
                throw new IOException("The current database could not be moved aside.");
            }

            File.Move(this.StagedDatabase, directory.DatabasePath);
            this.Step(RestoreStep.DatabasePlaced);

            if (Directory.Exists(directory.KeysPath))
            {
                throw new IOException("The current key ring could not be moved aside.");
            }

            Directory.Move(this.StagedKeys, directory.KeysPath);
            this.Step(RestoreStep.KeysPlaced);

            if (!File.Exists(directory.DatabasePath) || !Directory.Exists(directory.KeysPath))
            {
                throw new IOException("The restored files are not in place.");
            }

            this.WriteJournal(journal with { Phase = RestorePhase.Committed });
        }
        catch (Exception failure)
        {
            try
            {
                this.RollBack(journal);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "The restore failed and could not be rolled back. It is retried on the next start.",
                    failure,
                    rollbackFailure);
            }

            throw;
        }

        this.Step(RestoreStep.Committed);
        this.TryFinish();
    }

    public void Recover()
    {
        RestoreJournal? journal = this.ReadJournal();
        DeleteFileIfExists(this.JournalTempPath);
        if (journal is null)
        {
            return;
        }

        if (journal.Phase == RestorePhase.Committed)
        {
            this.TryFinish();
        }
        else
        {
            this.RollBack(journal);
        }
    }

    private void RollBack(RestoreJournal journal)
    {
        Directory.CreateDirectory(directory.RestorePendingPath);

        if (Directory.Exists(this.PreviousKeys))
        {
            if (Directory.Exists(directory.KeysPath))
            {
                this.ReturnStagedKeys();
            }

            Directory.Move(this.PreviousKeys, directory.KeysPath);
        }
        else if (!journal.HadKeys && Directory.Exists(directory.KeysPath))
        {
            this.ReturnStagedKeys();
        }

        if (File.Exists(this.PreviousDatabase))
        {
            if (File.Exists(directory.DatabasePath))
            {
                this.ReturnStagedDatabase();
            }

            File.Move(this.PreviousDatabase, directory.DatabasePath);
        }
        else if (!journal.HadDatabase && File.Exists(directory.DatabasePath))
        {
            this.ReturnStagedDatabase();
        }

        foreach (string suffix in DatabaseSuffixes)
        {
            if (File.Exists(this.PreviousDatabase + suffix))
            {
                DeleteFileIfExists(directory.DatabasePath + suffix);
                File.Move(this.PreviousDatabase + suffix, directory.DatabasePath + suffix);
            }
        }

        if (Directory.Exists(directory.RestorePreviousPath))
        {
            Directory.Delete(directory.RestorePreviousPath, recursive: false);
        }

        DeleteFileIfExists(this.JournalTempPath);
        File.Delete(directory.RestoreJournalPath);
    }

    private void ReturnStagedKeys()
    {
        if (Directory.Exists(this.StagedKeys))
        {
            Directory.Delete(directory.KeysPath, recursive: true);
        }
        else
        {
            Directory.Move(directory.KeysPath, this.StagedKeys);
        }
    }

    private void ReturnStagedDatabase()
    {
        if (File.Exists(this.StagedDatabase))
        {
            File.Delete(directory.DatabasePath);
        }
        else
        {
            File.Move(directory.DatabasePath, this.StagedDatabase);
        }
    }

    // The restored data is already live here, so a cleanup failure must not stop startup. The
    // journal stays until cleanup succeeds, and the next start tries again.
    private void TryFinish()
    {
        try
        {
            DeleteFileIfExists(Path.Combine(directory.RestorePendingPath, BackupNames.ReadyFileName));
            if (Directory.Exists(directory.RestorePendingPath))
            {
                Directory.Delete(directory.RestorePendingPath, recursive: true);
            }

            if (Directory.Exists(directory.RestorePreviousPath))
            {
                Directory.Delete(directory.RestorePreviousPath, recursive: true);
            }

            DeleteFileIfExists(this.JournalTempPath);
            File.Delete(directory.RestoreJournalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "The restore was applied, but {PreviousPath} could not be cleaned up. It holds the replaced data and is removed on the next start.",
                directory.RestorePreviousPath);
        }
    }

    private RestoreJournal? ReadJournal()
    {
        if (!File.Exists(directory.RestoreJournalPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(directory.RestoreJournalPath), JournalJson)
                ?? throw new JsonException("The journal is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The restore journal {directory.RestoreJournalPath} could not be read, so startup stopped without changing any files.",
                exception);
        }
    }

    private void WriteJournal(RestoreJournal journal)
    {
        using (FileStream stream = new(this.JournalTempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal, JournalJson);
            stream.Flush(flushToDisk: true);
        }

        File.Move(this.JournalTempPath, directory.RestoreJournalPath, overwrite: true);
    }

    private void Step(RestoreStep step) => afterStep?.Invoke(step);

    private static void MoveFileIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
