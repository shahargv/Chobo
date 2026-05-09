namespace ChoboServer.Data;

public sealed class SchemaDefinitionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SchemaHash { get; set; } = "";
    public string Database { get; set; } = "";
    public string Table { get; set; } = "";
    public string Engine { get; set; } = "";
    public string CreateTableSql { get; set; } = "";
    public string ColumnsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
