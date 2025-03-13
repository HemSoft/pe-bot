namespace Relias.PEBot.AI;

using Azure;
using Azure.AI.OpenAI.Assistants;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Text.Json;

public abstract class AssistantClient
{
    protected readonly List<ChatMessage> _messageHistory = [];
    protected readonly List<AIFunction> _functions = [];
    protected readonly ConfluenceInfoProvider? _confluenceProvider;

    protected const string DefaultDeploymentName = "assistants";
    protected const string FileSearchTool = "file_search";
    protected const string ApiVersionDefault = "2024-05-01-preview";
    protected const string ContentType = "application/json";
    protected const string AcceptHeader = "application/json";
    protected const string VectorStoreIdKey = "azureOpenAIVectorStoreId";
    protected const string UseEntraIdKey = "useEntraIdAuth";
    protected const string AssistantIdKey = "azureOpenAIAssistantId";

    protected AssistantClient(IConfiguration configuration, string? systemPrompt = null)
    {
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

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            _messageHistory.Add(new ChatMessage(ChatRole.System, systemPrompt));
        }
    }

    public virtual void RegisterFunctions(params AIFunction[] functions)
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

    protected string GetDescriptionFromFunction(AIFunction function)
    {
        var methodInfo = function.GetType().GetMethod("InvokeAsync");
        if (methodInfo == null) return string.Empty;

        var attr = methodInfo.GetCustomAttributes(typeof(DescriptionAttribute), true)
                           .FirstOrDefault() as DescriptionAttribute;
        
        return attr?.Description ?? function.Description;
    }

    public virtual async Task<string> GetResponseAsync(ChatOptions? options = null)
    {
        await Task.CompletedTask; // Add await to satisfy compiler
        throw new NotImplementedException("GetResponseAsync must be implemented by derived classes");
    }

    public virtual void AddUserMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("User message cannot be null or empty", nameof(message));
        }
        
        _messageHistory.Add(new ChatMessage(ChatRole.User, message));
    }

    public virtual void AddSystemMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("System message cannot be null or empty", nameof(message));
        }
        
        _messageHistory.Add(new ChatMessage(ChatRole.System, message));
    }

    public List<ChatMessage> GetMessages()
    {
        return _messageHistory;
    }

    public virtual void RegisterFunction(AIFunction function)
    {
        _functions.Add(function);
    }
}

public class AssistantClientSdk : AssistantClient
{
    private readonly AssistantsClient _client;
    private Assistant? _assistant;
    private AssistantThread? _thread;
    private readonly string _vectorStoreId;
    private readonly IConfiguration _configuration;

    public AssistantClientSdk(IConfiguration configuration, string? systemPrompt = null)
        : base(configuration, systemPrompt)
    {
        _configuration = configuration;
        var endpoint = configuration["azureOpenAIUrl"] ?? 
            throw new ArgumentNullException("azureOpenAIUrl", "Azure OpenAI Url cannot be null or empty.");

        var useEntraId = string.Equals(configuration["useEntraIdAuth"], "true", StringComparison.OrdinalIgnoreCase);
        
        if (useEntraId)
        {
            var credential = new DefaultAzureCredential();
            _client = new AssistantsClient(new Uri(endpoint), credential);
        }
        else
        {
            var apiKey = configuration["azureOpenAIKey"] ?? 
                throw new ArgumentNullException("azureOpenAIKey", "Azure OpenAI Key cannot be null or empty.");
            _client = new AssistantsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        }

        _vectorStoreId = configuration["azureOpenAIVectorStoreId"] ?? string.Empty;

        // Get or create assistant using Task.Run since we can't make the constructor async
        try
        {
            var assistantId = configuration[AssistantIdKey] ?? 
                throw new ArgumentNullException(AssistantIdKey, "Azure OpenAI Assistant ID cannot be null or empty.");

            // Try to get existing assistant using Task.Run to run async code
            var assistantTask = Task.Run(async () =>
            {
                var assistantResponse = await _client.GetAssistantAsync(assistantId);
                var assistant = assistantResponse.Value;

                // Update assistant instructions if system prompt provided
                if (!string.IsNullOrEmpty(systemPrompt))
                {
                    await _client.UpdateAssistantAsync(assistant.Id, new UpdateAssistantOptions 
                    { 
                        Instructions = systemPrompt 
                    });
                }
                
                return assistant;
            });

            // Wait for the async initialization to complete
            _assistant = assistantTask.GetAwaiter().GetResult();
            Console.WriteLine($"Using existing assistant with ID {_assistant?.Id}");
            
            // We'll create a new thread for each conversation
            CreateNewThread();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize assistant: {ex.Message}", ex);
        }
    }
    
    private void CreateNewThread()
    {
        try
        {
            var threadResponse = _client.CreateThreadAsync().GetAwaiter().GetResult();
            _thread = threadResponse.Value;
            Console.WriteLine($"Created new thread with ID {_thread?.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating thread: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    public override async Task<string> GetResponseAsync(ChatOptions? options = null)
    {
        if (_assistant == null || _thread == null)
        {
            throw new InvalidOperationException("Assistant or thread not properly initialized");
        }

        try
        {
            // Create run
            Console.WriteLine($"Creating run for thread {_thread.Id} with assistant {_assistant.Id}");
            var runResponse = await _client.CreateRunAsync(
                _thread.Id,
                new CreateRunOptions(_assistant.Id));

            var run = runResponse.Value;
            Console.WriteLine($"Run created with ID: {run.Id}, initial status: {run.Status}");

            // Poll until complete or requires action
            bool requiresAction = false;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                runResponse = await _client.GetRunAsync(_thread.Id, run.Id);
                Console.WriteLine($"Run status: {runResponse.Value.Status}");
                
                requiresAction = runResponse.Value.Status == RunStatus.RequiresAction;
                if (requiresAction)
                {
                    Console.WriteLine("Run requires action - attempting to handle tool calls directly");
                    
                    // Get the run details as JSON to extract tool calls
                    var runDetailsResponse = await _client.GetRunAsync(_thread.Id, run.Id);
                    var runDetails = runDetailsResponse.Value;
                    
                    // Convert the run to JSON to extract the required_action
                    var runJson = System.Text.Json.JsonSerializer.Serialize(runDetails);
                    Console.WriteLine($"Run JSON: {runJson}");
                    
                    // Parse the JSON to extract tool calls
                    using var jsonDoc = System.Text.Json.JsonDocument.Parse(runJson);
                    var root = jsonDoc.RootElement;
                    
                    if (root.TryGetProperty("required_action", out var requiredAction))
                    {
                        Console.WriteLine($"Found required_action: {requiredAction}");
                        
                        if (requiredAction.TryGetProperty("submit_tool_outputs", out var submitToolOutputs))
                        {
                            if (submitToolOutputs.TryGetProperty("tool_calls", out var toolCalls))
                            {
                                Console.WriteLine($"Found tool_calls: {toolCalls}");
                                
                                // Process each tool call
                                var toolOutputs = new List<(string id, string output)>();
                                
                                foreach (var toolCall in toolCalls.EnumerateArray())
                                {
                                    if (toolCall.TryGetProperty("id", out var idElement) && 
                                        toolCall.TryGetProperty("function", out var functionElement))
                                    {
                                        var id = idElement.GetString();
                                        var name = functionElement.GetProperty("name").GetString();
                                        var arguments = functionElement.GetProperty("arguments").GetString();
                                        
                                        Console.WriteLine($"Tool call: id={id}, function={name}, arguments={arguments}");
                                        
                                        // Find the matching function
                                        var function = _functions.FirstOrDefault(f => f.Name == name);
                                        if (function != null)
                                        {
                                            try
                                            {
                                                var result = await function.InvokeAsync(arguments ?? "{}");
                                                Console.WriteLine($"Function result: {result}");
                                                toolOutputs.Add((id ?? "", result ?? ""));
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine($"Error invoking function: {ex.Message}");
                                                toolOutputs.Add((id ?? "", $"Error: {ex.Message}"));
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Function not found: {name}");
                                            toolOutputs.Add((id ?? "", $"Error: Function {name} not found"));
                                        }
                                    }
                                }
                                
                                // Submit tool outputs
                                if (toolOutputs.Any())
                                {
                                    Console.WriteLine($"Submitting {toolOutputs.Count} tool outputs");
                                    
                                    // Create the request body
                                    var requestBody = new
                                    {
                                        tool_outputs = toolOutputs.Select(t => new
                                        {
                                            tool_call_id = t.id,
                                            output = t.output
                                        }).ToArray()
                                    };
                                    
                                    var jsonContent = System.Text.Json.JsonSerializer.Serialize(requestBody);
                                    Console.WriteLine($"Request body: {jsonContent}");
                                    
                                    // Submit tool outputs using HttpClient
                                    using var httpClient = new HttpClient();
                                    var apiKey = _configuration["azureOpenAIKey"];
                                    var apiUrl = _configuration["azureOpenAIUrl"];
                                    var apiVersion = _configuration["azureOpenAIApiVersion"] ?? "2024-02-15-preview";
                                    
                                    httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
                                    httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
                                    
                                    var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
                                    var url = $"{apiUrl}/openai/deployments/{DefaultDeploymentName}/threads/{_thread.Id}/runs/{run.Id}/submit_tool_outputs?api-version={apiVersion}";
                                    
                                    Console.WriteLine($"Submitting tool outputs to URL: {url}");
                                    var response = await httpClient.PostAsync(url, content);
                                    
                                    var responseContent = await response.Content.ReadAsStringAsync();
                                    Console.WriteLine($"Response: {response.StatusCode}, Content: {responseContent}");
                                    
                                    if (response.IsSuccessStatusCode)
                                    {
                                        Console.WriteLine("Tool outputs submitted successfully");
                                        
                                        // Continue polling until complete
                                        do
                                        {
                                            await Task.Delay(TimeSpan.FromMilliseconds(500));
                                            runResponse = await _client.GetRunAsync(_thread.Id, run.Id);
                                            Console.WriteLine($"Run status after tool outputs: {runResponse.Value.Status}");
                                        } while (runResponse.Value.Status == RunStatus.Queued || runResponse.Value.Status == RunStatus.InProgress);
                                        
                                        // Break out of the main polling loop
                                        break;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"Failed to submit tool outputs: {response.StatusCode}");
                                        throw new InvalidOperationException($"Failed to submit tool outputs: {response.StatusCode}");
                                    }
                                }
                            }
                        }
                    }
                }
            } while (runResponse.Value.Status == RunStatus.Queued || runResponse.Value.Status == RunStatus.InProgress || requiresAction);

            // Get messages after run completes
            if (runResponse.Value.Status == RunStatus.Completed)
            {
                Console.WriteLine("Run completed successfully, retrieving messages");
                var messagesResponse = await _client.GetMessagesAsync(_thread.Id);
                var messages = messagesResponse.Value.Data;
                Console.WriteLine($"Retrieved {messages.Count} messages");

                // Take most recent assistant message
                var assistantMessage = messages.FirstOrDefault(m => m.Role == MessageRole.Assistant);
                if (assistantMessage != null)
                {
                    var contentItems = assistantMessage.ContentItems.OfType<MessageTextContent>().ToList();
                    Console.WriteLine($"Found assistant message with {contentItems.Count} text content items");
                    
                    var responseText = string.Join("\n", contentItems.Select(c => c.Text));
                    Console.WriteLine($"Response text length: {responseText.Length} characters");

                    // Update message history
                    AddToMessageHistory(ChatRole.Assistant, responseText);
                    
                    // Create a new thread for the next conversation
                    CreateNewThread();
                    
                    return responseText;
                }
                else
                {
                    Console.WriteLine("No assistant message found in the response");
                }
            }
            else if (runResponse.Value.Status == RunStatus.Failed)
            {
                Console.WriteLine($"Run failed with status: {runResponse.Value.Status}");
                if (runResponse.Value.LastError != null)
                {
                    Console.WriteLine($"Error: {runResponse.Value.LastError.Code}: {runResponse.Value.LastError.Message}");
                }
                
                // Create a new thread for the next conversation
                CreateNewThread();
            }

            throw new InvalidOperationException(
                $"Run failed with status {runResponse.Value.Status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting assistant response: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Create a new thread for the next conversation
            CreateNewThread();
            
            // Provide a fallback response
            if (ex.Message.Contains("Run failed with status failed"))
            {
                Console.WriteLine("Run failed, attempting to provide fallback response");
                
                // Check if we have any functions registered for Confluence
                var confluenceFunction = _functions.FirstOrDefault(f => f.Name.Contains("Confluence"));
                if (confluenceFunction != null)
                {
                    Console.WriteLine($"Found Confluence function: {confluenceFunction.Name}");
                    try
                    {
                        // Extract the search term from the last user message
                        var lastUserMessage = _messageHistory.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
                        Console.WriteLine($"Last user message: {lastUserMessage}");
                        
                        if (!string.IsNullOrEmpty(lastUserMessage))
                        {
                            Console.WriteLine($"Last user message: '{lastUserMessage}'");
                            
                            // Check if it's a request for Confluence documents
                            bool isConfluenceRequest = lastUserMessage.Contains("confluence", StringComparison.OrdinalIgnoreCase) || 
                                                      lastUserMessage.Contains("document", StringComparison.OrdinalIgnoreCase) ||
                                                      lastUserMessage.Contains("docs", StringComparison.OrdinalIgnoreCase) ||
                                                      lastUserMessage.Contains("search for", StringComparison.OrdinalIgnoreCase) ||
                                                      lastUserMessage.Contains("onboarding", StringComparison.OrdinalIgnoreCase) ||
                                                      lastUserMessage.StartsWith("search", StringComparison.OrdinalIgnoreCase);
                            
                            Console.WriteLine($"Is Confluence request: {isConfluenceRequest}");
                            
                            if (isConfluenceRequest)
                            {
                                // Extract search term
                                string searchTerm = ExtractSearchTerm(lastUserMessage);
                                Console.WriteLine($"Extracted search term: {searchTerm}");
                                
                                if (!string.IsNullOrEmpty(searchTerm))
                                {
                                    // Check if the user is asking for latest/recent documents
                                    bool isRequestingLatest = lastUserMessage.Contains("latest", StringComparison.OrdinalIgnoreCase) ||
                                                             lastUserMessage.Contains("recent", StringComparison.OrdinalIgnoreCase) ||
                                                             lastUserMessage.Contains("newest", StringComparison.OrdinalIgnoreCase) ||
                                                             lastUserMessage.Contains("updated", StringComparison.OrdinalIgnoreCase);
                                    
                                    Console.WriteLine($"Is requesting latest documents: {isRequestingLatest}");
                                    
                                    // Find the appropriate function based on the request
                                    var functionToUse = isRequestingLatest 
                                        ? _functions.FirstOrDefault(f => f.Name.Contains("GetRecentUpdates")) 
                                        : confluenceFunction;
                                    
                                    if (functionToUse == null)
                                    {
                                        functionToUse = confluenceFunction; // Fallback to regular search if GetRecentUpdates not found
                                    }
                                    
                                    Console.WriteLine($"Using function: {functionToUse.Name}");
                                    
                                    // If the user is asking for latest documents without specifying a topic,
                                    // use a more general search term
                                    if (isRequestingLatest && (searchTerm.Contains("confluence") || 
                                                              searchTerm.Contains("document") || 
                                                              searchTerm.Contains("doc") ||
                                                              string.IsNullOrWhiteSpace(searchTerm)))
                                    {
                                        searchTerm = ""; // Empty search term will return all recent documents
                                        Console.WriteLine("Using empty search term to get all recent documents");
                                    }
                                    
                                    // Invoke the Confluence function
                                    Console.WriteLine($"Invoking Confluence function with search term: {searchTerm}");
                                    string functionResult = await functionToUse.InvokeAsync($"{{\"query\": \"{searchTerm}\", \"topic\": \"{searchTerm}\"}}");
                                    Console.WriteLine($"Confluence function result length: {functionResult?.Length ?? 0}");
                                    Console.WriteLine($"Confluence function result is null: {functionResult == null}");
                                    Console.WriteLine($"Confluence function result is empty: {string.IsNullOrEmpty(functionResult)}");
                                    
                                    if (!string.IsNullOrEmpty(functionResult))
                                    {
                                        // Check if the function returned no results
                                        bool containsNoResults = functionResult.Contains("couldn't find any results") || 
                                                               functionResult.Contains("no results found");
                                        Console.WriteLine($"Function result contains 'no results' message: {containsNoResults}");
                                        
                                        if (containsNoResults)
                                        {
                                            // Format a proper response with the expected structure
                                            string formattedResponse = FormatEmptyConfluenceResponse(searchTerm);
                                            Console.WriteLine("Successfully retrieved Confluence search results");
                                            
                                            // Update message history
                                            AddToMessageHistory(ChatRole.Assistant, formattedResponse);
                                            
                                            return formattedResponse;
                                        }
                                        
                                        Console.WriteLine("Successfully retrieved Confluence search results");
                                        
                                        // Check if we need to limit the number of results
                                        int requestedResultCount = ExtractRequestedResultCount(lastUserMessage);
                                        if (requestedResultCount > 0)
                                        {
                                            functionResult = LimitConfluenceResults(functionResult, requestedResultCount);
                                        }
                                        
                                        // Update message history
                                        AddToMessageHistory(ChatRole.Assistant, functionResult);
                                        
                                        return functionResult;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("Failed to extract search term from user message");
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("No user message found in message history");
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        Console.WriteLine($"Error in fallback response: {fallbackEx.Message}");
                        Console.WriteLine($"Stack trace: {fallbackEx.StackTrace}");
                    }
                }
                else
                {
                    Console.WriteLine("No Confluence function found in registered functions");
                    Console.WriteLine($"Registered functions: {string.Join(", ", _functions.Select(f => f.Name))}");
                }
                
                // Generic fallback if specific handling fails
                string fallbackResponse = "I apologize, but I encountered an issue while processing your request. " +
                    "I'm still learning and improving. Could you please try rephrasing your question or asking something else?";
                
                // Update message history
                AddToMessageHistory(ChatRole.Assistant, fallbackResponse);
                
                return fallbackResponse;
            }
            
            throw;
        }
    }

    private string FormatEmptyConfluenceResponse(string searchTerm)
    {
        // Format a response that matches the expected structure for Confluence results
        return $@"I searched for Confluence documents related to ""{searchTerm}"" but couldn't find any matching results.

Here's what you would typically see if results were found:

Example Result:
Space: [Space Name]
Last Updated: [Date]
Link: [URL]

Please try another search term or check if the information exists in Confluence.";
    }

    private string ExtractSearchTerm(string userMessage)
    {
        // Simple extraction logic - could be improved with more sophisticated NLP
        string searchTerm = userMessage.ToLower();
        
        // Remove common prefixes
        string[] prefixes = {
            "search confluence for ", 
            "search for ", 
            "find ", 
            "show me ", 
            "get ", 
            "retrieve ",
            "what are the ",
            "what is "
        };
        
        foreach (var prefix in prefixes)
        {
            if (searchTerm.Contains(prefix))
            {
                searchTerm = searchTerm.Substring(searchTerm.IndexOf(prefix) + prefix.Length);
                break;
            }
        }
        
        // Remove common suffixes
        string[] suffixes = {
            " in confluence",
            " from confluence",
            " documents",
            " docs",
            " pages",
            "?"
        };
        
        foreach (var suffix in suffixes)
        {
            if (searchTerm.EndsWith(suffix))
            {
                searchTerm = searchTerm.Substring(0, searchTerm.Length - suffix.Length);
                break;
            }
        }
        
        return searchTerm.Trim();
    }

    private int ExtractRequestedResultCount(string userMessage)
    {
        // Check for numeric patterns like "5 latest" or "show me 5" or "top 5"
        string lowerMessage = userMessage.ToLower();
        
        // Look for patterns like "5 latest", "show me 5", "top 5", etc.
        string[] patterns = { @"(\d+)\s+latest", @"show\s+me\s+(\d+)", @"top\s+(\d+)", @"(\d+)\s+most\s+recent" };
        
        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(lowerMessage, pattern);
            if (match.Success && match.Groups.Count > 1)
            {
                if (int.TryParse(match.Groups[1].Value, out int count))
                {
                    return count;
                }
            }
        }
        
        return 0; // Default: no limit
    }
    
    private string LimitConfluenceResults(string results, int limit)
    {
        // Split the results into lines
        var lines = results.Split('\n');
        
        // Check if we have a header line
        int startIndex = 0;
        if (lines.Length > 0 && lines[0].Contains("Found") && lines[0].Contains("relevant pages"))
        {
            startIndex = 1;
        }
        
        // Count the number of results (each result starts with "📄")
        int resultCount = 0;
        List<string> limitedResults = new List<string>();
        
        // Add the header if it exists
        if (startIndex > 0)
        {
            // Update the header to reflect the limited number of results
            string originalCount = System.Text.RegularExpressions.Regex.Match(lines[0], @"Found (\d+)").Groups[1].Value;
            limitedResults.Add(lines[0].Replace($"Found {originalCount}", $"Found {Math.Min(limit, int.Parse(originalCount))}"));
        }
        
        // Process the results
        for (int i = startIndex; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("📄"))
            {
                resultCount++;
                if (resultCount > limit)
                {
                    break;
                }
            }
            
            if (resultCount <= limit)
            {
                limitedResults.Add(lines[i]);
            }
        }
        
        return string.Join("\n", limitedResults);
    }

    public override void AddUserMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("User message cannot be null or empty", nameof(message));
        }

        try
        {
            // Create a new thread if one doesn't exist
            if (_thread == null)
            {
                CreateNewThread();
            }

            // Ensure thread is created
            if (_thread == null)
            {
                throw new InvalidOperationException("Failed to create thread");
            }

            // Add to thread
            Console.WriteLine($"Adding user message to thread {_thread.Id}");
            _client.CreateMessage(
                _thread.Id,
                MessageRole.User,
                message);

            // Update message history
            AddToMessageHistory(ChatRole.User, message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding user message: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Try to create a new thread and retry
            try
            {
                CreateNewThread();
                
                // Ensure thread is created
                if (_thread == null)
                {
                    throw new InvalidOperationException("Failed to create thread on retry");
                }
                
                // Add to new thread
                _client.CreateMessage(
                    _thread.Id,
                    MessageRole.User,
                    message);
                
                // Update message history
                AddToMessageHistory(ChatRole.User, message);
                
                Console.WriteLine("Successfully added message to new thread");
            }
            catch (Exception retryEx)
            {
                Console.WriteLine($"Error retrying to add user message: {retryEx.Message}");
                Console.WriteLine($"Stack trace: {retryEx.StackTrace}");
                throw;
            }
        }
    }

    public override void AddSystemMessage(string message)
    {
        if (_assistant == null)
        {
            throw new InvalidOperationException("Assistant not properly initialized");
        }

        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("System message cannot be null or empty", nameof(message));
        }

        // Update message history only since Azure SDK doesn't support system messages directly  
        AddToMessageHistory(ChatRole.System, message);
        
        // For Azure OpenAI, we'll handle system messages by updating the assistant's instructions
        _client.UpdateAssistant(_assistant.Id, new UpdateAssistantOptions 
        {
            Instructions = message
        });
    }

    private void AddToMessageHistory(ChatRole role, string content)
    {
        _messageHistory.Add(new ChatMessage(role, content));
    }
}