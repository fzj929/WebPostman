namespace WebPostman.Api.Data;

public sealed class UserEntity
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long ShareRevision { get; set; }
}

public sealed class SessionEntity
{
    public string TokenHash { get; set; } = "";
    public string UserId { get; set; } = "";
    public long Expires { get; set; }
}

public sealed class ShareEntity
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public long CreatedAt { get; set; }
    public string Json { get; set; } = "";
}

public sealed class WorkspaceStateEntity
{
    public string UserId { get; set; } = "";
    public long Revision { get; set; }
}

public sealed class CollectionEntity
{
    public string UserId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Json { get; set; } = "";
    public int Position { get; set; }
}

public sealed class EnvironmentEntity
{
    public string UserId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Json { get; set; } = "";
    public int Position { get; set; }
}

public sealed class HistoryEntity
{
    public string UserId { get; set; } = "";
    public string Id { get; set; } = "";
    public long At { get; set; }
    public string Json { get; set; } = "";
}

public sealed class DataImportEntity
{
    public string Id { get; set; } = "";
}
