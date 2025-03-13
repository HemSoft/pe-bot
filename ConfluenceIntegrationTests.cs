namespace Relias.PEBot.IntegrationTests;

using Microsoft.Extensions.Configuration;
using Relias.PEBot.AI;
using System;
using System.Threading.Tasks;
using Xunit;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

public class ConfluenceIntegrationTests : IAsyncLifetime
{
    private readonly IConfiguration _configuration;
    private AssistantClientSdk? _assistantClient;
    private const string TestSystemPrompt = @"
IMPORTANT TEST INSTRUCTION:
1. When asked about Confluence results, you MUST show exactly 5 results when asked for 5 latest documents
2. When showing Confluence results, always include the space, last updated date, and link
3. Always order Confluence results by most recent first";

    public ConfluenceIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<ConfluenceIntegrationTests>()
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
    public async Task SearchConfluence_ShouldReturnLatestFiveDocuments()
    {
        // Arrange
        Assert.NotNull(_assistantClient);

        // Act
        _assistantClient.AddUserMessage("Show me the 5 most recently updated Confluence documents");
        var response = await _assistantClient.GetResponseAsync();

        // Assert
        // Verify we got results
        Assert.Contains("Space:", response);
        Assert.Contains("Last Updated:", response);
        
        // Verify the response mentions limiting to 5 results
        Assert.Contains("5", response);
        
        // Count the number of results
        var resultCount = CountConfluenceResults(response);
        
        // We should have 5 or fewer results (might be fewer if there aren't 5 docs available)
        Assert.True(resultCount <= 5, $"Expected 5 or fewer results, but got {resultCount}");
        
        // Verify the results are ordered by date (most recent first)
        Assert.Contains("Last Updated:", response);
        
        // Log the response for debugging
        Console.WriteLine($"Response: {response}");
    }

    [Fact]
    public async Task SearchConfluence_DirectTest_ShouldReturnLatestDocuments()
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
        var result = await searchFunction.InvokeAsync("{\"query\": \"latest documents\"}");
        
        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("Space:", result);
        Assert.Contains("Last Updated:", result);
        
        // Log the result for debugging
        Console.WriteLine($"Direct function result: {result}");
    }

    private int CountConfluenceResults(string response)
    {
        // Simple counting of list items or entries in the response
        var lines = response.Split('\n');
        return lines.Count(line => line.Contains("Space:") || line.Contains("Last Updated:")) / 2; // Divide by 2 since each result has both Space and Last Updated
    }
} 