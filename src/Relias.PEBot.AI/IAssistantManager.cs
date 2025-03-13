namespace Relias.PEBot.AI;

using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Interface for Azure OpenAI Assistant management operations
/// </summary>
public interface IAssistantManager
{
    Task<bool> VerifyAssistantAsync(string? systemPrompt = null);
    Task UpdateAssistantToolsAsync();
    Task<string> CreateThreadAsync();
    Task<IList<AssistantFile>> GetAssistantFilesAsync();
    Task<IList<string>> GetAssistantVectorStoresAsync();
}