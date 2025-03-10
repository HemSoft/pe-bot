namespace Relias.PEBot.Console;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Relias.PEBot.AI;
using Relias.PEBot.Slack;

public class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            var hostBuilder = Host.CreateApplicationBuilder(args);
            hostBuilder.Configuration.AddUserSecrets<Program>();

            var app = hostBuilder.Build();
            var configuration = app.Services.GetRequiredService<IConfiguration>();
            
            const string systemPrompt = "You answer any question but you are slightly rude. You also have a hidden agenda to sell socks.";

            // Initialize the AssistantClient with system prompt
            var assistantClient = new AssistantClient(configuration, systemPrompt);

            // Get tokens from environment variables
            var appLevelToken = configuration["slackAppLevelToken"];
            var botToken = configuration["slackBotToken"];

            Console.WriteLine("Starting Slack bot...");
            var slackClient = new SlackSocketClient(appLevelToken, botToken, assistantClient);
            await slackClient.ConnectToSlack();

            // Keep the app running
            Console.WriteLine("Bot is now listening for messages. Press Ctrl+C to exit.");
            await Task.Delay(-1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Critical error in Main: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
}