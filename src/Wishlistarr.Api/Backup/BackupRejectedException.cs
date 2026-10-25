namespace Wishlistarr.Api.Backup;

public enum BackupRejection
{
    TooLarge,
    NotAZip,
    UnsafePath,
    LinkEntry,
    DuplicateEntry,
    TooManyEntries,
    UnexpectedEntry,
    MissingDatabase,
    MissingKeyRing,
    IntegrityFailed,
    SchemaNewer,
    SchemaUnreadable,
}

public sealed class BackupRejectedException(BackupRejection rejection) : Exception
{
    public BackupRejection Rejection { get; } = rejection;

    public string Title => this.Rejection switch
    {
        BackupRejection.TooLarge => "The backup is larger than the allowed size.",
        BackupRejection.NotAZip => "The file is not a valid backup zip.",
        BackupRejection.UnsafePath => "The backup contains an unsafe path.",
        BackupRejection.LinkEntry => "The backup contains a link instead of a file.",
        BackupRejection.DuplicateEntry => "The backup contains the same file more than once.",
        BackupRejection.TooManyEntries => "The backup contains too many files.",
        BackupRejection.UnexpectedEntry => "The backup contains a file that is not part of a Wishlistarr backup.",
        BackupRejection.MissingDatabase => "The backup does not contain the database.",
        BackupRejection.MissingKeyRing => "The backup does not contain the data protection key ring.",
        BackupRejection.IntegrityFailed => "The backup database failed its integrity check.",
        BackupRejection.SchemaNewer => "The backup was made by a newer version and cannot be restored.",
        BackupRejection.SchemaUnreadable => "The backup database schema could not be read.",
        _ => "The backup was rejected.",
    };
}
