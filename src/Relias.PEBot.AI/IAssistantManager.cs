namespace Relias.PEBot.AI;

using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Interface for Azure OpenAI Assistant management operations
/// </summary>
public interface IAssistantManager
{
    Task<bool> VerifyAssistantAsync();
    Task UpdateAssistantVectorStoreAsync();
    Task UpdateAssistantToolsAsync();
    Task<string> CreateAssistantAsync(string? systemPrompt);
    Task<string> CreateThreadAsync();
    Task<IList<AssistantFile>> GetAssistantFilesAsync();
    Task<IList<string>> GetAssistantVectorStoresAsync();
}