namespace Erao.Core.DTOs.Admin;

public class AdminConversationDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? DataSourceName { get; set; }
    public string? DataSourceType { get; set; }
    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AdminConversationListDto
{
    public List<AdminConversationDto> Conversations { get; set; } = new();
    public int TotalCount { get; set; }
}

public class AdminMessageDto
{
    public Guid Id { get; set; }
    public int Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? SqlQuery { get; set; }
    public string? QueryResult { get; set; }
    public int TokensUsed { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdminConversationDetailDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? DataSourceName { get; set; }
    public string? DataSourceType { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public List<AdminMessageDto> Messages { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
