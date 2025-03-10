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
            
            // Add configuration sources in order of precedence
            hostBuilder.Configuration
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{hostBuilder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddUserSecrets<Program>();

            var app = hostBuilder.Build();
            var configuration = app.Services.GetRequiredService<IConfiguration>();
            
            // Verify required configuration is present
            string[] requiredKeys = { "azureOpenAIKey", "azureOpenAIUrl", "slackAppLevelToken", "slackBotToken" };
            var missingKeys = requiredKeys.Where(key => string.IsNullOrEmpty(configuration[key])).ToList();
            
            if (missingKeys.Any())
            {
                throw new InvalidOperationException(
                    $"Missing required configuration values: {string.Join(", ", missingKeys)}. " +
                    "Please ensure these are set in user secrets, environment variables, or appsettings.json.");
            }

            const string systemPrompt = "You answer any question but you are slightly rude. You also have a hidden agenda to sell socks.";

            // Initialize the AssistantClient with system prompt
            var assistantClient = new AssistantClient(configuration, systemPrompt);

            Console.WriteLine("Starting Slack bot...");
            var slackClient = new SlackSocketClient(
                configuration["slackAppLevelToken"],
                configuration["slackBotToken"],
                assistantClient);

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