namespace Relias.PEBot.AI.Models;

public class ConfluencePage
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public BodyContent? Body { get; set; }
    public VersionInfo? Version { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("_links")]
    public Dictionary<string, string>? Links { get; set; }
}

public class BodyContent
{
    public StorageContent? Storage { get; set; }
    public ViewContent? View { get; set; }  // Some Confluence responses include view format as well
}

public class StorageContent
{
    public string? Value { get; set; }
    public string? Representation { get; set; }  // Usually "storage"
}

public class ViewContent
{
    public string? Value { get; set; }
    public string? Representation { get; set; }  // Usually "view"
}