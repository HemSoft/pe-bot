namespace Relias.PEBot.AI;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Azure.Core;
using Azure.Identity;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Client for interacting with Azure OpenAI Assistants API.
/// Provides similar functionality to PEChatClient but uses the Assistants API instead.
/// </summary>
public class AssistantClient
{
    private readonly string _apiKey;
    private readonly string _apiUrl;
    private readonly string _apiVersion;
    private readonly string _assistantId;
    private readonly string _threadId;
    private readonly List<ChatMessage> _messages = [];
    private readonly HttpClient _httpClient = new();
    private readonly string[] _assistantTools;
    private readonly string? _vectorStoreId;
    private readonly bool _useEntraId;
    private readonly AccessToken? _accessToken;

    private const string DefaultModel = "gpt-4o-mini";
    private const string FileSearchTool = "file_search";
    private const string ApiVersionDefault = "2024-05-01-preview";  // Updated to latest API version
    private const string ContentType = "application/json";
    private const string AcceptHeader = "application/json";
    private const string ErrorNotFound = "404";
    private const string VectorStoreIdKey = "azureOpenAIVectorStoreId";
    private const string UseEntraIdKey = "useEntraIdAuth";

    public AssistantClient(IConfiguration configuration, string? systemPrompt = null)
    {
        _useEntraId = string.Equals(configuration[UseEntraIdKey], "true", StringComparison.OrdinalIgnoreCase);
        
        if (_useEntraId)
        {
            // Use Azure AD authentication
            var credential = new DefaultAzureCredential();
            _accessToken = credential.GetToken(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), 
                default);
            _apiKey = string.Empty;
        }
        else
        {
            // Use API key authentication
            _apiKey = configuration["azureOpenAIKey"] ?? 
                      throw new ArgumentNullException(nameof(_apiKey), "Azure OpenAI Key cannot be null or empty.");
        }

        _apiUrl = configuration["azureOpenAIUrl"] ?? 
                  throw new ArgumentNullException(nameof(_apiUrl), "Azure OpenAI Url cannot be null or empty.");
        
        // Azure OpenAI Assistants API requires a specific API version
        _apiVersion = configuration["azureOpenAIApiVersion"] ?? ApiVersionDefault;
        
        // Configure vector store ID if available
        _vectorStoreId = configuration[VectorStoreIdKey];
        
        // Set up HttpClient with default headers
        if (_useEntraId)
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken?.Token}");
        }
        else
        {
            _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        }
        _httpClient.DefaultRequestHeaders.Add("Accept", AcceptHeader);

        // Configure tools for the assistant - use file_search instead of retrieval
        _assistantTools = [FileSearchTool];
        
        // Use existing assistant ID from config or create a new assistant
        _assistantId = configuration["azureAssistantId"];
        
        if (string.IsNullOrEmpty(_assistantId))
        {
            Console.WriteLine("No assistant ID provided in configuration. Creating a new assistant...");
            _assistantId = CreateAssistantAsync(systemPrompt).GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine($"Using existing assistant with ID: {_assistantId}");
            // Verify the assistant exists and has the file_search tool enabled
            var assistantExists = VerifyAssistantAsync().GetAwaiter().GetResult();
            
            // If verification fails, create a new assistant
            if (!assistantExists)
            {
                Console.WriteLine($"Assistant with ID {_assistantId} not found or inaccessible. Creating a new assistant...");
                _assistantId = CreateAssistantAsync(systemPrompt).GetAwaiter().GetResult();
            }
        }
        
        // Create a new thread for this conversation
        _threadId = CreateThreadAsync().GetAwaiter().GetResult();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            _messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        }
        
        Console.WriteLine($"AssistantClient initialized with Assistant ID: {_assistantId} and Thread ID: {_threadId}");
    }

    private async Task<bool> VerifyAssistantAsync()
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

            // Check if the assistant has the file_search tool enabled
            var responseBody = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseBody);
            
            var toolsProperty = document.RootElement.GetProperty("tools");
            bool hasFileSearchTool = false;
            
            foreach (var tool in toolsProperty.EnumerateArray())
            {
                if (tool.TryGetProperty("type", out var typeProperty) && typeProperty.GetString() == FileSearchTool)
                {
                    hasFileSearchTool = true;
                    break;
                }
            }
            
            if (!hasFileSearchTool)
            {
                Console.WriteLine($"Warning: Assistant {_assistantId} does not have the file_search tool enabled.");
                Console.WriteLine("Attempting to add file_search tool to the assistant...");
                
                try
                {
                    // Add the file_search tool to the assistant
                    await UpdateAssistantToolsAsync();
                    Console.WriteLine("Successfully updated assistant with file_search tool.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not update assistant tools: {ex.Message}");
                    Console.WriteLine("Will continue with existing assistant configuration.");
                }
            }
            else
            {
                Console.WriteLine($"Assistant {_assistantId} verified with file_search tool enabled.");
            }
            
            // Check for attached files and vector store
            if (document.RootElement.TryGetProperty("file_ids", out var fileIdsProperty))
            {
                var fileCount = fileIdsProperty.GetArrayLength();
                Console.WriteLine($"Assistant has {fileCount} files attached.");
            }
            
            // Check for vector store configuration
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
            else if (!string.IsNullOrEmpty(_vectorStoreId))
            {
                Console.WriteLine("Assistant does not have a vector store configured. Will attempt to add it.");
                await UpdateAssistantVectorStoreAsync();
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error verifying assistant: {ex.Message}");
            return false;
        }
    }
    
    private async Task UpdateAssistantVectorStoreAsync()
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
            Console.WriteLine($"Failed to update assistant vector store: {response.StatusCode}. Details: {errorBody}");
            return;
        }
        
        Console.WriteLine($"Successfully updated assistant with vector store ID: {_vectorStoreId}");
    }
    
    private async Task UpdateAssistantToolsAsync()
    {
        var requestUrl = $"{_apiUrl}/openai/assistants/{_assistantId}?api-version={_apiVersion}";
        
        var tools = _assistantTools.Select(tool => new { type = tool }).ToList();
        
        // Prepare tool_resources if vector store ID is available
        object updateRequest;
        if (!string.IsNullOrEmpty(_vectorStoreId))
        {
            var toolResources = new
            {
                file_search = new
                {
                    vector_store_ids = new[] { _vectorStoreId }
                }
            };
            
            updateRequest = new
            {
                tools,
                tool_resources = toolResources
            };
        }
        else
        {
            updateRequest = new
            {
                tools
            };
        }

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
            
            // Parse the error response to check for specific error codes
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

    private async Task<string> CreateAssistantAsync(string? systemPrompt)
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

    private async Task<string> CreateThreadAsync()
    {
        var requestUrl = $"{_apiUrl}/openai/threads?api-version={_apiVersion}";
        
        // Empty request body for thread creation
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

    /// <summary>
    /// Gets information about files attached to the current assistant
    /// </summary>
    /// <returns>A list of file information</returns>
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
                    Object = fileItem.GetProperty("object").GetString(),
                    CreatedAt = fileItem.GetProperty("created_at").GetInt64(),
                    AssistantId = fileItem.GetProperty("assistant_id").GetString()
                };
                
                result.Add(file);
            }
        }
        
        return result;
    }

    /// <summary>
    /// Gets information about vector stores attached to the current assistant
    /// </summary>
    /// <returns>A list of vector store IDs</returns>
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

    /// <summary>
    /// Uploads a file to Azure OpenAI and attaches it to the assistant
    /// </summary>
    /// <param name="filePath">Path to the file to upload</param>
    /// <param name="purpose">Purpose of the file, default is 'assistants'</param>
    /// <returns>The file ID if successful</returns>
    public async Task<string?> UploadAndAttachFileAsync(string filePath, string purpose = "assistants")
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found", filePath);
        }

        // First upload the file
        var requestUrl = $"{_apiUrl}/openai/files?api-version={_apiVersion}";
        
        var fileName = Path.GetFileName(filePath);
        var fileContent = await File.ReadAllBytesAsync(filePath);
        
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(fileContent), "file", fileName);
        content.Add(new StringContent(purpose), "purpose");
        
        var response = await _httpClient.PostAsync(requestUrl, content);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Failed to upload file: {response.StatusCode}. Details: {errorBody}");
            return null;
        }
            
        var responseBody = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseBody);
        var fileId = document.RootElement.GetProperty("id").GetString();
            
        if (string.IsNullOrEmpty(fileId))
        {
            return null;
        }
        
        Console.WriteLine($"Successfully uploaded file {fileName} with ID: {fileId}");
            
        // Now attach the file to the assistant
        var attachResult = await AttachFileToAssistantAsync(fileId);
        
        if (!attachResult)
        {
            Console.WriteLine($"Failed to attach file {fileId} to assistant.");
            return null;
        }
        
        return fileId;
    }
    
    /// <summary>
    /// Attaches an existing file to the assistant
    /// </summary>
    /// <param name="fileId">ID of the file to attach</param>
    /// <returns>True if successful</returns>
    public async Task<bool> AttachFileToAssistantAsync(string fileId)
    {
        var requestUrl = $"{_apiUrl}/openai/assistants/{_assistantId}/files?api-version={_apiVersion}";
        
        var attachRequest = new
        {
            file_id = fileId
        };

        var content = new StringContent(
            JsonSerializer.Serialize(attachRequest),
            Encoding.UTF8,
            ContentType);

        var response = await _httpClient.PostAsync(requestUrl, content);
        
        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Successfully attached file {fileId} to assistant {_assistantId}");
            return true;
        }
        
        var errorBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Failed to attach file to assistant: {response.StatusCode}. Details: {errorBody}");
        return false;
    }

    public void AddSystemMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("System message cannot be null or empty", nameof(message));
        }
        
        _messages.Add(new ChatMessage(ChatRole.System, message));
        
        // For system messages, we add them as user messages with a special prefix
        AddMessageToThreadAsync(_threadId, "user", $"[SYSTEM MESSAGE]: {message}").GetAwaiter().GetResult();
    }

    public void AddUserMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("User message cannot be null or empty", nameof(message));
        }
        
        _messages.Add(new ChatMessage(ChatRole.User, message));
        
        // Add the message to the thread
        AddMessageToThreadAsync(_threadId, "user", message).GetAwaiter().GetResult();
    }

    private async Task<string> AddMessageToThreadAsync(string threadId, string role, string content)
    {
        var requestUrl = $"{_apiUrl}/openai/threads/{threadId}/messages?api-version={_apiVersion}";
        
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

    // Match the signature of PEChatClient for consistency
    public async Task<string> GetResponseAsync(ChatOptions? options = null)
    {
        try
        {
            // Create a run to generate a response
            var requestUrl = $"{_apiUrl}/openai/threads/{_threadId}/runs?api-version={_apiVersion}";
            
            // Simple run request with only the assistant ID
            var runRequest = new
            {
                assistant_id = _assistantId
            };

            var requestContent = new StringContent(
                JsonSerializer.Serialize(runRequest),
                Encoding.UTF8,
                ContentType);

            var response = await _httpClient.PostAsync(requestUrl, requestContent);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to create run: {response.StatusCode}. Details: {errorBody}");
            }
            
            string runId;
            using (var responseBody = await response.Content.ReadAsStreamAsync())
            using (var document = await JsonDocument.ParseAsync(responseBody))
            {
                runId = document.RootElement.GetProperty("id").GetString() ?? 
                        throw new InvalidOperationException("Failed to get run ID from response");
            }
            
            // Wait for the run to complete with exponential backoff
            var runStatus = await PollRunUntilCompletionAsync(_threadId, runId);
            
            if (runStatus != "completed")
            {
                var errorMessage = $"Run failed with status: {runStatus}";
                throw new Exception(errorMessage);
            }
            
            // Get the assistant's messages
            var messageRequestUrl = $"{_apiUrl}/openai/threads/{_threadId}/messages?order=desc&limit=1&api-version={_apiVersion}";

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
            
            // Save the assistant's message for consistency with PEChatClient
            _messages.Add(new ChatMessage(ChatRole.Assistant, responseText));
            
            return responseText;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting assistant response: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task<string> PollRunUntilCompletionAsync(string threadId, string runId)
    {
        const int MaxRetries = 100;
        const int InitialDelayMs = 1000;
        const int MaxDelayMs = 5000;
        
        int retries = 0;
        int delayMs = InitialDelayMs; 
        string status;
        
        do
        {
            await Task.Delay(delayMs);
            
            // Get run status
            var requestUrl = $"{_apiUrl}/openai/threads/{threadId}/runs/{runId}?api-version={_apiVersion}";
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
            
            // If still in progress, increase delay with exponential backoff (cap at 5 seconds)
            if (status == "queued" || status == "in_progress")
            {
                delayMs = Math.Min(delayMs * 2, MaxDelayMs);
            }
            
        } while (status == "queued" || status == "in_progress");
        
        return status;
    }

    public List<ChatMessage> GetMessages()
    {
        return _messages;
    }
}

/// <summary>
/// Represents file information for files attached to an Assistant
/// </summary>
public class AssistantFile
{
    public string Id { get; set; } = string.Empty;
    public string? Object { get; set; }
    public long CreatedAt { get; set; }
    public string? AssistantId { get; set; }
}