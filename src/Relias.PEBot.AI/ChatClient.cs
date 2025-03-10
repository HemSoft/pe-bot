namespace Relias.PEBot.AI;

using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public class PEChatClient
{
    private readonly OpenAIClient _client;
    private readonly string _modelName;
    private readonly List<PEChatMessage> _messages = [];

    public PEChatClient(IConfiguration configuration, string? systemPrompt = null)
    {
        var apiKey = configuration["azureOpenAIKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new ArgumentNullException(nameof(apiKey), "Azure OpenAI Key cannot be null or empty.");
        }
        var apiKeyCredentials = new AzureKeyCredential(apiKey);

        var apiUrl = configuration["azureOpenAIUrl"];
        if (string.IsNullOrEmpty(apiUrl))
        {
            throw new ArgumentNullException(nameof(apiUrl), "Azure OpenAI Url cannot be null or empty.");
        }

        _client = new OpenAIClient(new Uri(apiUrl), apiKeyCredentials);
        _modelName = "gpt-4o-mini"; // Default model
        
        // Add system prompt if provided
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            _messages.Add(new PEChatMessage(Microsoft.Extensions.AI.ChatRole.System, systemPrompt));
        }
    }

    public void AddSystemMessage(string message)
    {
        _messages.Add(new PEChatMessage(Microsoft.Extensions.AI.ChatRole.System, message));
    }

    public void AddUserMessage(string message)
    {
        _messages.Add(new PEChatMessage(Microsoft.Extensions.AI.ChatRole.User, message));
    }

    public async Task<string> GetResponseAsync(ChatOptions? options = null)
    {
        // Azure OpenAI SDK specific options
        var completionsOptions = new ChatCompletionsOptions
        {
            DeploymentName = _modelName,
            MaxTokens = 2000,
            Temperature = options?.Temperature ?? 0.7f
        };

        // Add messages to the request
        foreach (var message in _messages)
        {
            ChatRequestMessage requestMessage;
            if (message.Role == Microsoft.Extensions.AI.ChatRole.System)
            {
                requestMessage = new ChatRequestSystemMessage(message.Text);
            }
            else if (message.Role == Microsoft.Extensions.AI.ChatRole.User)
            {
                requestMessage = new ChatRequestUserMessage(message.Text);
            }
            else if (message.Role == Microsoft.Extensions.AI.ChatRole.Assistant)
            {
                requestMessage = new ChatRequestAssistantMessage(message.Text);
            }
            else
            {
                throw new ArgumentException($"Unknown role: {message.Role}");
            }

            completionsOptions.Messages.Add(requestMessage);
        }

        var response = await _client.GetChatCompletionsAsync(completionsOptions);
        var choice = response.Value.Choices[0];
        var responseMessage = choice.Message.Content;

        _messages.Add(new PEChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, responseMessage));

        return responseMessage;
    }

    public List<PEChatMessage> GetMessages()
    {
        return _messages;
    }
}

public class PEChatMessage
{
    public Microsoft.Extensions.AI.ChatRole Role { get; }
    public string Text { get; }

    public PEChatMessage(Microsoft.Extensions.AI.ChatRole role, string text)
    {
        Role = role;
        Text = text;
    }
}

public class ChatOptions
{
    public float? Temperature { get; set; }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public List<PEChatMessage> Messages { get; set; } = [];
}