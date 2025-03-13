namespace Relias.PEBot.AI;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Manages Azure OpenAI Assistant operations like creation, verification, and updates
/// </summary>
public class AssistantManager : IAssistantManager
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiVersion;
    private readonly string _assistantId;
    private readonly string[] _assistantTools;
    private readonly string? _vectorStoreId;

    private const string FileSearchTool = "file_search";
    private const string DefaultDeploymentName = "assistants";
    private const string ContentType = "application/json";
    private const string ErrorNotFound = "404";
    
    public AssistantManager(HttpClient httpClient, string apiUrl, string apiVersion, string assistantId, string? vectorStoreId)
    {
        _httpClient = httpClient;
        _apiUrl = apiUrl;
        _apiVersion = apiVersion;
        _assistantId = assistantId;
        _vectorStoreId = vectorStoreId;
        _assistantTools = [FileSearchTool];
    }

    private string GetAssistantUrl(string? path = null)
    {
        var baseUrl = $"{_apiUrl}/openai/deployments/{DefaultDeploymentName}";
        if (!string.IsNullOrEmpty(path))
        {
            baseUrl = $"{baseUrl}/{path}";
        }
        return $"{baseUrl}?api-version={_apiVersion}";
    }

    public async Task<bool> VerifyAssistantAsync(string? systemPrompt = null)
    {
        var requestUrl = GetAssistantUrl($"assistants/{_assistantId}");
        
        try
        {
            var response = await _httpClient.GetAsync(requestUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Error verifying assistant: {response.StatusCode}. Details: {errorBody}");
                return false;
            }

            Console.WriteLine($"Successfully verified assistant {_assistantId} exists");
            
            try
            {
                if (!string.IsNullOrEmpty(systemPrompt))
                {
                    await UpdateAssistantInstructionsAsync(systemPrompt);
                }

                if (!string.IsNullOrEmpty(_vectorStoreId))
                {
                    var responseBody = await response.Content.ReadAsStreamAsync();
                    using var document = await JsonDocument.ParseAsync(responseBody);
                    await VerifyVectorStoreAsync(document);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Assistant exists but updates failed: {ex.Message}");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error verifying assistant: {ex.Message}");
            return false;
        }
    }

    public async Task<string> CreateThreadAsync()
    {
        var requestUrl = GetAssistantUrl("threads");
        
        var response = await _httpClient.PostAsync(requestUrl, new StringContent("{}", Encoding.UTF8, ContentType));
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to create thread: {response.StatusCode}. Details: {errorBody}");
        }
        
        var responseBody = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseBody);
        
        var threadId = document.RootElement.GetProperty("id").GetString() ?? 
            throw new InvalidOperationException("Failed to get thread ID from response");
        
        Console.WriteLine($"Created new thread with ID: {threadId}");
        return threadId;
    }

    private async Task<bool> VerifyFileSearchToolAsync(JsonDocument document)
    {
        var toolsProperty = document.RootElement.GetProperty("tools");
        foreach (var tool in toolsProperty.EnumerateArray())
        {
            if (tool.TryGetProperty("type", out var typeProperty) && typeProperty.GetString() == FileSearchTool)
            {
                return true;
            }
        }

        Console.WriteLine($"Warning: Assistant {_assistantId} does not have the file_search tool enabled.");
        Console.WriteLine("Attempting to add file_search tool to the assistant...");
        
        try
        {
            await UpdateAssistantToolsAsync();
            Console.WriteLine("Successfully updated assistant with file_search tool.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not update assistant tools: {ex.Message}");
            Console.WriteLine("Will continue with existing assistant configuration.");
            return false;
        }
    }

    private Task VerifyVectorStoreAsync(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("tool_resources", out var toolResourcesProperty) || 
            !toolResourcesProperty.TryGetProperty("file_search", out var fileSearchProperty) || 
            !fileSearchProperty.TryGetProperty("vector_store_ids", out var vectorStoreIdsProperty))
        {
            throw new InvalidOperationException($"Assistant {_assistantId} is missing required vector store configuration");
        }

        var vectorStoreConfigured = false;
        foreach (var id in vectorStoreIdsProperty.EnumerateArray())
        {
            if (id.GetString() == _vectorStoreId)
            {
                vectorStoreConfigured = true;
                break;
            }
        }

        if (!vectorStoreConfigured)
        {
            throw new InvalidOperationException($"Assistant {_assistantId} is not configured with vector store {_vectorStoreId}");
        }

        return Task.CompletedTask;
    }

    public async Task UpdateAssistantToolsAsync()
    {
        var requestUrl = GetAssistantUrl($"assistants/{_assistantId}");
        
        var updateRequest = new
        {
            tools = _assistantTools.Select(tool => new { type = tool }).ToList()
        };

        var content = new StringContent(
            JsonSerializer.Serialize(updateRequest),
            Encoding.UTF8,
            ContentType);

        var request = new HttpRequestMessage(HttpMethod.Patch, requestUrl)
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(request);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to update assistant tools: {response.StatusCode}. Details: {errorBody}");
        }
    }

    private async Task UpdateAssistantInstructionsAsync(string instructions)
    {
        var requestUrl = GetAssistantUrl($"assistants/{_assistantId}");
        
        var updateRequest = new
        {
            instructions = instructions
        };

        var content = new StringContent(
            JsonSerializer.Serialize(updateRequest),
            Encoding.UTF8,
            ContentType);

        var request = new HttpRequestMessage(HttpMethod.Patch, requestUrl)
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(request);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to update assistant instructions: {response.StatusCode}. Details: {errorBody}");
        }
        
        Console.WriteLine("Successfully updated assistant instructions");
    }

    public async Task<IList<AssistantFile>> GetAssistantFilesAsync()
    {
        var requestUrl = GetAssistantUrl($"assistants/{_assistantId}/files");
        
        var response = await _httpClient.GetAsync(requestUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to get assistant files: {response.StatusCode}. Details: {errorBody}");
        }
        
        var result = new List<AssistantFile>();
        var responseBody = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseBody);
        
        if (document.RootElement.TryGetProperty("data", out var dataElement))
        {
            foreach (var fileItem in dataElement.EnumerateArray())
            {
                var file = new AssistantFile
                {
                    Id = fileItem.GetProperty("id").GetString() ?? string.Empty,
                    Object = fileItem.GetProperty("object").GetString() ?? string.Empty,
                    CreatedAt = fileItem.GetProperty("created_at").GetInt64(),
                    AssistantId = fileItem.GetProperty("assistant_id").GetString() ?? string.Empty
                };
                
                result.Add(file);
            }
        }
        
        return result;
    }

    public async Task<IList<string>> GetAssistantVectorStoresAsync()
    {
        var requestUrl = GetAssistantUrl($"assistants/{_assistantId}");
        
        var response = await _httpClient.GetAsync(requestUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to get assistant: {response.StatusCode}. Details: {errorBody}");
        }
        
        var result = new List<string>();
        var responseBody = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseBody);
        
        if (document.RootElement.TryGetProperty("tool_resources", out var toolResourcesProperty) && 
            toolResourcesProperty.TryGetProperty("file_search", out var fileSearchProperty) && 
            fileSearchProperty.TryGetProperty("vector_store_ids", out var vectorStoreIdsProperty))
        {
            foreach (var id in vectorStoreIdsProperty.EnumerateArray())
            {
                var vectorStoreId = id.GetString();
                if (!string.IsNullOrEmpty(vectorStoreId))
                {
                    result.Add(vectorStoreId);
                }
            }
        }
        
        return result;
    }
}