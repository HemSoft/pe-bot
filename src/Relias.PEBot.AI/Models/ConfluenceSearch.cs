namespace Relias.PEBot.AI.Models;

public class SearchResponse
{
    public List<SearchResult>? Results { get; set; }
    public int? Start { get; set; }
    public int? Limit { get; set; }
    public int? Size { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("_links")]
    public Dictionary<string, string>? Links { get; set; }
}

public class SearchResult
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public SpaceInfo? Space { get; set; }
    public string? Status { get; set; }
    public VersionInfo? Version { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("_links")]
    public Dictionary<string, string>? Links { get; set; }
    
    public DateTime? Created => null; // We need to map this from actual API response if needed
    public DateTime? LastUpdated => Version?.When;
}

public class SpaceInfo
{
    public object? Id { get; set; }  // Can be string or number
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("_links")]
    public Dictionary<string, string>? Links { get; set; }
}

public class VersionInfo
{
    public int? Number { get; set; }
    public DateTime? When { get; set; }
    public string? Message { get; set; }
    public UserInfo? By { get; set; }
    public bool? Hidden { get; set; }
}

public class UserInfo
{
    public string? Id { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("_links")]
    public Dictionary<string, string>? Links { get; set; }
}