namespace Relias.PEBot.AI;

using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Interface for Azure OpenAI Assistant run and thread operations
/// </summary>
public interface IAssistantRunManager
{
    Task<Dictionary<string, string>> ProcessFunctionCallsAsync(string runId);
    Task SubmitToolOutputsAsync(string runId, Dictionary<string, string> toolOutputs);
    Task<string> PollRunUntilCompletionAsync(string runId);
    Task<string> AddMessageToThreadAsync(string role, string content);
    Task<string> GetLatestAssistantMessageAsync();
}