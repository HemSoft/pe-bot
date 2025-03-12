namespace Relias.PEBot.AI;

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private readonly IAssistantManager _assistantManager;
    private readonly IAssistantRunManager _runManager;

    private const string FileSearchTool = "file_search";
    private const string ApiVersionDefault = "2024-05-01-preview";
    private const string ContentType = "application/json";
    private const string AcceptHeader = "application/json";
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
            var credential = new DefaultAzureCredential();
            _accessToken = credential.GetToken(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), 
                default);
            _apiKey = string.Empty;
        }
        else
        {
            _apiKey = configuration["azureOpenAIKey"] ?? 
                      throw new ArgumentNullException("azureOpenAIKey", "Azure OpenAI Key cannot be null or empty when not using Entra ID.");
        }

        var rawApiUrl = configuration["azureOpenAIUrl"] ?? 
                  throw new ArgumentNullException("azureOpenAIUrl", "Azure OpenAI Url cannot be null or empty.");
        
        if (!Uri.TryCreate(rawApiUrl, UriKind.Absolute, out var apiUri))
        {
            throw new ArgumentException($"Invalid Azure OpenAI URL format: {rawApiUrl}", "azureOpenAIUrl");
        }
        _apiUrl = apiUri.ToString().TrimEnd('/');
        
        _apiVersion = configuration["azureOpenAIApiVersion"] ?? ApiVersionDefault;
        _vectorStoreId = configuration[VectorStoreIdKey];
        _assistantTools = [FileSearchTool];
        
        if (_useEntraId)
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken?.Token}");
        }
        else
        {
            _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        }
        _httpClient.DefaultRequestHeaders.Add("Accept", AcceptHeader);

        var configAssistantId = configuration["azureAssistantId"];
        
        if (string.IsNullOrEmpty(configAssistantId))
        {
            Console.WriteLine("No assistant ID provided in configuration. Creating a new assistant...");
            _assistantManager = new AssistantManager(_httpClient, _apiUrl, _apiVersion, string.Empty, _vectorStoreId);
            _assistantId = _assistantManager.CreateAssistantAsync(systemPrompt).GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine($"Using existing assistant with ID: {configAssistantId}");
            _assistantId = configAssistantId;
            _assistantManager = new AssistantManager(_httpClient, _apiUrl, _apiVersion, _assistantId, _vectorStoreId);
            
            var assistantExists = _assistantManager.VerifyAssistantAsync().GetAwaiter().GetResult();
            if (!assistantExists)
            {
                Console.WriteLine($"Assistant with ID {_assistantId} not found or inaccessible. Creating a new assistant...");
                _assistantId = _assistantManager.CreateAssistantAsync(systemPrompt).GetAwaiter().GetResult();
            }
        }
        
        _threadId = _assistantManager.CreateThreadAsync().GetAwaiter().GetResult();
        _runManager = new AssistantRunManager(_httpClient, _apiUrl, _apiVersion, _threadId, _functions);

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            _messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        }
        
        Console.WriteLine($"AssistantClient initialized with Assistant ID: {_assistantId} and Thread ID: {_threadId}");

        var confluenceEmail = configuration["Confluence:Email"];
        var confluenceApiToken = configuration["Confluence:APIToken"];
        var confluenceDomain = configuration["Confluence:Domain"];
        
        if (!string.IsNullOrEmpty(confluenceEmail) && 
            !string.IsNullOrEmpty(confluenceApiToken) && 
            !string.IsNullOrEmpty(confluenceDomain))
        {
            var confluenceUrl = $"https://{confluenceDomain}";
            _confluenceProvider = new ConfluenceInfoProvider(confluenceUrl, confluenceApiToken, confluenceEmail);
            
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

    public void RegisterFunctions(params AIFunction[] functions)
    {
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

    public async Task<string> GetResponseAsync(ChatOptions? options = null)
    {
        try
        {
            var requestUrl = $"{_apiUrl}/openai/threads/{_threadId}/runs?api-version={_apiVersion}";
            var tools = new List<object>();

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
            
            var runStatus = await _runManager.PollRunUntilCompletionAsync(runId);
            
            while (runStatus == "requires_action")
            {
                Console.WriteLine("Run requires action - processing tool calls");
                var toolOutputs = await _runManager.ProcessFunctionCallsAsync(runId);
                
                if (!toolOutputs.Any())
                {
                    Console.WriteLine("No tool outputs to submit, submitting empty response to continue");
                    await _runManager.AddMessageToThreadAsync("tool", 
                        "No tool outputs could be generated. Please continue without tool results.");
                }
                else
                {
                    await _runManager.SubmitToolOutputsAsync(runId, toolOutputs);
                }
                
                runStatus = await _runManager.PollRunUntilCompletionAsync(runId);
            }
            
            if (runStatus != "completed")
            {
                throw new Exception($"Run failed with status: {runStatus}");
            }

            var responseText = await _runManager.GetLatestAssistantMessageAsync();
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
        var methodInfo = function.GetType().GetMethod("InvokeAsync");
        if (methodInfo == null) return string.Empty;

        var attr = methodInfo.GetCustomAttributes(typeof(DescriptionAttribute), true)
                           .FirstOrDefault() as DescriptionAttribute;
        
        return attr?.Description ?? function.Description;
    }

    public Task<IList<AssistantFile>> GetAssistantFilesAsync() => 
        _assistantManager.GetAssistantFilesAsync();

    public Task<IList<string>> GetAssistantVectorStoresAsync() => 
        _assistantManager.GetAssistantVectorStoresAsync();

    public void AddSystemMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("System message cannot be null or empty", nameof(message));
        }
        
        _messages.Add(new ChatMessage(ChatRole.System, message));
        _runManager.AddMessageToThreadAsync("user", $"[SYSTEM MESSAGE]: {message}")
            .GetAwaiter()
            .GetResult();
    }

    public void AddUserMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("User message cannot be null or empty", nameof(message));
        }
        
        _messages.Add(new ChatMessage(ChatRole.User, message));
        _runManager.AddMessageToThreadAsync("user", message)
            .GetAwaiter()
            .GetResult();
    }

    public List<ChatMessage> GetMessages()
    {
        return _messages;
    }
}