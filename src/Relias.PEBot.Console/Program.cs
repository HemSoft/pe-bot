namespace Relias.PEBot.Console;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Relias.PEBot.AI;
using Relias.PEBot.Slack;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public class Program
{
    public static async Task Main(string[]args)
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
            string[] requiredKeys = {
                "azureOpenAIKey",
                "azureOpenAIUrl",
                "slackAppLevelToken",
                "slackBotToken",
                "Confluence:Email",
                "Confluence:APIToken",
                "Confluence:Domain"
            };

            var missingKeys = requiredKeys.Where(key => string.IsNullOrEmpty(configuration[key])).ToList();

            if (missingKeys.Any())
            {
                throw new InvalidOperationException(
                    $"Missing required configuration values: {string.Join(", ", missingKeys)}. " +
                    "Please ensure these are set in user secrets, environment variables, or appsettings.json.");
            }

            const string systemPrompt = "You are a helpful Performance Engineering assistant with access to Confluence documentation. " +
                                      "When asked questions about documentation, processes, or recent updates, you can search Confluence " +
                                      "and provide information about page content, contributors, and modification times. " +
                                      "Note: The Confluence integration is currently in development, so when using Confluence functions, " +
                                      "you'll receive a message indicating that implementation is pending. When you receive this message, " +
                                      "acknowledge that the integration is not yet complete and offer to help the user with other topics.";

            // Initialize the AssistantClient with system prompt
            var assistantClient = new AssistantClient(configuration, systemPrompt);

            Console.WriteLine("Starting Slack bot...");
            var slackAppToken = configuration["slackAppLevelToken"] ?? 
                throw new InvalidOperationException("slackAppLevelToken is required but was null");
            var slackBotToken = configuration["slackBotToken"] ?? 
                throw new InvalidOperationException("slackBotToken is required but was null");
            var slackClient = new SlackSocketClient(
                slackAppToken,
                slackBotToken,
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