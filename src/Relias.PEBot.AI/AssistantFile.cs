namespace Relias.PEBot.AI;

/// <summary>
/// Represents file information for files attached to an Assistant
/// </summary>
public class AssistantFile
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
    public string AssistantId { get; set; } = string.Empty;
}