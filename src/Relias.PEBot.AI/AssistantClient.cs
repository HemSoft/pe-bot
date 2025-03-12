namespace Relias.PEBot.AI;

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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
    private readonly List<AIFunction> _functions = [];
    private readonly ConfluenceInfoProvider? _confluenceProvider;

    private const string DefaultModel = "gpt-4o-mini";
    private const string FileSearchTool = "file_search";
    private const string ApiVersionDefault = "2024-05-01-preview";
    private const string ContentType = "application/json";
    private const string AcceptHeader = "application/json";
    private const string ErrorNotFound = "404";
    private const string VectorStoreIdKey = "azureOpenAIVectorStoreId";
    private const string UseEntraIdKey = "useEntraIdAuth";

    public AssistantClient(IConfiguration configuration, string? systemPrompt = null)
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

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
                      throw new ArgumentNullException("azureOpenAIKey", "Azure OpenAI Key cannot be null or empty when not using Entra ID.");
        }

        // Get and validate Azure OpenAI URL
        var rawApiUrl = configuration["azureOpenAIUrl"] ?? 
                  throw new ArgumentNullException("azureOpenAIUrl", "Azure OpenAI Url cannot be null or empty.");
        
        // Ensure URL is properly formatted
        if (!Uri.TryCreate(rawApiUrl, UriKind.Absolute, out var apiUri))
        {
            throw new ArgumentException($"Invalid Azure OpenAI URL format: {rawApiUrl}", "azureOpenAIUrl");
        }
        _apiUrl = apiUri.ToString().TrimEnd('/');
        
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
        var configAssistantId = configuration["azureAssistantId"];
        
        if (string.IsNullOrEmpty(configAssistantId))
        {
            Console.WriteLine("No assistant ID provided in configuration. Creating a new assistant...");
            _assistantId = CreateAssistantAsync(systemPrompt).GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine($"Using existing assistant with ID: {configAssistantId}");
            _assistantId = configAssistantId;
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

        // Initialize Confluence provider if configuration is available
        var confluenceEmail = configuration["Confluence:Email"];
        var confluenceApiToken = configuration["Confluence:APIToken"];
        var confluenceDomain = configuration["Confluence:Domain"];
        
        if (!string.IsNullOrEmpty(confluenceEmail) && 
            !string.IsNullOrEmpty(confluenceApiToken) && 
            !string.IsNullOrEmpty(confluenceDomain))
        {
            // Construct Confluence URL from domain
            var confluenceUrl = $"https://{confluenceDomain}";
            _confluenceProvider = new ConfluenceInfoProvider(confluenceUrl, confluenceApiToken, confluenceEmail);
            
            // Register Confluence functions using AIFunctionFactory
            var searchFunction = AIFunctionFactory.Create(_confluenceProvider.SearchConfluence);
            var pageFunction = AIFunctionFactory.Create(_confluenceProvider.GetPageContent);
            var relatedPagesFunction = AIFunctionFactory.Create(_confluenceProvider.GetRelatedPages);
            var lastModifiedFunction = AIFunctionFactory.Create(_confluenceProvider.GetPageLastModified);
            var contributorsFunction = AIFunctionFactory.Create(_confluenceProvider.GetPageContributors);
            var recentUpdatesFunction = AIFunctionFactory.Create(_confluenceProvider.GetRecentUpdates);
            
            _functions.AddRange(new[] { 
                searchFunction, 
                pageFunction,
                relatedPagesFunction,
                lastModifiedFunction,
                contributorsFunction,
                recentUpdatesFunction
            });
            Console.WriteLine("Confluence functions registered with assistant");
        }
        else
        {
            Console.WriteLine("Confluence configuration not found. Confluence integration disabled.");
        }
    }

    /// <summary>
    /// Registers functions that can be called by the assistant
    /// </summary>
    /// <param name="functions">The functions to register</param>
    public void RegisterFunctions(params AIFunction[] functions)
    {
        // Check for duplicate function names
        foreach (var function in functions)
        {
            if (!_functions.Any(f => f.Name == function.Name))
            {
                _functions.Add(function);
                Console.WriteLine($"Registered function: {function.Name}");
            }
            else
            {
                Console.WriteLine($"Skipping duplicate function registration: {function.Name}");
            }
        }
        Console.WriteLine($"Total registered functions: {_functions.Count}");
    }

    /// <summary>
    /// Process any function calls that the assistant wants to make
    /// </summary>
    /// <param name="runId">The ID of the current run</param>
    /// <returns>A dictionary of tool call IDs and their outputs</returns>
    private async Task<Dictionary<string, string>> ProcessFunctionCallsAsync(string runId)
    {
        var toolOutputs = new Dictionary<string, string>();
        var requestUrl = $"{_apiUrl}/openai/threads/{_threadId}/runs/{runId}?api-version={_apiVersion}";
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
                
                // Find matching registered function
                var registeredFunction = _functions.FirstOrDefault(f => f.Name == functionName);
                if (registeredFunction != null && !string.IsNullOrEmpty(arguments))
                {
                    try
                    {
                        Console.WriteLine($"Executing function {functionName}...");
                        var result = await registeredFunction.InvokeAsync(arguments);
                        Console.WriteLine($"Function {functionName} returned: {result}");
                        
                        toolOutputs[toolCallId] = result;
                        Console.WriteLine($"Added result to tool outputs: {result}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error executing function {functionName}: {ex.Message}");
                        Console.WriteLine($"Stack trace: {ex.StackTrace}");
                        var errorMessage = $"Error executing function {functionName}: {ex.Message}";
                        toolOutputs[toolCallId] = errorMessage;
                    }
                }
                else
                {
                    var errorMessage = registeredFunction == null
                        ? $"Function {functionName} not found in registered functions"
                        : $"Function {functionName} received invalid arguments: {arguments}";
                    
                    Console.WriteLine(errorMessage);
                    toolOutputs[toolCallId] = errorMessage;
                }
            }
        }
        else
        {
            Console.WriteLine("No required_action found in run status");
        }

        Console.WriteLine($"Returning {toolOutputs.Count} tool outputs");
        foreach (var kvp in toolOutputs)
        {
            Console.WriteLine($"Tool {kvp.Key}: {kvp.Value}");
        }
        return toolOutputs;
    }

    /// <summary>
    /// Gets a friendly error message for a specific function when no results are found
    /// </summary>
    private string GetFriendlyErrorMessageForFunction(string functionName, string arguments)
    {
        // Try to extract the query/topic/pageId from the arguments
        string searchTerm = "the requested information";
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.TryGetProperty("query", out var queryElement))
            {
                searchTerm = queryElement.GetString() ?? searchTerm;
            }
            else if (document.RootElement.TryGetProperty("topic", out var topicElement))
            {
                searchTerm = topicElement.GetString() ?? searchTerm;
            }
            else if (document.RootElement.TryGetProperty("pageId", out var pageIdElement))
            {
                searchTerm = $"page ID {pageIdElement.GetString() ?? "provided"}";
            }
        }
        catch (JsonException)
        {
            // If JSON parsing fails, use the generic search term
        }
        
        switch (functionName)
        {
            case "SearchConfluence":
                return $"I searched Confluence for \"{searchTerm}\" but couldn't find any relevant documentation. " +
                       "Please try another search term or check if this information exists in Confluence.";
                
            case "GetRelatedPages":
                return $"I couldn't find any Confluence pages related to \"{searchTerm}\". " +
                       "The information may not be documented or might be under a different topic name.";
                
            case "GetPageContent":
                return $"I couldn't retrieve the content for {searchTerm}. " +
                       "The page might not exist or you may not have access to it.";
                
            case "GetPageLastModified":
                return $"I couldn't determine when {searchTerm} was last modified. " +
                       "The page might not exist or you may not have access to it.";
                
            case "GetPageContributors":
                return $"I couldn't retrieve the contributors for {searchTerm}. " +
                       "The page might not exist or you may not have access to it.";
                
            case "GetRecentUpdates":
                return $"I couldn't find any recent updates related to \"{searchTerm}\" in Confluence. " +
                       "There may not have been any recent activity on this topic.";
                
            default:
                return $"I couldn't find any information about \"{searchTerm}\" " +
                       "using the requested function. Please try a different query.";
        }
    }

    public async Task<string> GetResponseAsync(ChatOptions? options = null)
    {
        try
        {
            // Create a run to generate a response
            var requestUrl = $"{_apiUrl}/openai/threads/{_threadId}/runs?api-version={_apiVersion}";
            
            // Combine function tools with file_search tool
            var tools = new List<object>();

            // Add registered functions as tools
            foreach (var function in _functions)
            {
                tools.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name = function.Name,
                        description = GetDescriptionFromFunction(function),
                        parameters = function.Parameters
                    }
                });
            }

            // Add file_search tool
            tools.Add(new { type = FileSearchTool });

            var runRequest = new
            {
                assistant_id = _assistantId,
                tools = tools.ToArray()
            };

            var requestContent = new StringContent(
                JsonSerializer.Serialize(runRequest),
                Encoding.UTF8,
                ContentType);

            Console.WriteLine($"Creating run with {tools.Count} tools...");

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
            
            // Handle all potential run statuses appropriately
            while (runStatus == "requires_action")
            {
                Console.WriteLine("Run requires action - processing tool calls");
                
                // Process function calls
                var toolOutputs = await ProcessFunctionCallsAsync(runId);
                
                if (!toolOutputs.Any())
                {
                    Console.WriteLine("No tool outputs to submit, submitting empty response to continue");
                    
                    // Get required_action details to understand what tools are needed
                    var requiredActionUrl = $"{_apiUrl}/openai/threads/{_threadId}/runs/{runId}?api-version={_apiVersion}";
                    var actionResponse = await _httpClient.GetAsync(requiredActionUrl);
                    
                    if (actionResponse.IsSuccessStatusCode)
                    {
                        using var actionBody = await actionResponse.Content.ReadAsStreamAsync();
                        using var actionDoc = await JsonDocument.ParseAsync(actionBody);
                        
                        if (actionDoc.RootElement.TryGetProperty("required_action", out var requiredAction) &&
                            requiredAction.TryGetProperty("submit_tool_outputs", out var submitToolOutputs) &&
                            submitToolOutputs.TryGetProperty("tool_calls", out var toolCalls))
                        {
                            // Create empty responses for each required tool call
                            foreach (var toolCall in toolCalls.EnumerateArray())
                            {
                                if (toolCall.TryGetProperty("id", out var toolCallId) && 
                                    toolCall.TryGetProperty("function", out var function))
                                {
                                    var id = toolCallId.GetString();
                                    var functionName = function.GetProperty("name").GetString() ?? "unknown";
                                    var argumentsJson = "{}";
                                    
                                    if (function.TryGetProperty("arguments", out var arguments))
                                    {
                                        argumentsJson = arguments.GetString() ?? "{}";
                                    }
                                    
                                    if (!string.IsNullOrEmpty(id))
                                    {
                                        Console.WriteLine($"Creating empty response for tool call: {id} ({functionName})");
                                        
                                        // Handle specific function types with better fallback responses
                                        if (functionName == "SearchConfluence")
                                        {
                                            string searchTerm = "the requested information";
                                            try
                                            {
                                                using var argsDoc = JsonDocument.Parse(argumentsJson);
                                                if (argsDoc.RootElement.TryGetProperty("query", out var queryElement))
                                                {
                                                    searchTerm = queryElement.GetString() ?? searchTerm;
                                                }
                                            }
                                            catch { /* Use the default search term */ }
                                            
                                            var message = $"I searched Confluence for \"{searchTerm}\" but couldn't find any relevant documentation. " +
                                                         "Please try another search term or check if this information exists in Confluence.";
                                            toolOutputs[id] = message;
                                        }
                                        else if (functionName == "GetRelatedPages") 
                                        {
                                            string topic = "the requested topic";
                                            try
                                            {
                                                using var argsDoc = JsonDocument.Parse(argumentsJson);
                                                if (argsDoc.RootElement.TryGetProperty("topic", out var topicElement))
                                                {
                                                    topic = topicElement.GetString() ?? topic;
                                                }
                                            }
                                            catch { /* Use the default topic */ }
                                            
                                            var message = $"I couldn't find any Confluence pages related to \"{topic}\". " +
                                                        "The information may not be documented or might be under a different topic name.";
                                            toolOutputs[id] = message;
                                        }
                                        else
                                        {
                                            // Generic fallback for other functions
                                            toolOutputs[id] = $"Function {functionName} could not be executed: not implemented or not available";
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    // If we still have no outputs, we need to respond with at least something to not get stuck
                    if (!toolOutputs.Any())
                    {
                        Console.WriteLine("Warning: Could not determine required tool calls, continuing without response");
                        // Submit a dummy response to unblock the run
                        await AddMessageToThreadAsync(_threadId, "tool", 
                            "No tool outputs could be generated. Please continue without tool results.");
                    }
                    else
                    {
                        // Submit the empty responses we created
                        await SubmitToolOutputsAsync(runId, toolOutputs);
                    }
                }
                else
                {
                    // Submit tool outputs
                    await SubmitToolOutputsAsync(runId, toolOutputs);
                }
                
                // Continue waiting for completion
                runStatus = await PollRunUntilCompletionAsync(_threadId, runId);
            }
            
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

    private string GetDescriptionFromFunction(AIFunction function)
    {
        // Get the method info from the function
        var methodInfo = function.GetType().GetMethod("InvokeAsync");
        if (methodInfo == null) return string.Empty;

        // Get the description attribute from the method being called
        var attr = methodInfo.GetCustomAttributes(typeof(DescriptionAttribute), true)
                           .FirstOrDefault() as DescriptionAttribute;
        
        return attr?.Description ?? function.Description;
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

    private async Task UpdateAssistantToolsAsync()
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
                    Object = fileItem.GetProperty("object").GetString() ?? string.Empty,
                    CreatedAt = fileItem.GetProperty("created_at").GetInt64(),
                    AssistantId = fileItem.GetProperty("assistant_id").GetString() ?? string.Empty
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
            
        } while (status == "queued" || status == "in_progress"); // Stop polling when we hit requires_action
        
        return status;
    }

    /// <summary>
    /// Submit tool outputs to the run
    /// </summary>
    /// <param name="runId">ID of the current run</param>
    /// <param name="toolOutputs">Dictionary of tool call IDs and their outputs</param>
    private async Task SubmitToolOutputsAsync(string runId, Dictionary<string, string> toolOutputs)
    {
        var requestUrl = $"{_apiUrl}/openai/threads/{_threadId}/runs/{runId}/submit_tool_outputs?api-version={_apiVersion}";
        
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
    public string Object { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
    public string AssistantId { get; set; } = string.Empty;
}