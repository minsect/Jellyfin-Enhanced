using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using Jellyfin.Plugin.JellyfinEnhanced.Helpers;
using Jellyfin.Plugin.JellyfinEnhanced.JellyfinVersionSpecific;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.JellyfinEnhanced.Model;

namespace Jellyfin.Plugin.JellyfinEnhanced.Services
{
    public class SLSKDService : BackgroundService
    {
        private readonly ILogger<SLSKDService> _logger;
        private readonly IApplicationPaths _applicationPaths;
        private readonly SLSKDStore _store;
        private readonly IHttpClientFactory _httpClientFactory;

        public SLSKDService(SLSKDStore store, ILogger<SLSKDService> logger, IApplicationPaths applicationPaths, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _applicationPaths = applicationPaths;
            _store = store;
            _httpClientFactory = httpClientFactory;
        }
        // Copy pasted from JellyfinEnhancedController, will (maybe) clean up later
        private async Task<HttpResponseMessage?> ProxySLSKDRequest(string apiPath, HttpMethod method, string? content = null)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.SpotifySearchSLSKDUrls) || string.IsNullOrEmpty(config.SpotifySearchSLSKDKey) || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory))
            {
                //_logger.Warning("Spotify SLSKD integration is not configured or enabled.");
                return null;
            }

            string[] urls = config.SpotifySearchSLSKDUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.SpotifySearchSLSKDKey);

            foreach (string url in urls)
            {
                string trimmedUrl = url.Trim();
                try
                {
                    string requestUri = $"{trimmedUrl.TrimEnd('/')}/api/v0/{apiPath}";
                    //_logger.Info($"Proxying SLSKD request to: {requestUri}");

                    HttpRequestMessage request = new HttpRequestMessage(method, requestUri);
                    if (content != null)
                    {
                        //_logger.Info($"Request body: {content}");
                        request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                    }

                    var response = await httpClient.SendAsync(request);
                    return response;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to connect to SLSKD URL: {trimmedUrl}. Error: {ex.Message}");
                    //_logger.Error($"Failed to connect to SLSKD URL: {trimmedUrl}. Error: {ex.Message}");
                }
            }

            return null;
        }
        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "flac", "ogg", "wav", "m4a", "aac", "alac", "dsf", "aiff", "wma"
        };
        private static string GetExtension(string? filename)
        {
            if (string.IsNullOrEmpty(filename)) {return "";}
            // getting file extension (extension property in file doesn't always return the extension)
            int lastDotIndex = filename.LastIndexOf('.');
            if (lastDotIndex >= 0 && lastDotIndex < filename.Length - 1)
            {
                return filename.Substring(lastDotIndex + 1).ToLowerInvariant();
            }
            return "";
        }
        private static int GetExtensionPriority(string? filename)
        {
            string extension = GetExtension(filename);
            if (string.IsNullOrEmpty(extension)) {return 99;}    
            return extension switch
            {
                "flac" => 1,
                "ogg" => 2,
                "wav" => 3,
                _ => 99
            };
        }

        protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested) {
                var requests = _store.GetAllRequests();

                if (requests.Count == 0)
                {
                    _logger.LogInformation("Waiting until requests are made to SLSKD");

                    await _store.WaitForItemsAsync(stoppingToken); // now we wait until there is something to actually process
                    _logger.LogInformation("Received request, resuming");
                    continue;
                }
                var config = JellyfinEnhanced.Instance?.Configuration;
                if (config == null || string.IsNullOrEmpty(config.SpotifySearchSLSKDUrls) || string.IsNullOrEmpty(config.SpotifySearchSLSKDKey) || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory))
                {
                    _logger.LogInformation("Spotify SLSKD integration is not configured or enabled.");
                    await Task.Delay(4000, stoppingToken);
                    continue;
                }
                var SLSKDSearches = await ProxySLSKDRequest("searches", HttpMethod.Get);
                if (SLSKDSearches != null && SLSKDSearches.IsSuccessStatusCode) {
                    var SLSKDSearchesDoc = JsonDocument.Parse(await SLSKDSearches.Content.ReadAsStringAsync()); // this is an array
                    foreach (var search in SLSKDSearchesDoc.RootElement.EnumerateArray())
                    {
                        var searchId = search.GetProperty("id").GetString();
                        if (searchId == null) continue;
                        if (_store.TryGetRequest(searchId, out var request)) {
                            var isSearchComplete = search.GetProperty("isComplete").GetBoolean();
                            if (!string.IsNullOrEmpty(request.SLSKDDownloadUsername) && !string.IsNullOrEmpty(request.SLSKDDownloadFilename))
                            {
                                // this file is downloading!!!!
                                var downloadRequestInformation = await ProxySLSKDRequest($"transfers/downloads/{request.SLSKDDownloadUsername}", HttpMethod.Get);
                                if (downloadRequestInformation == null || !downloadRequestInformation.IsSuccessStatusCode)
                                {
                                    _logger.LogError($"Failed to get download information for user {request.SLSKDDownloadUsername}");
                                    continue;
                                }
                                SlskdDownloadInfo? downloadInfo = JsonSerializer.Deserialize<SlskdDownloadInfo>(await downloadRequestInformation.Content.ReadAsStringAsync());
                                if (downloadInfo == null)
                                {
                                    _logger.LogError($"Failed to get deserialize download information for request {searchId}");
                                    continue;
                                }
                                SlskdDownloadFileInfo? fileInfo = downloadInfo.Directories
                                    .SelectMany(directory => directory.Files)
                                    .Where(file => file.Direction == "Download" && file.Filename == request.SLSKDDownloadFilename)
                                    .FirstOrDefault();
                                if (fileInfo == null)
                                {
                                    _logger.LogError($"Failed to find a download request for {searchId}, filename {request.SLSKDDownloadFilename}");
                                    _logger.LogError(JsonSerializer.Serialize(downloadInfo.Directories));
                                    _store.TryRemoveRequest(searchId);
                                    continue;
                                }
                                if (fileInfo.State == "Completed, Succeeded")
                                {
                                    _logger.LogInformation($"Download for {request.TrackName} is successful!");
                                    string mainArtistName = request.Artists[0] ?? "";
                                    string[] pathDisassembled = request.SLSKDDownloadFilename.Split("\\");

                                    string filePath = $"{config.SpotifySearchSLSKDDownloadsDirectory}/{pathDisassembled[^2]}/{pathDisassembled[^1]}";
                                    string outputPath = $"{config.SpotifySearchMusicDirectory}/{mainArtistName}/{request.AlbumName.Trim()}/{request.TrackName}.{GetExtension(request.SLSKDDownloadFilename)}";
                                    string? outputDirectory = Path.GetDirectoryName(outputPath);
                                    if (outputDirectory != null)
                                    {
                                        try
                                        {
                                            Directory.CreateDirectory(outputDirectory);
                                            File.Move(filePath, outputPath);
                                            _logger.LogInformation($"Successfully saved file for {request.TrackName} to {outputPath}");
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError($"Error moving file for {request.TrackName} to {outputPath}: {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogInformation($"Failed to get directory for output path: {outputPath}");
                                    }
                                    _store.TryRemoveRequest(searchId);
                                } else if (fileInfo.State.Contains("Completed"))
                                {
                                    // do retry stuff if it was "completed" but not successful
                                    if (request.DownloadAttempts >= 3)
                                    {
                                        _logger.LogInformation($"Download for {request.TrackName} has failed too many times, cancelling request");
                                        _store.TryRemoveRequest(searchId);
                                        continue;
                                    }
                                    // retry download
                                    await ProxySLSKDRequest($"transfers/downloads/{request.SLSKDDownloadUsername}/{fileInfo.Id}", HttpMethod.Delete);
                                    List<SlskdDownloadRequest> downloadRequests = new List<SlskdDownloadRequest>
                                    {
                                        new SlskdDownloadRequest { Filename = request.SLSKDDownloadFilename, Size = request.Size }
                                    };
                                    var download = await ProxySLSKDRequest($"transfers/downloads/{Uri.EscapeDataString(request.SLSKDDownloadUsername)}", HttpMethod.Post, JsonSerializer.Serialize(downloadRequests));
                                    await ProxySLSKDRequest($"transfers/downloads/{Uri.EscapeDataString(request.SLSKDDownloadUsername)}", HttpMethod.Post, JsonSerializer.Serialize(downloadRequests));
                                    request.DownloadAttempts = request.DownloadAttempts + 1;
                                }
                            } 
                            else if (isSearchComplete) 
                            {
                                // we must download something or discard this if theres no good match
                                var SearchResponses = await ProxySLSKDRequest($"searches/{searchId}?includeResponses=true", HttpMethod.Get);
                                if (SearchResponses == null || !SearchResponses.IsSuccessStatusCode) {
                                    _logger.LogError($"Failed to get search responses for search ID {searchId}");
                                    continue;
                                }
                                SlskdSearchInfo? searchRoot = JsonSerializer.Deserialize<SlskdSearchInfo>(await SearchResponses.Content.ReadAsStringAsync());
                                if (searchRoot == null || searchRoot.Responses == null) {
                                    _logger.LogError($"Failed to deserialize search responses for search ID {searchId}");
                                    continue;
                                }
                                var fileCount = searchRoot.FileCount - searchRoot.LockedFileCount;
                                if (fileCount <= 0)
                                {
                                    _logger.LogInformation($"No available files found for search ID {searchId}, removing request");
                                    _store.TryRemoveRequest(searchId);
                                    continue;
                                }

                                string mainArtistName = request.Artists[0] ?? "";
                                var files = searchRoot.Responses
                                    .Where(response => response.HasFreeUploadSlot == true)
                                    .SelectMany(response => response.Files.Select(file => new
                                    {
                                        Username = response.Username,
                                        File = file
                                    }))
                                    .Where(item =>
                                        item.File.IsLocked == false &&
                                        AudioExtensions.Contains(GetExtension(item.File.Filename)) &&
                                        item.File.Size < 2000000000 &&
                                        item.File.Filename.Contains(request.TrackName) &&
                                        item.File.Filename.Contains(mainArtistName)
                                    )
                                    .OrderBy(item => GetExtensionPriority(item.File.Filename));
                                // first file to appear will be downloaded
                                bool foundFile = false;
                                foreach (var item in files)
                                {
                                    _logger.LogInformation($"Trying {item.Username}...");
                                    if (item == null) continue;
                                    // check if the user is alive
                                    var userStatusResponse = await ProxySLSKDRequest($"users/{Uri.EscapeDataString(item.Username)}/status", HttpMethod.Get);
                                    if (userStatusResponse == null || !userStatusResponse.IsSuccessStatusCode) continue;
                                    if (JsonDocument.Parse(await userStatusResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("presence").GetString() == "Offline") continue;
                                    _logger.LogInformation($"User {item.Username} is online, proceeding");
                                    // download
                                    List<SlskdDownloadRequest> downloadRequests = new List<SlskdDownloadRequest>
                                    {
                                        new SlskdDownloadRequest { Filename = item.File.Filename, Size = item.File.Size }
                                    };
                                    var download = await ProxySLSKDRequest($"transfers/downloads/{Uri.EscapeDataString(item.Username)}", HttpMethod.Post, JsonSerializer.Serialize(downloadRequests));

                                    string fileString = JsonSerializer.Serialize(item);
                                    _logger.LogInformation($"Files found for search ID {searchId}");
                                    // remove request for now
                                    request.SLSKDDownloadFilename = item.File.Filename;
                                    request.SLSKDDownloadUsername = item.Username;
                                    request.Size = item.File.Size;
                                    /*_store.UpdateRequest(searchId, new SLSKDRequest
                                    {
                                        SpotifyTrackId = request.SpotifyTrackId,
                                        SLSKDSearchId = request.SLSKDSearchId,
                                        SLSKDDownloadUsername = item.Username,
                                        SLSKDDownloadFilename = item.File.Filename,
                                        TrackName = request.TrackName,
                                        Artists = request.Artists,
                                        AlbumName = request.AlbumName,
                                        Size = request.Size,
                                    });*/
                                    foundFile = true;
                                    _logger.LogInformation($"{search.GetProperty("searchText").GetString()} search complete! downloading it now...");
                                    break;
                                }
                                if (foundFile == true) continue;
                                _logger.LogInformation($"No suitable files found for search ID {searchId}, cancelling request");
                                _store.TryRemoveRequest(searchId);
                                continue;
                            }
                        }
                    }
                }
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}