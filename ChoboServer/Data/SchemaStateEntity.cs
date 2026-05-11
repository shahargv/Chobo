namespace ChoboServer.Data;

public sealed class SchemaStateEntity
{
    public int Id { get; set; } = 1;
    public int SchemaVersion { get; set; }
    public string AppliedMigrationId { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; }
    public string ProductVersion { get; set; } = "";
}

