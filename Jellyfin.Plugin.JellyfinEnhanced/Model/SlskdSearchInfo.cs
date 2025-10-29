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
    }
}