
public class SLSKDRequest
{
    public string SpotifyTrackId { get; set; } = string.Empty;
    public string SLSKDSearchId { get; set; } = string.Empty;
    public string SLSKDDownloadUsername { get; set; } = string.Empty;
    public string SLSKDDownloadFilename { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public string[] Artists { get; set; } =  new string[0];
    public int DownloadAttempts { get; set; } = 0;
    public string AlbumName { get; set; } = string.Empty;
    public long Size { get; set; } = 0;
}