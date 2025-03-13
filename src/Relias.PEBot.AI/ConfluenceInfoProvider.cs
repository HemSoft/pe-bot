namespace Relias.PEBot.AI;

using HtmlAgilityPack;
using Models;
using System;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using System.Collections.Generic;
using System.Linq;

public class ConfluenceInfoProvider
{
    private const string NoResultsMessage = "I couldn't find any results for that query in Confluence. Please try another search term or check if the information exists in Confluence.";
    private const string ApiErrorMessage = "I encountered an issue accessing Confluence. This might be due to incorrect configuration or API changes. Error details: {0}";
    private readonly string _confluenceUrl;
    private readonly string _apiToken;
    private readonly string _email;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _apiBaseUrl;
    private readonly Dictionary<string, int> _spacePriorities;

    public ConfluenceInfoProvider(string confluenceUrl, string apiToken, string email, HttpClient? httpClient = null, Dictionary<string, int>? spacePriorities = null)
    {
        // Ensure the URL starts with https://
        if (!confluenceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
            !confluenceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            confluenceUrl = "https://" + confluenceUrl;
        }
        
        _confluenceUrl = confluenceUrl.TrimEnd('/');
        _apiBaseUrl = $"{_confluenceUrl}/wiki/api/v2";
        _apiToken = apiToken;
        _email = email;
        _httpClient = httpClient ?? new HttpClient();
        _spacePriorities = spacePriorities ?? new Dictionary<string, int>();
        
        Console.WriteLine($"Initializing ConfluenceInfoProvider with URL: {_confluenceUrl}");
        Console.WriteLine($"API base URL: {_apiBaseUrl}");
        
        // Set up basic authentication if using default HttpClient
        if (httpClient == null)
        {
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_email}:{_apiToken}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        // Validate configuration immediately
        ValidateConfiguration().GetAwaiter().GetResult();
    }

    private async Task ValidateConfiguration()
    {
        try
        {
            // Try to access the v2 API spaces endpoint
            string requestUrl = $"{_apiBaseUrl}/spaces";
            Console.WriteLine($"Validating Confluence configuration by accessing: {requestUrl}");
            
            var response = await _httpClient.GetAsync(requestUrl);
            Console.WriteLine($"Validation response status code: {response.StatusCode}");
            
            if (!response.IsSuccessStatusCode)
            {
                // If v2 fails, try the v1 API
                requestUrl = $"{_confluenceUrl}/wiki/rest/api/space";
                Console.WriteLine($"Retrying with v1 API: {requestUrl}");
                response = await _httpClient.GetAsync(requestUrl);
                Console.WriteLine($"V1 API response status code: {response.StatusCode}");
            }
            
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Validation failed. Response content: {content}");
                throw new Exception($"Could not access Confluence API. Status: {response.StatusCode}, Content: {content}");
            }

            Console.WriteLine("Confluence configuration validated successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to validate Confluence configuration: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
            }
            throw;
        }
    }

    [Description("I need to find documentation in Confluence about {query}")]
    public async Task<string> SearchConfluence(
        [Description("What topic are you looking for information about?")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Please provide a search term to look for in Confluence.";
        }

        try
        {
            Console.WriteLine($"SearchConfluence called with query: {query}");
            
            // Try v2 API first with broader search capabilities
            var requestUrl = $"{_apiBaseUrl}/search?" +
                $"type=page&" +
                $"limit=25&" +
                $"excerpt=highlight&" +
                $"query={Uri.EscapeDataString(query)}";
                
            Console.WriteLine($"Making search API request to: {requestUrl}");
            var response = await _httpClient.GetAsync(requestUrl);

            // If v2 fails, try v1 API with CQL
            if (!response.IsSuccessStatusCode)
            {
                // Build the CQL query with proper encoding
                var quotedQuery = $"\"{query}\"";
                var cql = $"type=page AND (title ~ {quotedQuery} OR text ~ {quotedQuery}) ORDER BY lastmodified DESC";
                var queryString = $"cql={Uri.EscapeDataString(cql)}&expand=space,version&limit=25";
                requestUrl = $"{_confluenceUrl}/wiki/rest/api/content/search?{queryString}";
                    
                Console.WriteLine($"Retrying with v1 API: {requestUrl}");
                response = await _httpClient.GetAsync(requestUrl);
            }
            
            Console.WriteLine($"Search response status code: {response.StatusCode}");
            
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                throw new Exception($"API request failed. Status: {response.StatusCode}, Content: {content}");
            }
            
            var responseText = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Raw response: {responseText}");
            
            var searchResponse = await JsonSerializer.DeserializeAsync<SearchResponse>(
                await response.Content.ReadAsStreamAsync(), 
                _jsonOptions);

            if (searchResponse?.Results == null || !searchResponse.Results.Any())
            {
                Console.WriteLine("No search results found");
                return NoResultsMessage;
            }

            // Order results by space priority first, then by last updated date
            var orderedResults = searchResponse.Results
                .Where(r => r != null)
                .OrderByDescending(r => _spacePriorities.GetValueOrDefault(r.Space?.Key ?? "", 0))
                .ThenByDescending(r => r.LastUpdated)
                .ToList();

            var resultBuilder = new StringBuilder();
            resultBuilder.AppendLine($"Found {orderedResults.Count} relevant pages in Confluence (ordered by priority and date):");
            resultBuilder.AppendLine();

            foreach (var result in orderedResults)
            {
                if (result?.Title == null) continue;

                string pageUrl = GetPageUrl(result.Id ?? "", result.Space?.Key);
                string lastUpdated = FormatRelativeTime(result.LastUpdated);
                string spaceName = result.Space?.Name ?? "Unknown Space";
                string spaceKey = result.Space?.Key ?? "UNKNOWN";
                int spacePriority = _spacePriorities.GetValueOrDefault(spaceKey, 0);

                resultBuilder.AppendLine($"📄 {result.Title}");
                resultBuilder.AppendLine($"   Space: {spaceName} ({spaceKey}){(spacePriority > 0 ? $" [Priority: {spacePriority}]" : "")}");
                resultBuilder.AppendLine($"   Last Updated: {lastUpdated}");
                resultBuilder.AppendLine($"   Link: {pageUrl}");
                resultBuilder.AppendLine();
            }

            return resultBuilder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in SearchConfluence: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
            }
            return string.Format(ApiErrorMessage, ex.Message);
        }
    }

    [Description("Can you show me the content of the Confluence page with ID {pageId}?")]
    public async Task<string> GetPageContent(
        [Description("What is the ID of the page you want to see?")] string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return "Please provide a valid Confluence page ID.";
        }

        try
        {
            string requestUrl = $"{_confluenceUrl}/rest/api/content/{pageId}?expand=body.storage";
            Console.WriteLine($"Making API request to: {requestUrl}");
            
            var response = await _httpClient.GetAsync(requestUrl);
            Console.WriteLine($"Response status code: {response.StatusCode}");
            
            response.EnsureSuccessStatusCode();
            
            var page = await JsonSerializer.DeserializeAsync<ConfluencePage>(
                await response.Content.ReadAsStreamAsync(),
                _jsonOptions);

            if (page?.Body?.Storage?.Value == null)
            {
                Console.WriteLine("No content found in response");
                return "Sorry, I couldn't find any content in that page.";
            }

            // Strip HTML tags and clean up the content
            var doc = new HtmlDocument();
            doc.LoadHtml(page.Body.Storage.Value);
            string textContent = doc.DocumentNode.InnerText.Trim();
            
            // Format the response
            var responseBuilder = new StringBuilder();
            responseBuilder.AppendLine($"📄 {page.Title}");
            responseBuilder.AppendLine();
            responseBuilder.AppendLine(textContent);
            
            string pageUrl = GetPageUrl(pageId);
            responseBuilder.AppendLine();
            responseBuilder.AppendLine($"View in Confluence: {pageUrl}");

            return responseBuilder.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetPageContent: {ex.Message}");
            return $"I encountered an error while retrieving the Confluence page: {ex.Message}";
        }
    }

    [Description("What documentation do we have in Confluence about {topic}?")]
    public async Task<string> GetRelatedPages(
        [Description("What topic would you like to learn more about?")] string topic)
    {
        // This is effectively the same as SearchConfluence but with different messaging
        return await SearchConfluence(topic);
    }

    [Description("What was the last time the Confluence page {pageId} was updated?")]
    public async Task<string> GetPageLastModified(
        [Description("Which page's update time do you want to know?")] string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return "Please provide a valid Confluence page ID.";
        }

        try
        {
            string requestUrl = $"{_confluenceUrl}/rest/api/content/{pageId}?expand=version";
            Console.WriteLine($"Making API request to: {requestUrl}");
            
            var response = await _httpClient.GetAsync(requestUrl);
            Console.WriteLine($"Response status code: {response.StatusCode}");
            
            response.EnsureSuccessStatusCode();
            
            var page = await JsonSerializer.DeserializeAsync<ConfluencePage>(
                await response.Content.ReadAsStreamAsync(),
                _jsonOptions);

            if (page?.Title == null)
            {
                return "Sorry, I couldn't find that page in Confluence.";
            }

            string pageUrl = GetPageUrl(pageId);
            return $"📄 {page.Title}\nLast updated: {FormatRelativeTime(page.Version?.When)}\nView page: {pageUrl}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetPageLastModified: {ex.Message}");
            return $"I encountered an error while retrieving the last modification time: {ex.Message}";
        }
    }

    [Description("Who has recently made changes to the Confluence page {pageId}?")]
    public async Task<string> GetPageContributors(
        [Description("Which page's contributors do you want to see?")] string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return "Please provide a valid Confluence page ID.";
        }

        try
        {
            string requestUrl = $"{_confluenceUrl}/rest/api/content/{pageId}/history?expand=contributors";
            Console.WriteLine($"Making API request to: {requestUrl}");
            
            var response = await _httpClient.GetAsync(requestUrl);
            Console.WriteLine($"Response status code: {response.StatusCode}");
            
            response.EnsureSuccessStatusCode();
            
            using var jsonDoc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var contributors = jsonDoc.RootElement.GetProperty("contributors");
            var publishers = contributors.GetProperty("publishers");

            var responseBuilder = new StringBuilder("Recent contributors to this page:\n\n");
            
            foreach (var publisher in publishers.EnumerateArray())
            {
                var user = publisher.GetProperty("user");
                string displayName = user.GetProperty("displayName").GetString() ?? "Unknown User";
                responseBuilder.AppendLine($"👤 {displayName}");
            }

            string pageUrl = GetPageUrl(pageId);
            responseBuilder.AppendLine($"\nView page: {pageUrl}");

            return responseBuilder.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetPageContributors: {ex.Message}");
            return $"I encountered an error while retrieving page contributors: {ex.Message}";
        }
    }

    [Description("What are the most recently updated pages in Confluence about {topic}?")]
    public async Task<string> GetRecentUpdates(
        [Description("What topic's recent updates would you like to see?")] string topic)
    {
        try
        {
            var oneYearAgo = DateTime.Now.AddYears(-1).ToString("yyyy-MM-dd");
            Console.WriteLine($"Filtering for dates after: {oneYearAgo}");

            // Try v2 API first
            string requestUrl;
            if (string.IsNullOrWhiteSpace(topic))
            {
                // If no topic is provided, just get all recent documents
                requestUrl = $"{_apiBaseUrl}/search?" +
                    $"type=page&" +
                    $"limit=25&" +
                    $"excerpt=highlight&" +
                    $"query={Uri.EscapeDataString($"lastModified >= {oneYearAgo}")}";
                Console.WriteLine("No specific topic provided, searching for all recent documents");
            }
            else
            {
                requestUrl = $"{_apiBaseUrl}/search?" +
                    $"type=page&" +
                    $"limit=25&" +
                    $"excerpt=highlight&" +
                    $"query={Uri.EscapeDataString($"{topic} AND lastModified >= {oneYearAgo}")}";
            }
                
            Console.WriteLine($"Making search API request to: {requestUrl}");
            var response = await _httpClient.GetAsync(requestUrl);

            // If v2 fails, try v1 API with CQL
            if (!response.IsSuccessStatusCode)
            {
                string cql;
                if (string.IsNullOrWhiteSpace(topic))
                {
                    // If no topic is provided, just get all recent documents
                    cql = $"type=page AND lastmodified >= \"{oneYearAgo}\" ORDER BY lastmodified DESC";
                }
                else
                {
                    // Build the CQL query with proper encoding and date filter
                    var quotedQuery = $"\"{topic}\"";
                    cql = $"type=page AND (title ~ {quotedQuery} OR text ~ {quotedQuery}) " +
                         $"AND lastmodified >= \"{oneYearAgo}\" " +
                         $"ORDER BY lastmodified DESC";
                }
                var queryString = $"cql={Uri.EscapeDataString(cql)}&expand=space,version&limit=25";
                requestUrl = $"{_confluenceUrl}/wiki/rest/api/content/search?{queryString}";
                    
                Console.WriteLine($"Retrying with v1 API: {requestUrl}");
                response = await _httpClient.GetAsync(requestUrl);
            }

            Console.WriteLine($"Search response status code: {response.StatusCode}");
            
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                throw new Exception($"API request failed. Status: {response.StatusCode}, Content: {content}");
            }
            
            var responseText = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Raw response: {responseText}");
            
            var searchResponse = await JsonSerializer.DeserializeAsync<SearchResponse>(
                await response.Content.ReadAsStreamAsync(), 
                _jsonOptions);

            if (searchResponse?.Results == null || !searchResponse.Results.Any())
            {
                Console.WriteLine("No search results found");
                if (string.IsNullOrWhiteSpace(topic))
                {
                    return "I couldn't find any pages that have been updated in the last year.";
                }
                else
                {
                    return $"I couldn't find any pages about \"{topic}\" that have been updated in the last year.";
                }
            }

            // Order results by last updated date only, not by space priority
            var orderedResults = searchResponse.Results
                .Where(r => r != null)
                .OrderByDescending(r => r.LastUpdated)
                .ToList();

            var resultBuilder = new StringBuilder();
            if (string.IsNullOrWhiteSpace(topic))
            {
                resultBuilder.AppendLine($"Recently updated pages in Confluence (within the last year):");
            }
            else
            {
                resultBuilder.AppendLine($"Recently updated pages about \"{topic}\" (within the last year):");
            }
            resultBuilder.AppendLine();

            foreach (var result in orderedResults)
            {
                if (result?.Title == null) continue;

                string pageUrl = GetPageUrl(result.Id ?? "", result.Space?.Key);
                string lastUpdated = FormatRelativeTime(result.LastUpdated);
                string spaceName = result.Space?.Name ?? "Unknown Space";
                string spaceKey = result.Space?.Key ?? "UNKNOWN";
                int spacePriority = _spacePriorities.GetValueOrDefault(spaceKey, 0);

                resultBuilder.AppendLine($"📄 {result.Title}");
                resultBuilder.AppendLine($"   Space: {spaceName} ({spaceKey}){(spacePriority > 0 ? $" [Priority: {spacePriority}]" : "")}");
                resultBuilder.AppendLine($"   Last Updated: {lastUpdated}");
                resultBuilder.AppendLine($"   Link: {pageUrl}");
                resultBuilder.AppendLine();
            }

            return resultBuilder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetRecentUpdates: {ex.Message}");
            return $"I encountered an error while retrieving recent updates: {ex.Message}";
        }
    }

    private bool IsWithinLastYear(string relativeTimeText)
    {
        if (string.IsNullOrWhiteSpace(relativeTimeText))
            return false;

        // Parse relative time text
        if (relativeTimeText.Contains("just now") || 
            relativeTimeText.Contains("hour") || 
            relativeTimeText.Contains("day") || 
            relativeTimeText.Contains("week") || 
            relativeTimeText.Contains("month"))
            return true;

        if (relativeTimeText == "last year")
            return true;

        if (relativeTimeText.Contains("years ago"))
            return false;

        return false;
    }

    private string GetPageUrl(string pageId, string? spaceKey = null)
    {
        // Format: https://domain.atlassian.net/wiki/spaces/SPACEKEY/pages/PAGEID
        var domain = new Uri(_confluenceUrl).Host;
        return $"https://{domain}/wiki/spaces/{spaceKey ?? "UNKNOWN"}/pages/{pageId}";
    }

    private static string FormatRelativeTime(DateTime? dateTime)
    {
        if (dateTime == null)
            return "unknown date";

        var now = DateTime.Now;
        var span = now - dateTime.Value;

        if (span.TotalDays < 1)
        {
            if (span.TotalHours < 1)
                return "just now";
            return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
        }
        if (span.TotalDays < 7)
            return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
        if (span.TotalDays < 30)
            return $"{(int)(span.TotalDays / 7)} week{((int)(span.TotalDays / 7) == 1 ? "" : "s")} ago";
        if (span.TotalDays < 365)
            return $"{(int)(span.TotalDays / 30)} month{((int)(span.TotalDays / 30) == 1 ? "" : "s")} ago";

        int years = (int)(span.TotalDays / 365);
        return years == 1 ? "last year" : $"{years} years ago";
    }
}