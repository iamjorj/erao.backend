namespace Erao.Core.DTOs.Chat;

public class ClarificationRequest
{
    public string Question { get; set; } = string.Empty;
    public List<ClarificationOption> Options { get; set; } = new();
}

public class ClarificationOption
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
