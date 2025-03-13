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
using System.Collections.Generic;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Set up console output logging to file
        var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "Output.txt");
        using var writer = new StreamWriter(outputPath, false);  // false to overwrite file each run
        var multiWriter = new MultiTextWriter(new TextWriter[] { System.Console.Out, writer });
        System.Console.SetOut(multiWriter);

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

            // Check if we're in test mode for Confluence search
            if (args.Length > 0 && args[0].Equals("--test-confluence", StringComparison.OrdinalIgnoreCase))
            {
                await TestConfluenceSearch(configuration, args.Skip(1).ToArray());
                return;
            }

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

            const string systemPrompt = @"You are a helpful Productivity Engineering Assistant.

You are created by Relias, designed to assist employees with their workstation setups, onboarding processes, and to provide information related to company policies. Your purpose is to help create a smooth and efficient experience for users as they navigate their work environment.

Here are the things you can assist with:
1. Workstation Setup: Providing guidance on how to install and configure software specific to Relias.
2. Onboarding Assistance: Offering information about onboarding procedures and resources available for new hires or existing employees setting up new workstations.
3. HR-related Inquiries: Answering questions about company policies, employee benefits, and other HR-related topics as per the provided documentation.
4. Confluence Documentation: Helping you navigate and find specific documentation in Confluence related to various topics including onboarding, software development, and company policies.
5. Troubleshooting: Assisting with troubleshooting software issues or setup problems you may encounter during the workstation setup process.

IMPORTANT INSTRUCTIONS FOR CONFLUENCE SEARCHES:
- When a user asks about documentation or information that might be in Confluence, ALWAYS use the SearchConfluence function.
- The Confluence integration is fully functional and working correctly.
- NEVER respond that there are no documents available unless the SearchConfluence function explicitly returns no results.
- If the SearchConfluence function returns results, ALWAYS show those results to the user.
- ALWAYS show 10 results by default unless the user specifically requests a different number.
- Order results by most recent updates first.
- Only show a different number of results if the user explicitly requests it.
- If user asks for 'all results' or a specific number, honor that request.
- When a user asks for documents updated within a specific time period, use the results from SearchConfluence and filter them based on the 'Last Updated' field.";

            // Initialize the AssistantClient with system prompt using SDK version
            var assistantClient = new AssistantClientSdk(configuration, systemPrompt);

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
            System.Console.WriteLine($"Critical error in Main: {ex.Message}");
            System.Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private static async Task TestConfluenceSearch(IConfiguration configuration, string[] searchTerms)
    {
        if (searchTerms.Length == 0)
        {
            Console.WriteLine("Usage: --test-confluence <search terms>");
            return;
        }

        // Verify Confluence configuration
        var confluenceEmail = configuration["Confluence:Email"];
        var confluenceToken = configuration["Confluence:APIToken"];
        var confluenceDomain = configuration["Confluence:Domain"];

        if (string.IsNullOrEmpty(confluenceEmail) || string.IsNullOrEmpty(confluenceToken) || string.IsNullOrEmpty(confluenceDomain))
        {
            Console.WriteLine("Missing Confluence configuration. Please check your configuration settings.");
            return;
        }

        try
        {
            // Initialize space priorities (can be moved to configuration later)
            var spacePriorities = new Dictionary<string, int>
            {
                { "RLPD", 3 },     // Software Development
                { "RLMSM", 3 },    // Relias Platform Modernization
                { "PD", 2 },       // Product Development
                { "CEMVP", 2 },    // Relias Communities Platform
                { "DA", 1 }        // Data & Analytics
            };

            var confluenceProvider = new ConfluenceInfoProvider(
                confluenceDomain,
                confluenceToken,
                confluenceEmail,
                spacePriorities: spacePriorities);

            var searchQuery = string.Join(" ", searchTerms);
            
            Console.WriteLine($"Searching Confluence for: {searchQuery}");
            Console.WriteLine("----------------------------------------");
            
            var results = await confluenceProvider.SearchConfluence(searchQuery);
            Console.WriteLine(results);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during Confluence search: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
            }
        }
    }
}

public class MultiTextWriter : TextWriter
{
    private readonly IEnumerable<TextWriter> _writers;
    private readonly object _lock = new();
    private bool _isDebugOutput = false;
    
    public MultiTextWriter(IEnumerable<TextWriter> writers)
    {
        _writers = writers;
    }

    public override void Write(char value)
    {
        lock (_lock)
        {
            foreach (var writer in _writers)
            {
                // Only write debug output to console, not to the output file
                if (_isDebugOutput && writer != System.Console.Out)
                    continue;
                    
                writer.Write(value);
                writer.Flush();  // Ensure immediate output
            }
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        
        // Check if this is debug output
        _isDebugOutput = IsDebugOutput(value);
        
        lock (_lock)
        {
            foreach (var writer in _writers)
            {
                // Only write debug output to console, not to the output file
                if (_isDebugOutput && writer != System.Console.Out)
                    continue;
                    
                writer.Write(value);
                writer.Flush();  // Ensure immediate output
            }
        }
    }

    public override void WriteLine(string? value)
    {
        if (value == null) return;
        
        // Check if this is debug output
        _isDebugOutput = IsDebugOutput(value);
        
        lock (_lock)
        {
            foreach (var writer in _writers)
            {
                // Only write debug output to console, not to the output file
                if (_isDebugOutput && writer != System.Console.Out)
                    continue;
                    
                writer.WriteLine(value);
                writer.Flush();  // Ensure immediate output
            }
        }
    }

    private bool IsDebugOutput(string text)
    {
        // Add patterns that indicate debug/internal output
        string[] debugPatterns = new[]
        {
            "Raw response:",
            "Found required_action:",
            "Processing tool call",
            "Executing function",
            "Arguments:",
            "Function returned:",
            "Tool call_",
            "Making API request",
            "Response status code:",
            "Added result to tool outputs:",
            "Returning tool outputs",
            "Submitting tool outputs"
        };

        return debugPatterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var writer in _writers)
            {
                writer.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}