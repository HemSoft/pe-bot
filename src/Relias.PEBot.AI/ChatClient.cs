namespace Relias.PEBot.AI;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Azure;
using Azure.AI.OpenAI;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class PEChatClient
{
    private readonly IChatClient _chatClient;
    private readonly List<ChatMessage> _messages = [];

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

        var openAiClient = new AzureOpenAIClient(new Uri(apiUrl), apiKeyCredentials);
        const string modelName = "gpt-4o-mini";
        //const string modelName = "gpt-4o";
        //const string modelName = "gpt-4-turbo";

        // Open Chat Client:
        var innerChatClient = openAiClient.GetChatClient(modelName);

        var services = new ServiceCollection();
        services.AddChatClient(builder => builder
            .UseFunctionInvocation()
            .Use(innerChatClient.AsChatClient()));

        var serviceProvider = services.BuildServiceProvider();
        _chatClient = serviceProvider.GetRequiredService<IChatClient>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            _messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        }
    }

    public void AddSystemMessage(string message)
    {
        _messages.Add(new ChatMessage(ChatRole.System, message));
    }

    public void AddUserMessage(string message)
    {
        _messages.Add(new ChatMessage(ChatRole.User, message));
    }

    public async Task<string> GetResponseAsync(ChatOptions? options = null)
    {
        var response = await _chatClient.CompleteAsync(_messages, options ?? new ChatOptions());
        _messages.Add(response.Message);
        return response.Message.Text ?? string.Empty;
    }

    public List<ChatMessage> GetMessages()
    {
        return _messages;
    }
}