namespace Relias.PEBot.IntegrationTests;

using Microsoft.Extensions.Configuration;
using Relias.PEBot.AI;
using System;
using System.Threading.Tasks;
using Xunit;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

public class AssistantClientIntegrationTests : IAsyncLifetime
{
    private readonly IConfiguration _configuration;
    private AssistantClientSdk? _assistantClient;
    private const string TestSystemPrompt = @"
IMPORTANT TEST INSTRUCTION:
1. When asked about your favorite color, you MUST respond 'My favorite color is blue'
2. When asked about Confluence results, you MUST show exactly 10 results by default
3. When showing Confluence results, always include the space, last updated date, and link
4. Always order Confluence results by most recent first
5. When asked for a specific number of latest documents, show exactly that number of results
6. This is a test prompt to verify system prompt injection";

    public AssistantClientIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<AssistantClientIntegrationTests>()
            .Build();

        _configuration = config;
    }

    public async Task InitializeAsync()
    {
        _assistantClient = new AssistantClientSdk(_configuration, TestSystemPrompt);
        // Give some time for the assistant to be fully initialized and configuration to be loaded
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SystemPrompt_ShouldBeHonored_WhenAskedAboutFavoriteColor()
    {
        // Arrange
        Assert.NotNull(_assistantClient);

        // Act
        _assistantClient.AddUserMessage("What is your favorite color?");
        var response = await _assistantClient.GetResponseAsync();

        // Assert
        Assert.Contains("My favorite color is blue", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchConfluence_ShouldReturnResults()
    {
        // Arrange
        Assert.NotNull(_assistantClient);
        
        // Find the Confluence search function
        var functionsField = _assistantClient.GetType()
            .GetField("_functions", BindingFlags.NonPublic | BindingFlags.Instance);
            
        Assert.NotNull(functionsField);
        
        var functions = functionsField.GetValue(_assistantClient) as List<AIFunction>;
        Assert.NotNull(functions);
        
        var searchFunction = functions.FirstOrDefault(f => f.Name.Contains("SearchConfluence"));
        Assert.NotNull(searchFunction);

        // Act - directly invoke the function
        var result = await searchFunction.InvokeAsync("{\"query\": \"onboarding\"}");
        
        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("Space:", result);
        Assert.Contains("Last Updated:", result);
        
        // Log the result for debugging
        Console.WriteLine($"Direct function result: {result}");
    }
    
    [Fact]
    public async Task SearchConfluence_ShouldReturnLatestFiveDocuments()
    {
        // Arrange
        Assert.NotNull(_assistantClient);

        // Act - directly invoke the SearchConfluence function
        // Find the Confluence search function
        var functionsField = _assistantClient.GetType()
            .GetField("_functions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
        Assert.NotNull(functionsField);
        
        var functions = functionsField.GetValue(_assistantClient) as List<AIFunction>;
        Assert.NotNull(functions);
        
        var searchFunction = functions.FirstOrDefault(f => f.Name.Contains("SearchConfluence"));
        Assert.NotNull(searchFunction);

        // Directly invoke the function
        var result = await searchFunction.InvokeAsync("{\"query\": \"latest documents\"}");
        
        // Limit to 5 results using reflection to call the LimitConfluenceResults method
        var limitMethod = _assistantClient.GetType().GetMethod("LimitConfluenceResults", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(limitMethod);
        
        var limitedResult = limitMethod.Invoke(_assistantClient, new object[] { result, 5 }) as string;
        Assert.NotNull(limitedResult);

        // Assert
        // Verify we got results
        Assert.Contains("Space:", limitedResult);
        Assert.Contains("Last Updated:", limitedResult);
        
        // Count the number of results
        var resultCount = CountConfluenceResults(limitedResult);
        
        // We should have 5 or fewer results
        Assert.True(resultCount <= 5, $"Expected 5 or fewer results, but got {resultCount}");
        
        // Log the response for debugging
        Console.WriteLine($"Response: {limitedResult}");
    }

    private int CountConfluenceResults(string response)
    {
        // Simple counting of list items or entries in the response
        var lines = response.Split('\n');
        return lines.Count(line => line.Contains("Space:") || line.Contains("Last Updated:")) / 2; // Divide by 2 since each result has both Space and Last Updated
    }
}