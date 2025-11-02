using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinEnhanced.Model
{
    public class SlskdFile
    {
        [JsonPropertyName("filename")]
        public required string Filename { get; set; }

        [JsonPropertyName("size")]
        public required long Size { get; set; }

        [JsonPropertyName("isLocked")]
        public required bool IsLocked { get; set; }
    }

    public class SlskdSearchResponse
    {
        [JsonPropertyName("username")]
        public required string Username { get; set; }

        [JsonPropertyName("fileCount")]
        public required int FileCount { get; set; }

        [JsonPropertyName("lockedFileCount")]
        public required int LockedFileCount { get; set; }

        [JsonPropertyName("hasFreeUploadSlot")]
        public required bool HasFreeUploadSlot { get; set; }

        [JsonPropertyName("files")]
        public required List<SlskdFile> Files { get; set; }

        [JsonPropertyName("uploadSpeed")]
        public required int UploadSpeed { get; set; }
    }
    public class SlskdDownloadRequest
    {
        // Use the JsonPropertyName attribute for lowercase property names if required
        [JsonPropertyName("filename")]
        public required string Filename { get; set; }

        [JsonPropertyName("size")]
        public required long Size { get; set; }

        // You could also add the 'username' here if needed for the serialized list
    }
    public class SlskdDownloadFileInfo
    {
        [JsonPropertyName("filename")]
        public required string Filename { get; set; }

        [JsonPropertyName("size")]
        public required long Size { get; set; }

        [JsonPropertyName("direction")]
        public required string Direction { get; set; }

        [JsonPropertyName("state")]
        public required string State { get; set; }
        [JsonPropertyName("id")]
        public required string Id { get; set; }

    }
    public class SlskdDownloadDirectoryInfo
    {
        [JsonPropertyName("directory")]
        public required string Directory { get; set; }

        [JsonPropertyName("fileCount")]
        public required int FileCount { get; set; }

        [JsonPropertyName("files")]
        public required List<SlskdDownloadFileInfo> Files { get; set; }
    }
    public class SlskdDownloadInfo
    {
        [JsonPropertyName("username")]
        public required string Username { get; set; }

        [JsonPropertyName("directories")]
        public required List<SlskdDownloadDirectoryInfo> Directories { get; set; }
    }
    public class SpotifyImage
    {
        [JsonPropertyName("url")]
        public required string Url { get; set; }

        [JsonPropertyName("height")]
        public required long Height { get; set; }
        [JsonPropertyName("width")]
        public required long Width { get; set; }
    }
    public class SpotifyArtist
    {
        [JsonPropertyName("genres")]
        public required string[] Genres { get; set; }

        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("images")]
        public required SpotifyImage[] Images { get; set; }

        [JsonPropertyName("name")]
        public required string Name { get; set; }
    }
    public class SpotifyMultipleArtistsResponse
    {
        [JsonPropertyName("artists")]
        public required List<SpotifyArtist> Artists { get; set; }
    }
    public class SlskdRequest
    {
        public string SpotifyTrackId { get; set; } = string.Empty;
        public string SLSKDSearchId { get; set; } = string.Empty;
        public string SLSKDDownloadUsername { get; set; } = string.Empty;
        public string SLSKDDownloadFilename { get; set; } = string.Empty;
        public string YoutubeDownloadId { get; set; } = string.Empty;
        public string TrackName { get; set; } = string.Empty;
        public string[] Artists { get; set; } = new string[0];
        public string[] AlbumArtists { get; set; } = new string[0];
        public string ImageUrl { get; set; } = string.Empty;
        public int DownloadAttempts { get; set; } = 0;
        public string AlbumName { get; set; } = string.Empty;
        public long Size { get; set; } = 0;
    }

    public class SlskdSearchInfo
    {
        [JsonPropertyName("fileCount")]
        public required int FileCount { get; set; }

        [JsonPropertyName("lockedFileCount")]
        public required int LockedFileCount { get; set; }

        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("isComplete")]
        public required bool IsComplete { get; set; }

        [JsonPropertyName("searchText")]
        public required string SearchText { get; set; }

        [JsonPropertyName("state")]
        public required string State { get; set; }

        [JsonPropertyName("responses")]
        public List<SlskdSearchResponse>? Responses { get; set; }
        [JsonPropertyName("startedAt")]
        public required DateTimeOffset StartedAt { get; set; }
    }

    public class MetubeFileInfo
    {
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("filename")]
        public required string Filename { get; set; }
    }
    
    public class MetubeHistoryInfo
    {
        [JsonPropertyName("done")]
        public required MetubeFileInfo[] Done { get; set; }
    }
}