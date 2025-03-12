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
    private const string DefaultModel = "gpt-4o-mini";
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

    public async Task<bool> VerifyAssistantAsync()
    {
        var requestUrl = $"{_apiUrl}/openai/assistants/{_assistantId}?api-version={_apiVersion}";
        
        try
        {
            var response = await _httpClient.GetAsync(requestUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Failed to verify assistant: {response.StatusCode}. Details: {errorBody}");
                return false;
            }

            var responseBody = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseBody);
            
            // Check if the assistant has the file_search tool enabled
            var hasFileSearchTool = await VerifyFileSearchToolAsync(document);
            
            // Check for vector store configuration
            if (!string.IsNullOrEmpty(_vectorStoreId))
            {
                await VerifyVectorStoreAsync(document);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error verifying assistant: {ex.Message}");
            return false;
        }
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

    private async Task VerifyVectorStoreAsync(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("tool_resources", out var toolResourcesProperty) && 
            toolResourcesProperty.TryGetProperty("file_search", out var fileSearchProperty) && 
            fileSearchProperty.TryGetProperty("vector_store_ids", out var vectorStoreIdsProperty))
        {
            var vectorStoreIds = new List<string>();
            foreach (var id in vectorStoreIdsProperty.EnumerateArray())
            {
                vectorStoreIds.Add(id.GetString() ?? string.Empty);
            }
            
            Console.WriteLine($"Assistant uses {vectorStoreIds.Count} vector stores: {string.Join(", ", vectorStoreIds)}");
        }
        else
        {
            Console.WriteLine("Assistant does not have a vector store configured. Will attempt to add it.");
            await UpdateAssistantVectorStoreAsync();
        }
    }

    public async Task UpdateAssistantVectorStoreAsync()
    {
        if (string.IsNullOrEmpty(_vectorStoreId))
        {
            Console.WriteLine("No vector store ID configured. Skipping vector store update.");
            return;
        }
        
        var requestUrl = $"{_apiUrl}/openai/assistants/{_assistantId}?api-version={_apiVersion}";
        
        var toolResources = new
        {
            file_search = new
            {
                vector_store_ids = new[] { _vectorStoreId }
            }
        };
        
        var updateRequest = new
        {
            tool_resources = toolResources
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
            
            try
            {
                using var document = await JsonDocument.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(errorBody)));
                if (document.RootElement.TryGetProperty("error", out var errorElement) && 
                    errorElement.TryGetProperty("code", out var codeElement) && 
                    codeElement.GetString() == ErrorNotFound)
                {
                    throw new InvalidOperationException($"Assistant with ID {_assistantId} not found. Details: {errorBody}");
                }
            }
            catch (JsonException)
            {
                // If JSON parsing fails, just throw with the original error message
            }
            
            throw new InvalidOperationException($"Failed to update assistant tools: {response.StatusCode}. Details: {errorBody}");
        }
    }

    public async Task UpdateAssistantToolsAsync()
    {
        var requestUrl = $"{_apiUrl}/openai/assistants/{_assistantId}?api-version={_apiVersion}";
        
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

    public async Task<string> CreateAssistantAsync(string? systemPrompt)
    {
        var requestUrl = $"{_apiUrl}/openai/assistants?api-version={_apiVersion}";
        
        // Create tools configuration for file_search
        var tools = _assistantTools.Select(tool => new { type = tool }).ToList();
        
        // Prepare the assistant request with or without vector store ID
        object assistantRequest;
        if (!string.IsNullOrEmpty(_vectorStoreId))
        {
            var toolResources = new
            {
                file_search = new
                {
                    vector_store_ids = new[] { _vectorStoreId }
                }
            };
            
            assistantRequest = new
            {
                model = DefaultModel,
                name = "PE Assistant",
                description = "Relias PE Bot Assistant",
                instructions = systemPrompt ?? "You are a helpful assistant.",
                tools,
                tool_resources = toolResources
            };
        }
        else
        {
            assistantRequest = new
            {
                model = DefaultModel,
                name = "PE Assistant",
                description = "Relias PE Bot Assistant",
                instructions = systemPrompt ?? "You are a helpful assistant.",
                tools
            };
        }

        var content = new StringContent(
            JsonSerializer.Serialize(assistantRequest),
            Encoding.UTF8,
            ContentType);

        var response = await _httpClient.PostAsync(requestUrl, content);
        
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseBody);
            return document.RootElement.GetProperty("id").GetString() ?? 
                   throw new InvalidOperationException("Failed to get assistant ID from response");
        }
        
        var errorBody = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Failed to create assistant: {response.StatusCode}. Details: {errorBody}");
    }

    public async Task<string> CreateThreadAsync()
    {
        var requestUrl = $"{_apiUrl}/openai/threads?api-version={_apiVersion}";
        var content = new StringContent("{}", Encoding.UTF8, ContentType);
        var response = await _httpClient.PostAsync(requestUrl, content);
        
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseBody);
            return document.RootElement.GetProperty("id").GetString() ?? 
                   throw new InvalidOperationException("Failed to get thread ID from response");
        }
        
        var errorBody = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Failed to create thread: {response.StatusCode}. Details: {errorBody}");
    }

    public async Task<IList<AssistantFile>> GetAssistantFilesAsync()
    {
        var requestUrl = $"{_apiUrl}/openai/assistants/{_assistantId}/files?api-version={_apiVersion}";
        
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
        var requestUrl = $"{_apiUrl}/openai/assistants/{_assistantId}?api-version={_apiVersion}";
        
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