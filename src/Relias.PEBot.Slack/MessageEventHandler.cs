namespace Relias.PEBot.Slack;

using System;
using System.Threading.Tasks;
using Relias.PEBot.AI;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

public class MessageEventHandler : IEventHandler<MessageEvent>
{
    private readonly ISlackApiClient _slackClient;
    private readonly string _botUserId;
    private readonly AssistantClient? _assistantClient;
    private readonly PEChatClient? _chatClient;
    private readonly bool _useAssistantClient;

    public MessageEventHandler(
        ISlackApiClient slackClient, 
        string botUserId, 
        AssistantClient? assistantClient = null, 
        PEChatClient? chatClient = null)
    {
        _slackClient = slackClient;
        _botUserId = botUserId;
        _assistantClient = assistantClient;
        _chatClient = chatClient;
        _useAssistantClient = _assistantClient != null;
        
        Console.WriteLine($"MessageEventHandler initialized with bot ID: {_botUserId}");
        Console.WriteLine($"Using AssistantClient: {_useAssistantClient}");
    }

    public Task Handle(MessageEvent slackEvent)
    {
        // Log EVERY event received to help with debugging
        Console.WriteLine($"[EVENT RECEIVED] Type: {slackEvent.Type}, Subtype: {slackEvent.Subtype}, User: {slackEvent.User}, Text: {slackEvent.Text}");

        // Ignore messages from the bot itself or empty messages
        if (slackEvent.User == _botUserId || string.IsNullOrEmpty(slackEvent.Text))
        {
            Console.WriteLine("Ignoring message from bot itself or empty message");
            return Task.CompletedTask;
        }

        // Check if this is a direct mention of our bot
        var isMention = slackEvent.Text.Contains($"<@{_botUserId}>");
        
        // Check if this message has format "<USERID> AI Real message here."
        var isAIMessage = slackEvent.Text.Contains("AI ", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"Message: '{slackEvent.Text}', IsMention: {isMention}, IsAIMessage: {isAIMessage}, Channel: {slackEvent.Channel}");

        // Process ALL messages - we'll respond to @mentions and direct messages
        if (string.IsNullOrEmpty(slackEvent.Subtype))
        {
            if (isAIMessage && (_assistantClient != null || _chatClient != null))
            {
                Console.WriteLine($"Processing AI message from user: {slackEvent.User}");
                return RespondToAIMessage(slackEvent);
            }
            // Check for AI messages
            else if (isMention)
            {
                Console.WriteLine($"Processing @mention from user: {slackEvent.User}");
                return RespondToMention(slackEvent);
            }
            // For direct messages (DMs) - respond to all messages
            else if (slackEvent.Channel.StartsWith("D"))
            {
                Console.WriteLine($"Processing direct message from user: {slackEvent.User}");
                return RespondToMessage(slackEvent);
            }
            // For public channels - only respond to @mentions
        }

        return Task.CompletedTask;
    }

    private async Task RespondToMessage(MessageEvent message)
    {
        try
        {
            if (_assistantClient == null && _chatClient == null)
            {
                string resp = "Hi there! I received your message, but my AI service is not available right now.";
                Console.WriteLine($"Both AssistantClient and ChatClient are null. Sending simple response to channel {message.Channel}");
                await _slackClient.Chat.PostMessage(new Message
                {
                    Channel = message.Channel,
                    Text = resp
                });
                return;
            }

            // First, send a processing message
            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = message.Channel,
                Text = "Processing your request...",
                ThreadTs = message.ThreadTs ?? message.Ts
            });

            string response;
            
            if (_useAssistantClient && _assistantClient != null)
            {
                // Use the AssistantClient to process the message
                _assistantClient.AddUserMessage(message.Text);
                response = await _assistantClient.GetResponseAsync();
            }
            else if (_chatClient != null)
            {
                // Use the ChatClient to process DMs
                _chatClient.AddUserMessage(message.Text);
                response = await _chatClient.GetResponseAsync();
            }
            else
            {
                response = "Sorry, I'm having trouble accessing my AI services at the moment.";
            }

            Console.WriteLine($"Sending AI response to channel {message.Channel}: {response}");

            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = message.Channel,
                Text = response,
                ThreadTs = message.ThreadTs ?? message.Ts
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR responding to message: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Send error message to Slack
            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = message.Channel,
                Text = $"Sorry, I encountered an error processing your request: {ex.Message}",
                ThreadTs = message.ThreadTs ?? message.Ts
            });
        }
    }

    private async Task RespondToMention(MessageEvent message)
    {
        try
        {
            // Clean the message text by removing the mention
            string cleanText = message.Text.Replace($"<@{_botUserId}>", "").Trim();
            Console.WriteLine($"Processing mention with text: {cleanText}");

            string response = $"Hi there! I received your mention. You said: '{cleanText}'";

            Console.WriteLine($"Sending response to @mention in channel {message.Channel}: {response}");

            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = message.Channel,
                Text = response,
                ThreadTs = message.ThreadTs ?? message.Ts // Reply in thread if it's part of a thread
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR responding to mention: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private async Task RespondToAIMessage(MessageEvent message)
    {
        try
        {
            if (_assistantClient == null && _chatClient == null)
            {
                Console.WriteLine("Both AssistantClient and ChatClient are not initialized. Cannot process AI message.");
                await _slackClient.Chat.PostMessage(new Message
                {
                    Channel = message.Channel,
                    Text = "Sorry, the AI service is not available at the moment.",
                    ThreadTs = message.ThreadTs ?? message.Ts
                });
                return;
            }

            // Extract the actual message text after "AI "
            string cleanText = message.Text;
            int aiIndex = cleanText.IndexOf("> AI ", StringComparison.OrdinalIgnoreCase);
            if (aiIndex >= 0)
            {
                // Get everything after "AI "
                cleanText = cleanText.Substring(aiIndex + 5).Trim();
            }
            
            Console.WriteLine($"Sending message to AI: {cleanText}");
            
            // First, send a processing message
            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = message.Channel,
                Text = "Processing your request...",
                ThreadTs = message.ThreadTs ?? message.Ts
            });
            
            string response;

            if (_useAssistantClient && _assistantClient != null)
            {
                // Use the AssistantClient
                _assistantClient.AddUserMessage(cleanText);
                response = await _assistantClient.GetResponseAsync();
            }
            else if (_chatClient != null)
            {
                // Use the ChatClient
                _chatClient.AddUserMessage(cleanText);
                response = await _chatClient.GetResponseAsync();
            }
            else
            {
                response = "Sorry, I'm having trouble accessing my AI services at the moment.";
            }

            Console.WriteLine($"Received AI response: {response}");
            Console.WriteLine($"Sending AI response to channel {message.Channel}");

            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = message.Channel,
                Text = response,
                ThreadTs = message.ThreadTs ?? message.Ts // Reply in thread if it's part of a thread
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR processing AI message: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Send error message to Slack
            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = message.Channel,
                Text = $"Sorry, I encountered an error processing your AI request: {ex.Message}",
                ThreadTs = message.ThreadTs ?? message.Ts
            });
        }
    }
}