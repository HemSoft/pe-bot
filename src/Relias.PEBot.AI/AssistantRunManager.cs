namespace Relias.PEBot.AI;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Manages Azure OpenAI Assistant run operations including tool calls and message handling
/// </summary>
public class AssistantRunManager : IAssistantRunManager
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiVersion;
    private readonly string _threadId;
    private readonly List<AIFunction> _functions;

    private const string ContentType = "application/json";
    private const int MaxRetries = 100;
    private const int InitialDelayMs = 1000;
    private const int MaxDelayMs = 5000;
    private const string DefaultDeploymentName = "assistants";

    public AssistantRunManager(
        HttpClient httpClient, 
        string apiUrl, 
        string apiVersion, 
        string threadId,
        List<AIFunction> functions)
    {
        _httpClient = httpClient;
        _apiUrl = apiUrl;
        _apiVersion = apiVersion;
        _threadId = threadId;
        _functions = functions;
    }

    private string GetAssistantUrl(string path)
    {
        return $"{_apiUrl}/openai/deployments/{DefaultDeploymentName}/{path}?api-version={_apiVersion}";
    }

    public async Task<Dictionary<string, string>> ProcessFunctionCallsAsync(string runId)
    {
        var toolOutputs = new Dictionary<string, string>();
        var requestUrl = GetAssistantUrl($"threads/{_threadId}/runs/{runId}");
        var response = await _httpClient.GetAsync(requestUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to get run status: {response.StatusCode}");
            return toolOutputs;
        }

        using var responseBody = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseBody);

        Console.WriteLine("Checking run response for required actions...");
        Console.WriteLine($"Raw response: {await response.Content.ReadAsStringAsync()}");
        
        if (document.RootElement.TryGetProperty("required_action", out var requiredAction))
        {
            Console.WriteLine($"Found required_action: {requiredAction}");
            var toolCalls = requiredAction.GetProperty("submit_tool_outputs").GetProperty("tool_calls");
            Console.WriteLine($"Found {toolCalls.GetArrayLength()} tool calls");

            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var toolCallId = toolCall.GetProperty("id").GetString();
                var functionObj = toolCall.GetProperty("function");
                var functionName = functionObj.GetProperty("name").GetString();
                var arguments = functionObj.GetProperty("arguments").GetString();
                
                Console.WriteLine($"Processing tool call {toolCallId} for function {functionName}");
                Console.WriteLine($"Arguments: {arguments}");

                var matchingFunction = _functions.FirstOrDefault(f => f.Name == functionName);
                if (matchingFunction != null)
                {
                    try
                    {
                        var result = await matchingFunction.InvokeAsync(arguments ?? string.Empty);
                        Console.WriteLine($"Function returned: {result}");
                        if (toolCallId != null)
                        {
                            toolOutputs[toolCallId] = result;
                            Console.WriteLine($"Added result to tool outputs: {toolCallId} = {result}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error executing function {functionName}: {ex.Message}");
                        if (toolCallId != null)
                        {
                            toolOutputs[toolCallId] = $"Error: {ex.Message}";
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"No matching function found for {functionName}");
                }
            }
        }

        Console.WriteLine($"Returning {toolOutputs.Count} tool outputs");
        return toolOutputs;
    }

    public async Task SubmitToolOutputsAsync(string runId, Dictionary<string, string> toolOutputs)
    {
        var requestUrl = GetAssistantUrl($"threads/{_threadId}/runs/{runId}/submit_tool_outputs");
        
        if (!toolOutputs.Any())
        {
            Console.WriteLine("No tool outputs to submit");
            return;
        }

        var outputs = toolOutputs.Select(kvp => new
        {
            tool_call_id = kvp.Key,
            output = kvp.Value
        }).ToArray();

        var submitRequest = new
        {
            tool_outputs = outputs
        };

        Console.WriteLine($"Submitting {outputs.Length} tool outputs");
        var content = new StringContent(
            JsonSerializer.Serialize(submitRequest),
            Encoding.UTF8,
            ContentType);

        var response = await _httpClient.PostAsync(requestUrl, content);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to submit tool outputs: {response.StatusCode}. Details: {errorBody}");
        }
    }

    public async Task<string> PollRunUntilCompletionAsync(string runId)
    {
        int retries = 0;
        int delayMs = InitialDelayMs; 
        string status;
        
        do
        {
            await Task.Delay(delayMs);
            
            var requestUrl = GetAssistantUrl($"threads/{_threadId}/runs/{runId}");
            var response = await _httpClient.GetAsync(requestUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to get run status: {response.StatusCode}. Details: {errorBody}");
            }
            
            using var responseBody = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseBody);
            
            status = document.RootElement.GetProperty("status").GetString() ?? 
                    throw new InvalidOperationException("Failed to get status from response");
            
            retries++;
            if (retries > MaxRetries)
            {
                throw new TimeoutException("Maximum retries reached waiting for run completion");
            }
            
            if (status == "queued" || status == "in_progress")
            {
                delayMs = Math.Min(delayMs * 2, MaxDelayMs);
            }
            
        } while (status == "queued" || status == "in_progress");
        
        return status;
    }

    public async Task<string> AddMessageToThreadAsync(string role, string content)
    {
        var requestUrl = GetAssistantUrl($"threads/{_threadId}/messages");
        
        var messageRequest = new
        {
            role = role,
            content = content
        };

        var requestContent = new StringContent(
            JsonSerializer.Serialize(messageRequest),
            Encoding.UTF8,
            ContentType);

        var response = await _httpClient.PostAsync(requestUrl, requestContent);
        
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseBody);
            return document.RootElement.GetProperty("id").GetString() ?? 
                   throw new InvalidOperationException("Failed to get message ID from response");
        }
        
        var errorBody = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Failed to add message to thread: {response.StatusCode}. Details: {errorBody}");
    }

    public async Task<string> GetLatestAssistantMessageAsync()
    {
        var messageRequestUrl = GetAssistantUrl($"threads/{_threadId}/messages");
        var messageResponse = await _httpClient.GetAsync(messageRequestUrl);
        
        if (!messageResponse.IsSuccessStatusCode)
        {
            var errorBody = await messageResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to get messages: {messageResponse.StatusCode}. Details: {errorBody}");
        }
        
        string responseText = string.Empty;
        using (var responseBody = await messageResponse.Content.ReadAsStreamAsync())
        using (var document = await JsonDocument.ParseAsync(responseBody))
        {
            var messages = document.RootElement.GetProperty("data");
            if (messages.GetArrayLength() > 0)
            {
                var latestMessage = messages[0];
                var role = latestMessage.GetProperty("role").GetString();
                
                if (role == "assistant")
                {
                    var content = latestMessage.GetProperty("content");
                    foreach (var contentItem in content.EnumerateArray())
                    {
                        if (contentItem.TryGetProperty("text", out var textElement) && 
                            contentItem.GetProperty("type").GetString() == "text")
                        {
                            responseText += textElement.GetProperty("value").GetString();
                        }
                    }
                }
            }
        }
        
        return responseText;
    }
}