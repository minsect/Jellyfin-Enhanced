using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Jellyfin.Plugin.JellyfinEnhanced.Model;
using TagLib;
using System.Xml.Linq;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Pagination;
using YouTubeMusicAPI.Models.Search;
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
        
        // https://www.dotnetperls.com/levenshtein thank you Sam Allen
        private static int LevenshteinDifference(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            // Verify arguments.
            if (n == 0)
            {
                return m;
            }

            if (m == 0)
            {
                return n;
            }

            // Initialize arrays.
            for (int i = 0; i <= n; d[i, 0] = i++)
            {
            }

            for (int j = 0; j <= m; d[0, j] = j++)
            {
            }

            // Begin looping.
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    // Compute cost.
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
                }
            }
            // Return cost.
            return d[n, m];
        }

        // Copy pasted from JellyfinEnhancedController, will (maybe) clean up later
        private async Task<HttpResponseMessage?> ProxySLSKDRequest(string apiPath, HttpMethod method, string? content = null)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.SpotifySearchSLSKDUrls) || string.IsNullOrEmpty(config.SpotifySearchSLSKDKey) || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory.TrimEnd('/')))
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
        private async Task<HttpResponseMessage?> MetubeRequest(string apiPath, HttpMethod method, string? content = null)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || config.MetubeDownloaderEnabled == false)
            {
                //_logger.Warning("Spotify SLSKD integration is not configured or enabled.");
                return null;
            }

            string[] urls = config.MetubeUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var httpClient = _httpClientFactory.CreateClient();

            foreach (string url in urls)
            {
                string trimmedUrl = url.Trim();
                try
                {
                    string requestUri = $"{trimmedUrl.TrimEnd('/')}/{apiPath}";

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
                    _logger.LogError($"Failed to connect to MeTube URL: {trimmedUrl}. Error: {ex.Message}");
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
            if (string.IsNullOrEmpty(filename)) { return ""; }
            // getting file extension (extension property in file doesn't always return the extension)
            int lastDotIndex = filename.LastIndexOf('.');
            if (lastDotIndex >= 0 && lastDotIndex < filename.Length - 1)
            {
                return filename.Substring(lastDotIndex + 1).ToLowerInvariant();
            }
            return "";
        }

        public async Task<string?> DownloadImageAsync(string imageUrl, string filePathBase)
        {
            string extension = ".jpg"; // Default fallback extension
            
            using (var client = new HttpClient())
            {
                // 1. Determine the file extension by checking Content-Type headers via HEAD request
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Head, imageUrl))
                    using (var response = await client.SendAsync(request))
                    {
                        response.EnsureSuccessStatusCode();

                        if (response.Content.Headers.TryGetValues("Content-Type", out var values))
                        {
                            string mimeType = values.FirstOrDefault() ?? string.Empty;

                            extension = mimeType.ToLowerInvariant() switch
                            {
                                "image/jpeg" => ".jpg",
                                "image/png" => ".png",
                                "image/gif" => ".gif",
                                "image/webp" => ".webp",
                                _ => ".jpg", // Fallback to JPEG
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error determining image type for {imageUrl}: {ex.Message}");
                    // Use default extension (.jpg) and continue
                }

                // 2. Construct the final file path with the determined extension
                string finalFilePath = filePathBase + extension;

                // 3. Download the actual image content using the same HttpClient
                try
                {
                    using (var stream = await client.GetStreamAsync(imageUrl))
                    {
                        // 4. Save to the file path
                        using (var fileStream = new FileStream(finalFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await stream.CopyToAsync(fileStream);
                            _logger.LogInformation($"Successfully downloaded image to {finalFilePath}");
                            return finalFilePath; // Return the path on success
                        }
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError($"Error downloading image from {imageUrl}: {httpEx.Message}");
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error saving file to {finalFilePath}: {ex.Message}");
                    return null;
                }
            }
        }

        private static readonly Regex InvalidFileNameChars = new Regex(@"[<>:""/\\|?*]", RegexOptions.Compiled);

        private static string SanitizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }
            return InvalidFileNameChars.Replace(input, "");
        }
        
        public static string NormalizeTitle(string title)
        {
            title = title.ToLowerInvariant();
            title = Regex.Replace(title, @"\s?[\(\[].*?[\)\]]", string.Empty);
            title = Regex.Replace(title, @"\s?[-_]\s?(instrumental|remix|edit)", string.Empty);

            return title.Trim();
        }

        private async Task CreateOrUpdateArtistDirectory(SpotifyArtist artist)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory)) return;
            string artistPath = Path.Combine(config.SpotifySearchMusicDirectory, SanitizeString(artist.Name));
            Directory.CreateDirectory(artistPath);
            string? artistArtPath = null;
            if (artist.Images[0] != null)
            {
                artistArtPath = await DownloadImageAsync(artist.Images[0].Url, Path.Combine(artistPath, "folder"));
            }
            var nfoDocument = new XDocument(
            new XElement("artist",
                new XElement("name", artist.Name),
                new XElement("sortname", artist.Name), // For sorting purposes
                new XElement("spotifyid", artist.Id),
                (artist.Genres ?? Enumerable.Empty<string>()).Select(g => new XElement("genre", g)),
                //new XElement("album", "Album Placeholder"),
                //new XElement("genre", artist.Genres),
                new XElement("thumb", artist.Images[0].Url)
            )
        );

        // 3. Save the XML document to the file system asynchronously
        await using (var stream = new FileStream(Path.Combine(artistPath, "artist.nfo"), FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            nfoDocument.Save(stream);
        }
        }
        
        /*private static void CreateOrUpdateAlbumDirectory(SlskdRequest request)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory.TrimEnd('/'))) return;
            CreateOrUpdateArtistDirectory(request);
            string mainArtistName = SanitizeString(request.Artists[0]);
            string albumPath = $"{config.SpotifySearchMusicDirectory.TrimEnd('/')}/{mainArtistName}/{SanitizeString(request.AlbumName)}";
            Directory.CreateDirectory(albumPath);

        }*/

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
                if (config == null || string.IsNullOrEmpty(config.SpotifySearchSLSKDUrls) || string.IsNullOrEmpty(config.SpotifySearchSLSKDKey) || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory.TrimEnd('/')))
                {
                    _logger.LogInformation("Spotify SLSKD integration is not configured or enabled.");
                    await Task.Delay(4000, stoppingToken);
                    continue;
                }

                // address the artist situation
                foreach (SpotifyArtist artist in _store.GetAllArtists())
                {
                    _logger.LogInformation($"artist attempting to be added: {artist.Name}");
                    await CreateOrUpdateArtistDirectory(artist);
                    _store.TryRemoveArtist(artist.Id);
                }

                var SLSKDSearches = await ProxySLSKDRequest("searches", HttpMethod.Get);
                if (SLSKDSearches != null && SLSKDSearches.IsSuccessStatusCode) {
                    List<SlskdSearchInfo>? SLSKDSearchesDoc = JsonSerializer.Deserialize<List<SlskdSearchInfo>>(await SLSKDSearches.Content.ReadAsStringAsync()); // this is an array
                    if (SLSKDSearchesDoc == null) continue;
                    foreach (SlskdSearchInfo search in SLSKDSearchesDoc)
                    {
                        var searchId = search.Id;
                        if (searchId == null) continue;
                        if (_store.TryGetRequest(searchId, out var request)) {
                            var isSearchComplete = search.IsComplete;
                            if (!string.IsNullOrEmpty(request.YoutubeDownloadId))
                            {
                                HttpResponseMessage? downloadRequestInformation = await MetubeRequest("history", HttpMethod.Get);
                                if (downloadRequestInformation == null || !downloadRequestInformation.IsSuccessStatusCode)
                                {
                                    _logger.LogError($"Failed to get download information for youtube {request.YoutubeDownloadId}");
                                    continue;
                                }
                                MetubeHistoryInfo? meTubeInfo = JsonSerializer.Deserialize<MetubeHistoryInfo>(await downloadRequestInformation.Content.ReadAsStringAsync());
                                if (meTubeInfo == null)
                                {
                                    _logger.LogError($"Failed to get MewTube history information for request {request.YoutubeDownloadId}");
                                    continue;
                                }
                                MetubeFileInfo? metubeFileInfo = meTubeInfo.Done.FirstOrDefault(item => item.Id == request.YoutubeDownloadId);
                                if (metubeFileInfo != null)
                                {
                                    _logger.LogInformation($"Download for {request.TrackName} is successful!");
                                    string mainArtistName = request.Artists[0] ?? "";
                                    string filePath = Path.Combine(config.MetubeDownloadDirectory, metubeFileInfo.Filename);
                                    //string filePath = $"{config.MetubeDownloadDirectory.TrimEnd('/')}/{metubeFileInfo.Filename}";
                                    string outputDirectory = Path.Combine(config.SpotifySearchMusicDirectory, SanitizeString(mainArtistName), SanitizeString(request.AlbumName.Trim()));
                                    string outputPath = Path.Combine(outputDirectory, SanitizeString($"{request.TrackName}.{GetExtension(metubeFileInfo.Filename)}"));
                                    try
                                    {
                                        byte[] imageBytes;
                                        using (var client = new HttpClient())
                                        {
                                            try
                                            {
                                                _logger.LogInformation($"trying to download {request.ImageUrl}");
                                                imageBytes = await client.GetByteArrayAsync(request.ImageUrl);
                                            }
                                            catch (HttpRequestException ex)
                                            {
                                                _logger.LogError($"Error downloading album image: {ex.Message}");
                                                return;
                                            }
                                        }
                                        TagLib.File audioFile = TagLib.File.Create(filePath);
                                        audioFile.Tag.Clear();
                                        audioFile.Tag.AlbumArtists = request.AlbumArtists;
                                        audioFile.Tag.Performers = request.Artists;
                                        audioFile.Tag.Album = request.AlbumName;
                                        if (imageBytes != null)
                                        {
                                            audioFile.Tag.Pictures = [new Picture(new ByteVector(imageBytes))];
                                        }
                                        audioFile.Save();
                                        Directory.CreateDirectory(outputDirectory);
                                        await DownloadImageAsync(request.ImageUrl, Path.Combine(outputDirectory, "cover"));
                                        System.IO.File.Move(filePath, outputPath);
                                        _logger.LogInformation($"Successfully saved file for {request.TrackName} to {outputPath}");
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError($"Error moving file for {request.TrackName} to {outputPath}: {ex.Message}");
                                    }
                                    _store.TryRemoveRequest(searchId);
                                }

                            }
                            else if (!string.IsNullOrEmpty(request.SLSKDDownloadUsername) && !string.IsNullOrEmpty(request.SLSKDDownloadFilename))
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

                                    string filePath = Path.Combine(config.SpotifySearchSLSKDDownloadsDirectory, pathDisassembled[^2], pathDisassembled[^1]);
                                    string outputDirectory = Path.Combine(config.SpotifySearchMusicDirectory, SanitizeString(mainArtistName), SanitizeString(request.AlbumName.Trim()));
                                    string outputPath = Path.Combine(outputDirectory, SanitizeString($"{request.TrackName}.{GetExtension(request.SLSKDDownloadFilename)}"));
                                    try
                                    {
                                        byte[] imageBytes;
                                        using (var client = new HttpClient())
                                        {
                                            try
                                            {
                                                _logger.LogInformation($"trying to download {request.ImageUrl}");
                                                imageBytes = await client.GetByteArrayAsync(request.ImageUrl);
                                            }
                                            catch (HttpRequestException ex)
                                            {
                                                _logger.LogError($"Error downloading album image: {ex.Message}");
                                                return;
                                            }
                                        }
                                        TagLib.File audioFile = TagLib.File.Create(filePath);
                                        audioFile.Tag.Clear();
                                        audioFile.Tag.AlbumArtists = request.AlbumArtists;
                                        audioFile.Tag.Performers = request.Artists;
                                        audioFile.Tag.Album = request.AlbumName;
                                        if (imageBytes != null)
                                        {
                                            audioFile.Tag.Pictures = [new Picture(new ByteVector(imageBytes))];
                                        }
                                        audioFile.Save();
                                        Directory.CreateDirectory(outputDirectory);
                                        await DownloadImageAsync(request.ImageUrl, Path.Combine(outputDirectory, "cover"));
                                        System.IO.File.Move(filePath, outputPath);
                                        _logger.LogInformation($"Successfully saved file for {request.TrackName} to {outputPath}");
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError($"Error moving file for {request.TrackName} to {outputPath}: {ex.Message}");
                                    }
                                    _store.TryRemoveRequest(searchId);
                                }
                                else if (fileInfo.State.Contains("Completed"))
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
                                if (SearchResponses == null || !SearchResponses.IsSuccessStatusCode)
                                {
                                    _logger.LogError($"Failed to get search responses for search ID {searchId}");
                                    continue;
                                }
                                SlskdSearchInfo? searchRoot = JsonSerializer.Deserialize<SlskdSearchInfo>(await SearchResponses.Content.ReadAsStringAsync());
                                if (searchRoot == null || searchRoot.Responses == null)
                                {
                                    _logger.LogError($"Failed to deserialize search responses for search ID {searchId}");
                                    continue;
                                }
                                var fileCount = searchRoot.FileCount - searchRoot.LockedFileCount;
                                if (fileCount <= 0)
                                {
                                    _logger.LogInformation($"No available files found for search ID {searchId}, removing request");
                                    //_store.TryRemoveRequest(searchId);
                                    //continue;
                                }
                                string mainArtistName = request.Artists[0] ?? "";
                                if (fileCount >= 0)
                                {
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
                                            NormalizeTitle(request.TrackName).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => NormalizeTitle(Path.GetFileNameWithoutExtension(item.File.Filename)).Contains(word)) &&
                                            item.File.Filename.ToLower().Contains(mainArtistName.ToLower())
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
                                        request.SLSKDDownloadFilename = item.File.Filename;
                                        request.SLSKDDownloadUsername = item.Username;
                                        request.Size = item.File.Size;
                                        foundFile = true;
                                        _logger.LogInformation($"{search.SearchText} search complete! downloading it now...");
                                        break;
                                    }
                                    if (foundFile == true) continue;
                                }
                                // below is logic for when the song isnt found on SLSKD and MeTube is provided
                                if (config.MetubeDownloaderEnabled == true)
                                {
                                    YouTubeMusicClient ytclient = new(_logger, "US");

                                    _logger.LogInformation($"No suitable files found for search ID {searchId}, searching youtube music with prompt {request.Artists[0]} {request.TrackName}");
                                    PaginatedAsyncEnumerable<SearchResult> searchResults = ytclient.SearchAsync($"{request.Artists[0]} {request.TrackName}", SearchCategory.Songs);
                                    IReadOnlyList<SearchResult> bufferedSearchResults = await searchResults.FetchItemsAsync(0, 20);
                                    bool foundYoutube = false;
                                    foreach (SongSearchResult song in bufferedSearchResults.Cast<SongSearchResult>())
                                    {
                                        if (song.Artists.Any(artist => artist.Name.Equals(request.Artists[0], StringComparison.OrdinalIgnoreCase)) && NormalizeTitle(song.Name) == NormalizeTitle(request.TrackName))
                                        {

                                            // the song was found!
                                            _logger.LogInformation($"Youtube music found: {song.Name}, {string.Join(", ", song.Artists.Select(artist => artist.Name))}");
                                            HttpResponseMessage? metubeQuery = await MetubeRequest("add", HttpMethod.Post, JsonSerializer.Serialize(new
                                            {
                                                url = $"https://music.youtube.com/watch?v={song.Id}",
                                                quality = "best",
                                                format = "flac"
                                            }));
                                            if (metubeQuery != null && metubeQuery.IsSuccessStatusCode)
                                            {
                                                foundYoutube = true;
                                                request.YoutubeDownloadId = song.Id;
                                                break;
                                            }
                                        }
                                    }
                                    if (foundYoutube == false)
                                    {
                                        // impossible!!! youtube music has everything
                                        // believe in yourself more (fuzzy search)
                                        var songsFuzzyMatch = bufferedSearchResults.Cast<SongSearchResult>()
                                            .OrderBy(song => LevenshteinDifference(song.Name, request.TrackName));
                                        // trust in the process bro, just pick the first song
                                        SongSearchResult? song = songsFuzzyMatch.FirstOrDefault();
                                        if (song != null)
                                        {
                                            HttpResponseMessage? metubeQuery = await MetubeRequest("add", HttpMethod.Post, JsonSerializer.Serialize(new
                                            {
                                                url = $"https://music.youtube.com/watch?v={song.Id}",
                                                quality = "best",
                                                format = "flac"
                                            }));
                                            if (metubeQuery != null && metubeQuery.IsSuccessStatusCode)
                                            {
                                                _logger.LogInformation($"Youtube music fuzzy found: {song.Name}, {string.Join(", ", song.Artists.Select(artist => artist.Name))}");
                                                request.YoutubeDownloadId = song.Id;
                                            }
                                        }
                                        else
                                        {
                                            // maybe we were wrong...
                                            _store.TryRemoveRequest(searchId);
                                            _logger.LogInformation($"No suitable youtube songs found for search ID {searchId}, This request is cancelled.");
                                        }
                                    }
                                } else
                                {
                                    _logger.LogInformation($"No suitable files found for search ID {searchId}, This request is cancelled.");
                                    _store.TryRemoveRequest(searchId);
                                }
                                
                                continue;
                            }
                            else if (!isSearchComplete)
                            {
                                if (search.FileCount - search.LockedFileCount > 20 && (DateTimeOffset.UtcNow - search.StartedAt).TotalSeconds >= 4)
                                {
                                    // force completion if more than 30 files and 4 seconds passed since the search
                                    await ProxySLSKDRequest($"searches/{searchId}", HttpMethod.Put);
                                }
                            }
                        }
                    }
                }
                await Task.Delay(500, stoppingToken);
            }
        }
    }
}