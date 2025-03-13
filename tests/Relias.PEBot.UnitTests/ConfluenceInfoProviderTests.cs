namespace Relias.PEBot.UnitTests;

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Relias.PEBot.AI;
using Xunit;

public class ConfluenceInfoProviderTests
{
    private const string TestConfluenceUrl = "https://test.atlassian.net";
    private const string TestApiToken = "test-token";
    private const string TestEmail = "test@example.com";

    private static MockHttpMessageHandler CreateMockHandler()
    {
        var handler = new MockHttpMessageHandler();
        // Add mock responses for validation endpoints
        handler.SetupResponse(
            $"{TestConfluenceUrl}/wiki/api/v2/spaces",
            """
            {
                "results": [
                    {
                        "id": "123",
                        "key": "TEST",
                        "name": "Test Space",
                        "type": "global",
                        "status": "current"
                    }
                ],
                "_links": {}
            }
            """);

        return handler;
    }

    [Fact]
    public async Task SearchConfluence_WithValidQuery_ReturnsResults()
    {
        // Arrange
        var mockHandler = CreateMockHandler();
        mockHandler.SetupResponse(
            $"{TestConfluenceUrl}/wiki/api/v2/search?type=page&limit=25&excerpt=highlight&query=onboarding",
            """
            {
                "results": [
                    {
                        "id": "123",
                        "type": "page",
                        "title": "Onboarding Guide",
                        "space": {
                            "id": "456",
                            "key": "DOC",
                            "name": "Documentation",
                            "_links": {}
                        },
                        "version": {
                            "when": "2024-02-20T10:00:00Z"
                        },
                        "_links": {}
                    }
                ],
                "_links": {}
            }
            """);

        var provider = new ConfluenceInfoProvider(TestConfluenceUrl, TestApiToken, TestEmail, mockHandler.ToHttpClient());

        // Act
        var result = await provider.SearchConfluence("onboarding");

        // Assert
        Assert.Contains("Onboarding Guide", result);
        Assert.Contains("Documentation (DOC)", result);
    }

    [Fact]
    public async Task SearchConfluence_WithEmptyResponse_ReturnsNoResultsMessage()
    {
        // Arrange
        var mockHandler = CreateMockHandler();
        mockHandler.SetupResponse(
            $"{TestConfluenceUrl}/wiki/api/v2/search?type=page&limit=25&excerpt=highlight&query=nonexistent",
            """
            {
                "results": [],
                "_links": {}
            }
            """);

        var provider = new ConfluenceInfoProvider(TestConfluenceUrl, TestApiToken, TestEmail, mockHandler.ToHttpClient());

        // Act
        var result = await provider.SearchConfluence("nonexistent");

        // Assert
        Assert.Contains("I couldn't find any results", result);
    }

    [Fact]
    public async Task SearchConfluence_WhenV2ApiFails_FallsBackToV1Api()
    {
        // Arrange
        var mockHandler = CreateMockHandler();
        // Setup V2 API to fail
        mockHandler.SetupResponse(
            $"{TestConfluenceUrl}/wiki/api/v2/search?type=page&limit=25&excerpt=highlight&query=test",
            "Service Unavailable",
            HttpStatusCode.ServiceUnavailable);

        // Setup V1 API to succeed with URL matching code's encoding
        mockHandler.SetupResponse(
            $"{TestConfluenceUrl}/wiki/rest/api/content/search?cql=type%3Dpage AND %28title ~ \"test\" OR text ~ \"test\"%29 ORDER BY lastmodified DESC&expand=space,version&limit=25",
            """
            {
                "results": [
                    {
                        "id": "789",
                        "type": "page",
                        "title": "Test Page",
                        "space": {
                            "id": "123",
                            "key": "TEST",
                            "name": "Test Space",
                            "_links": {}
                        },
                        "version": {
                            "when": "2024-02-20T10:00:00Z"
                        },
                        "_links": {}
                    }
                ]
            }
            """);

        var provider = new ConfluenceInfoProvider(TestConfluenceUrl, TestApiToken, TestEmail, mockHandler.ToHttpClient());

        // Act
        var result = await provider.SearchConfluence("test");

        // Assert
        Assert.Contains("Test Page", result);
        Assert.Contains("Test Space (TEST)", result);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode StatusCode, string Content)> _responses 
            = new();

        public void SetupResponse(string url, string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[url] = (statusCode, content);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUrl = request.RequestUri!.ToString();
            Console.WriteLine($"Mock handler received request for URL: {requestUrl}");
            Console.WriteLine($"Known URLs in mock:");
            foreach (var url in _responses.Keys)
            {
                Console.WriteLine($"- {url}");
                if (url == requestUrl)
                {
                    Console.WriteLine("  ✓ Exact match found!");
                }
            }

            if (_responses.TryGetValue(requestUrl, out var response))
            {
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = response.StatusCode,
                    Content = new StringContent(response.Content)
                });
            }

            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound,
                Content = new StringContent("Not found")
            });
        }

        public HttpClient ToHttpClient() => new(this);
    }
}