namespace Relias.PEBot.Slack;

using Relias.PEBot.AI;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Events;
using SlackNet.WebApi;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public class MessageEventHandler : IEventHandler<MessageEvent>
{
    private readonly ISlackApiClient _slackClient;
    private readonly string _botUserId;
    private readonly AssistantClient? _assistantClient;
    private readonly PEChatClient? _chatClient;
    private readonly bool _useAssistantClient;

    // Constants for configuration
    private const int MaxBlockTextLength = 3000;
    private const string ProcessingMessage = "Processing your request...";
    private const string AIUnavailableMessage = "Sorry, the AI service is not available at the moment.";
    private const string ServiceErrorMessage = "Sorry, I'm having trouble accessing my AI services at the moment.";
    private const string SimpleResponseMessage = "Hi there! I received your message, but my AI service is not available right now.";
    private const string ErrorMessagePrefix = "Sorry, I encountered an error processing your request: ";
    private const string AIRequestErrorPrefix = "Sorry, I encountered an error processing your AI request: ";

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
                Console.WriteLine($"Both AssistantClient and ChatClient are null. Sending simple response to channel {message.Channel}");
                await SendFormattedMessage(message.Channel, SimpleResponseMessage, message.ThreadTs ?? message.Ts);
                return;
            }

            // First, send a processing message
            await SendSimpleMessage(message.Channel, ProcessingMessage, message.ThreadTs ?? message.Ts);

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
                response = ServiceErrorMessage;
            }

            Console.WriteLine($"Sending AI response to channel {message.Channel}: {response}");
            await SendFormattedMessage(message.Channel, response, message.ThreadTs ?? message.Ts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR responding to message: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Send error message to Slack
            string errorMessage = $"{ErrorMessagePrefix}{ex.Message}";
            await SendSimpleMessage(message.Channel, errorMessage, message.ThreadTs ?? message.Ts);
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

            await SendSimpleMessage(message.Channel, response, message.ThreadTs ?? message.Ts);
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
                await SendSimpleMessage(
                    message.Channel,
                    AIUnavailableMessage,
                    message.ThreadTs ?? message.Ts);
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
            await SendSimpleMessage(message.Channel, ProcessingMessage, message.ThreadTs ?? message.Ts);
            
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
                response = ServiceErrorMessage;
            }

            Console.WriteLine($"Received AI response: {response}");
            Console.WriteLine($"Sending AI response to channel {message.Channel}");

            await SendFormattedMessage(message.Channel, response, message.ThreadTs ?? message.Ts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR processing AI message: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Send error message to Slack
            string errorMessage = $"{AIRequestErrorPrefix}{ex.Message}";
            await SendSimpleMessage(message.Channel, errorMessage, message.ThreadTs ?? message.Ts);
        }
    }
    
    private async Task SendSimpleMessage(string channel, string text, string? threadTs = null)
    {
        // For simple messages or error messages, use standard text message
        var message = new Message
        {
            Channel = channel,
            Text = text,
            ThreadTs = threadTs,
            Parse = ParseMode.Full
        };
        
        await _slackClient.Chat.PostMessage(message);
    }

    private async Task SendFormattedMessage(string channel, string text, string? threadTs = null)
    {
        // Convert the text from standard Markdown to mrkdwn
        string mrkdwnText = ConvertToMrkdwn(text);

        // For longer AI responses, we'll check if we need to split the message to avoid truncation
        if (mrkdwnText.Length > MaxBlockTextLength)
        {
            // Split into multiple messages based on natural boundaries like paragraphs or list items
            var messages = SplitLongMessage(mrkdwnText);

            foreach (string messagePart in messages)
            {
                // Send each part as a formatted message
                await SendSingleFormattedMessage(channel, messagePart, threadTs);
            }
        }
        else
        {
            // Send the message as a single formatted message
            await SendSingleFormattedMessage(channel, mrkdwnText, threadTs);
        }
    }

    public static string ConvertToMrkdwn(string markdownText)
    {
        var placeholder = "{{BOLD}}";
        var pattern = @"\*\*(.*?)\*\*";
        markdownText = Regex.Replace(markdownText, pattern, match => placeholder + match.Groups[1].Value + placeholder);
        pattern = @"\*(.*?)\*";
        markdownText = Regex.Replace(markdownText, pattern, "_$1_");
        return markdownText.Replace(placeholder, "*");
    }

    private async Task SendSingleFormattedMessage(string channel, string text, string? threadTs = null)
    {
        // Check if the message contains a code block
        bool hasCodeBlock = Regex.IsMatch(text, "```[^`]*```");

        if (hasCodeBlock)
        {
            // For messages with code blocks, use the text field to ensure proper rendering
            var message = new Message
            {
                Channel = channel,
                ThreadTs = threadTs,
                Text = text
            };
            await _slackClient.Chat.PostMessage(message);
        }
        else
        {
            // Use Block Kit with Markdown for proper rendering
            var blocks = new List<Block>
            {
                new SectionBlock
                {
                    Text = new Markdown(text) // Use Markdown class to enable mrkdwn rendering
                }
            };

            var message = new Message
            {
                Channel = channel,
                ThreadTs = threadTs,
                Blocks = blocks.ToArray(),
                Text = text // Fallback text for notifications
            };

            await _slackClient.Chat.PostMessage(message);
        }
    }

    private List<string> SplitLongMessage(string text)
    {
        var result = new List<string>();
        
        // Try to split on paragraphs first (double line breaks)
        var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.None);
        
        var currentMessage = new List<string>();
        var currentLength = 0;
        
        foreach (var paragraph in paragraphs)
        {
            // If adding this paragraph would exceed the limit, finish the current message
            if (currentLength + paragraph.Length > MaxBlockTextLength && currentMessage.Count > 0)
            {
                result.Add(string.Join("\n\n", currentMessage));
                currentMessage.Clear();
                currentLength = 0;
            }
            
            // If a single paragraph is too long, we need to split it further
            if (paragraph.Length > MaxBlockTextLength)
            {
                // If we have accumulated content, send it first
                if (currentMessage.Count > 0)
                {
                    result.Add(string.Join("\n\n", currentMessage));
                    currentMessage.Clear();
                    currentLength = 0;
                }
                
                // Split the paragraph into sentences or by length
                var sentences = Regex.Split(paragraph, @"(?<=[.!?])\s+");
                var sentenceGroup = new List<string>();
                var sentenceGroupLength = 0;
                
                foreach (var sentence in sentences)
                {
                    if (sentenceGroupLength + sentence.Length > MaxBlockTextLength && sentenceGroup.Count > 0)
                    {
                        result.Add(string.Join(" ", sentenceGroup));
                        sentenceGroup.Clear();
                        sentenceGroupLength = 0;
                    }
                    
                    // If a single sentence is too long, split by pure length
                    if (sentence.Length > MaxBlockTextLength)
                    {
                        for (int i = 0; i < sentence.Length; i += MaxBlockTextLength)
                        {
                            int length = Math.Min(MaxBlockTextLength, sentence.Length - i);
                            result.Add(sentence.Substring(i, length));
                        }
                    }
                    else
                    {
                        sentenceGroup.Add(sentence);
                        sentenceGroupLength += sentence.Length;
                    }
                }
                
                // Add any remaining sentences
                if (sentenceGroup.Count > 0)
                {
                    result.Add(string.Join(" ", sentenceGroup));
                }
            }
            else
            {
                currentMessage.Add(paragraph);
                currentLength += paragraph.Length;
            }
        }
        
        // Add any remaining content
        if (currentMessage.Count > 0)
        {
            result.Add(string.Join("\n\n", currentMessage));
        }
        
        return result;
    }
}