namespace Relias.PEBot.Slack;

using System;
using System.Threading.Tasks;
using Relias.PEBot.AI;
using SlackNet;
using SlackNet.WebApi;

public class SlackSocketClient
{
    private readonly string _appLevelToken;
    private readonly string _botToken;
    private readonly AssistantClient? _assistantClient;
    private readonly PEChatClient? _chatClient;

    public SlackSocketClient(
        string appLevelToken, 
        string botToken, 
        AssistantClient? assistantClient = null, 
        PEChatClient? chatClient = null)
    {
        _appLevelToken = appLevelToken;
        _botToken = botToken;
        _assistantClient = assistantClient;
        _chatClient = chatClient;
        
        // Log which client is being used
        if (_assistantClient != null)
        {
            Console.WriteLine("SlackSocketClient initialized with AssistantClient");
        }
        else if (_chatClient != null)
        {
            Console.WriteLine("SlackSocketClient initialized with PEChatClient");
        }
        else
        {
            Console.WriteLine("SlackSocketClient initialized without AI client");
        }
    }

    public async Task ConnectToSlack()
    {
        try
        {
            // First get bot info to get the bot's user ID
            Console.WriteLine("Getting bot info...");
            var slackApiClient = new SlackServiceBuilder()
                .UseApiToken(_botToken)
                .GetApiClient();

            var botInfo = await slackApiClient.Auth.Test();
            string botUserId = botInfo.UserId;
            Console.WriteLine($"Bot info retrieved - Name: {botInfo.User}, ID: {botUserId}, Team: {botInfo.Team}");

            // Register for all message events
            Console.WriteLine("Registering message event handler...");
            var slackBuilder = new SlackServiceBuilder()
                .UseAppLevelToken(_appLevelToken)
                .UseApiToken(_botToken)
                .RegisterEventHandler(new MessageEventHandler(slackApiClient, botUserId, _assistantClient, _chatClient));

            Console.WriteLine("Connecting socket mode client...");
            var client = slackBuilder.GetSocketModeClient();
            await client.Connect();
            Console.WriteLine("Socket Mode connection successful!");

            // Print instructions for the user
            const string botConnectedText = "\n======= BOT CONNECTED SUCCESSFULLY =======";
            Console.WriteLine(botConnectedText);
            Console.WriteLine($"Connected as: {botInfo.User} (ID: {botUserId}) to team: {botInfo.Team}");
            Console.WriteLine($"To interact with the bot in channels: @mention it using <@{botUserId}>");
            Console.WriteLine("You can also send direct messages to the bot");
            Console.WriteLine($"Using AssistantClient: {_assistantClient != null}, Using ChatClient: {_chatClient != null}");

            // Print troubleshooting guidance
            const string troubleshootingHeader = "===== TROUBLESHOOTING =====";
            Console.WriteLine(troubleshootingHeader);
            Console.WriteLine("If the bot isn't responding, check the following:");
            Console.WriteLine("1. Ensure Events API is enabled in your Slack App configuration");
            Console.WriteLine("2. Verify the app has these required scopes:");
            Console.WriteLine("   - chat:write");
            Console.WriteLine("   - channels:history");
            Console.WriteLine("   - groups:history");
            Console.WriteLine("   - im:history");
            Console.WriteLine("3. Verify the app is subscribed to these events:");
            Console.WriteLine("   - message.channels");
            Console.WriteLine("   - message.im");
            Console.WriteLine("4. Make sure the bot is invited to the channel you're testing in");
            Console.WriteLine("5. Socket Mode must be enabled in your Slack app configuration");
        }
        catch (SlackException ex)
        {
            Console.WriteLine($"SLACK API ERROR: {ex.Message}");
            Console.WriteLine($"Error details: {ex}");
            Console.WriteLine("Please check your Slack tokens and permissions.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GENERAL ERROR: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
}