namespace Limescape.Umbraco.AgentApi.Services;

public sealed class DetailPageOptions
{
    public bool Enabled { get; set; }
    public int DownloadLifetimeSeconds { get; set; } = 600;
    /// <summary>HTTPS account service URI. Empty selects the local signed stream endpoint.</summary>
    public string BlobServiceUri { get; set; } = string.Empty;
    public string BlobContainer { get; set; } = "media";
    /// <summary>
    /// Folder inside <see cref="BlobContainer"/> under which the media provider stores files (Umbraco's
    /// <c>ContainerRootPath</c>). Empty: the path of the media URL (<c>/media/abc/file.pdf</c> becomes blob
    /// <c>media/abc/file.pdf</c>), which is the provider's default (root path = virtual path).
    /// </summary>
    public string BlobRootPath { get; set; } = string.Empty;
    /// <summary>Relation types shown on the detail page and in MaterialDetail, and the only ones the Agent API writes.</summary>
    public List<string> RelationTypes { get; set; } = ["lsRelated", "lsPartOf", "lsVersionOf"];
}
