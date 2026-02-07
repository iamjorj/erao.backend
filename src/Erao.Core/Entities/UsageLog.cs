namespace Erao.Core.Entities;

public class UsageLog : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? DatabaseConnectionId { get; set; }
    public string QueryType { get; set; } = string.Empty;
    public int TokensUsed { get; set; }
    public int ExecutionTimeMs { get; set; }

    // Navigation properties
    public virtual User User { get; set; } = null!;
    public virtual DatabaseConnection? DatabaseConnection { get; set; }
}
