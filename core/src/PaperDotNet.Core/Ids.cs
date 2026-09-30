namespace PaperDotNet.Core;

public static class Ids
{
    /// <summary>New time-ordered identifier (UUIDv7), good for index locality and keyset paging.</summary>
    public static Guid New() => Guid.CreateVersion7();
}
