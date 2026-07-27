namespace Lumina.Shared.Models.Enums;

public enum SourceType
{
    Git = 0,
    Tar = 1,
    Http = 2,
    Ftp = 3,
    Rsync = 4,
    Svn = 5,
    Hg = 6,
    Local = 7
}

public enum SourceStatus
{
    Pending = 0,
    Fetching = 1,
    Ready = 2,
    Failed = 3,
    Cancelled = 4
}
