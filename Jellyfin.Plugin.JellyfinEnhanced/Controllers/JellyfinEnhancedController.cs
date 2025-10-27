using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellyfinEnhanced.Controllers
{
    public class JellyseerrUser
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("jellyfinUserId")]
        public string? JellyfinUserId { get; set; }
    }

    [Route("JellyfinEnhanced")]
    [ApiController]
    public class JellyfinEnhancedController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Logger _logger;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;
        private readonly IMemoryCache _memoryCache;
        public JellyfinEnhancedController(IHttpClientFactory httpClientFactory, Logger logger, IUserManager userManager, IUserDataManager userDataManager, ILibraryManager libraryManager, IDtoService dtoService, IMemoryCache memoryCache)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _dtoService = dtoService;
            _memoryCache = memoryCache;
        }

        private async Task<string?> GetJellyseerrUserId(string jellyfinUserId)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.JellyseerrUrls) || string.IsNullOrEmpty(config.JellyseerrApiKey))
            {
                _logger.Warning("Jellyseerr configuration is missing. Cannot look up user ID.");
                return null;
            }

            _logger.Info($"Attempting to find Jellyseerr user for Jellyfin User ID: {jellyfinUserId}");
            var urls = config.JellyseerrUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.JellyseerrApiKey);

            foreach (var url in urls)
            {
                try
                {
                    var requestUri = $"{url.Trim().TrimEnd('/')}/api/v1/user?take=1000"; // Fetch all users to find a match
                    _logger.Info($"Requesting users from Jellyseerr URL: {requestUri}");
                    var response = await httpClient.GetAsync(requestUri);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var usersResponse = JsonSerializer.Deserialize<JsonElement>(content);
                        if (usersResponse.TryGetProperty("results", out var usersArray))
                        {
                            var users = JsonSerializer.Deserialize<List<JellyseerrUser>>(usersArray.ToString());
                            _logger.Info($"Found {users?.Count ?? 0} users at {url.Trim()}");
                            var user = users?.FirstOrDefault(u => string.Equals(u.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));
                            if (user != null)
                            {
                                _logger.Info($"Found Jellyseerr user ID {user.Id} for Jellyfin user ID {jellyfinUserId} at {url.Trim()}");
                                return user.Id.ToString();
                            }
                            else
                            {
                                _logger.Info($"No matching Jellyfin User ID found in the {users?.Count ?? 0} users from {url.Trim()}");
                            }
                        }
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.Warning($"Failed to fetch users from Jellyseerr at {url}. Status: {response.StatusCode}. Response: {errorContent}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception while trying to get Jellyseerr user ID from {url}: {ex.Message}");
                }
            }

            _logger.Warning($"Could not find a matching Jellyseerr user for Jellyfin User ID {jellyfinUserId} after checking all URLs.");
            return null;
        }
        private async Task<string?> GetSpotifyAPIToken()
        {
            if (!_memoryCache.TryGetValue("SpotifyAPIToken", out string? token))
            {
                var config = JellyfinEnhanced.Instance?.Configuration;
                if (config == null || !config.SpotifySearchEnabled || string.IsNullOrEmpty(config.SpotifySearchSLSKDUrls) || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory) || string.IsNullOrEmpty(config.SpotifyClientID) || string.IsNullOrEmpty(config.SpotifyClientSecret))
                {
                    _logger.Warning("Spotify integration is not configured correctly or enabled.");
                    return null; //StatusCode(503, "Spotify integration is not configured correctly or enabled.");
                }
                token = "";
                var httpClient = _httpClientFactory.CreateClient();
                var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "grant_type", "client_credentials" },
                    { "client_id", config.SpotifyClientID },
                    { "client_secret", config.SpotifyClientSecret }
                });
                var response = await httpClient.SendAsync(request);
                // parse response as json
                var responseContent = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseContent);
                    if (doc.RootElement.TryGetProperty("access_token", out var tokenElement))
                    {
                        token = tokenElement.GetString() ?? "";
                        var cacheEntryOptions = new MemoryCacheEntryOptions()
                            .SetSlidingExpiration(TimeSpan.FromSeconds(3599));
                        _memoryCache.Set("SpotifyAPIToken", token, cacheEntryOptions);
                        return token;
                    }
                }
                return null;
            }
            return token;
        }
        private async Task<IActionResult> ProxySpotifyRequest(string apiPath, HttpMethod method, string? content = null)
        {
            string? jellyfinUserId = null;
            if (Request.Headers.TryGetValue("X-Jellyfin-User-Id", out var jellyfinUserIdValues))
            {
                jellyfinUserId = jellyfinUserIdValues.FirstOrDefault();
                if (string.IsNullOrEmpty(jellyfinUserId))
                {
                    _logger.Warning("Could not find Jellyfin User ID in request headers.");
                    return BadRequest(new { message = "Jellyfin User ID was not provided in the request." });
                }
            }
            var token = await GetSpotifyAPIToken();
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(config.SpotifySearchSLSKDUrls) || string.IsNullOrEmpty(config.SpotifySearchSLSKDKey) || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory) ) {
                _logger.Warning("Spotify SLSKD integration is not configured correctly or enabled.");
                return StatusCode(503, "Spotify SLSKD integration is not configured correctly or enabled.");
            }

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            var url = $"https://api.spotify.com/v1/{apiPath}";
            var request = new HttpRequestMessage(method, url);
            if (content != null)
            {
                _logger.Info($"Request body: {content}");
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }
            var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.Info($"Successfully received response from spotify for user {jellyfinUserId}. Status: {response.StatusCode}");
                return Content(responseContent, "application/json");
            }

            _logger.Warning($"Request to Spotify API for user {jellyfinUserId} failed. URL: {url}, Status: {response.StatusCode}, Response: {responseContent}");
            // Try to parse the error as JSON, if it fails, create a new JSON error object.
            try
            {
                JsonDocument.Parse(responseContent);
                return StatusCode((int)response.StatusCode, responseContent);
            }
            catch (JsonException)
            {
                // The response was not valid JSON (e.g., HTML error page), so we create a standard error object.
                var errorResponse = new { message = $"Upstream error from Spotify: {response.ReasonPhrase}" };
                return StatusCode((int)response.StatusCode, errorResponse);
            }
        }
        private async Task<IActionResult> ProxySLSKDRequest(string apiPath, HttpMethod method, string? content = null)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.SpotifySearchSLSKDUrls) || string.IsNullOrEmpty(config.SpotifySearchSLSKDKey) || string.IsNullOrEmpty(config.SpotifySearchMusicDirectory))
            {
                _logger.Warning("Spotify SLSKD integration is not configured or enabled.");
                return StatusCode(503, "Spotify SLSKD integration is not configured or enabled.");
            }

            var urls = config.SpotifySearchSLSKDUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.SpotifySearchSLSKDKey);

            foreach (var url in urls)
            {
                var trimmedUrl = url.Trim();
                try
                {
                    var requestUri = $"{trimmedUrl.TrimEnd('/')}/api/v0/{apiPath}";
                    _logger.Info($"Proxying SLSKD request to: {requestUri}");

                    var request = new HttpRequestMessage(method, requestUri);
                    if (content != null)
                    {
                        _logger.Info($"Request body: {content}");
                        request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                    }

                    var response = await httpClient.SendAsync(request);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Info($"Successfully received response from SLSKD. Status: {response.StatusCode}");
                        return Content(responseContent, "application/json");
                    }

                    _logger.Warning($"Request to SLSKD. URL: {trimmedUrl}, Status: {response.StatusCode}, Response: {responseContent}");
                    // Try to parse the error as JSON, if it fails, create a new JSON error object.
                    try
                    {
                        JsonDocument.Parse(responseContent);
                        return StatusCode((int)response.StatusCode, responseContent);
                    }
                    catch (JsonException)
                    {
                        // The response was not valid JSON (e.g., HTML error page), so we create a standard error object.
                        var errorResponse = new { message = $"Upstream error from SLSKD: {response.ReasonPhrase}" };
                        return StatusCode((int)response.StatusCode, errorResponse);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to connect to SLSKD URL: {trimmedUrl}. Error: {ex.Message}");
                }
            }

            return StatusCode(500, "Could not connect to any configured SLSKD instance.");
        }

        public async Task<IActionResult> SpotifyGetAlbum(string albumId, string[]? trackIdsWhitelist = null)
        {   
            var albumInfo = await ProxySpotifyRequest($"albums/{Uri.EscapeDataString(albumId)}", HttpMethod.Get) as ContentResult;
            if (albumInfo?.Content == null) {
                return StatusCode(502, new { ok = false, message = $"Album not found for {albumId}" });
            }
            var doc = JsonDocument.Parse(albumInfo.Content);
            if (doc.RootElement.TryGetProperty("name", out var albumNameObject) && doc.RootElement.TryGetProperty("artists", out var albumArtistsObject) && doc.RootElement.TryGetProperty("tracks", out var albumTracksObject)) {
                string? albumName = albumNameObject.GetString();
                albumArtistsObject[0].TryGetProperty("name", out var mainArtistNameObject);
                string? mainArtistName = mainArtistNameObject.GetString();
                if (albumName == null || mainArtistName == null) {
                    return StatusCode(502, new { ok = false, message = "Album or artist name not found! Cannot get download!" });
                }
                albumTracksObject.TryGetProperty("items", out var albumTrackItemsObject);
                foreach (var trackObject in albumTrackItemsObject.EnumerateArray()) {
                    trackObject.TryGetProperty("id", out var trackIdObject);
                    string? trackId = trackIdObject.GetString();
                    trackObject.TryGetProperty("name", out var trackNameObject);
                    string? trackName = trackNameObject.GetString();
                    if (trackId == null || trackName == null) {
                        continue;
                    }
                    if (trackIdsWhitelist == null || trackIdsWhitelist.Length == 0 || trackIdsWhitelist.Contains(trackId)) {
                        // either the track is whitelisted or there is no whitelist, so request a download for the track
                        _ = Task.Run(async () => {
                            string searchContent = JsonSerializer.Serialize(new {searchText = $"{mainArtistName} {trackName}"});
                            var SLSKDSearchRequest = await ProxySLSKDRequest("searches", HttpMethod.Post, searchContent) as ContentResult;
                        });
                    }
                }
                

                // This is where we request the album download and whitelist ONLY this track
                return Ok(new { albumId = albumId });
            }
            return StatusCode(502, new { ok = false, message = "No album found! Cannot get download!" });
        }

        private async Task<IActionResult> ProxyJellyseerrRequest(string apiPath, HttpMethod method, string? content = null)
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || !config.JellyseerrEnabled || string.IsNullOrEmpty(config.JellyseerrUrls) || string.IsNullOrEmpty(config.JellyseerrApiKey))
            {
                _logger.Warning("Jellyseerr integration is not configured or enabled.");
                return StatusCode(503, "Jellyseerr integration is not configured or enabled.");
            }

            var urls = config.JellyseerrUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.JellyseerrApiKey);

            string? jellyfinUserId = null;
            if (Request.Headers.TryGetValue("X-Jellyfin-User-Id", out var jellyfinUserIdValues))
            {
                jellyfinUserId = jellyfinUserIdValues.FirstOrDefault();
                if (string.IsNullOrEmpty(jellyfinUserId))
                {
                    _logger.Warning("Could not find Jellyfin User ID in request headers.");
                    return BadRequest(new { message = "Jellyfin User ID was not provided in the request." });
                }
                var jellyseerrUserId = await GetJellyseerrUserId(jellyfinUserId);

                if (string.IsNullOrEmpty(jellyseerrUserId))
                {
                    _logger.Warning($"Could not find a Jellyseerr user for Jellyfin user {jellyfinUserId}. Aborting request.");
                    return NotFound(new { message = "Current Jellyfin user is not linked to a Jellyseerr user." });
                }

                httpClient.DefaultRequestHeaders.Add("X-Api-User", jellyseerrUserId);
            }
            else
            {
                _logger.Warning("X-Jellyfin-User-Id header was not present in the request. Aborting.");
                return BadRequest(new { message = "Jellyfin User ID was not provided in the request." });
            }

            foreach (var url in urls)
            {
                var trimmedUrl = url.Trim();
                try
                {
                    var requestUri = $"{trimmedUrl.TrimEnd('/')}{apiPath}";
                    _logger.Info($"Proxying Jellyseerr request for user {jellyfinUserId} to: {requestUri}");

                    var request = new HttpRequestMessage(method, requestUri);
                    if (content != null)
                    {
                        _logger.Info($"Request body: {content}");
                        request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                    }

                    var response = await httpClient.SendAsync(request);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Info($"Successfully received response from Jellyseerr for user {jellyfinUserId}. Status: {response.StatusCode}");
                        return Content(responseContent, "application/json");
                    }

                    _logger.Warning($"Request to Jellyseerr for user {jellyfinUserId} failed. URL: {trimmedUrl}, Status: {response.StatusCode}, Response: {responseContent}");
                    // Try to parse the error as JSON, if it fails, create a new JSON error object.
                    try
                    {
                        JsonDocument.Parse(responseContent);
                        return StatusCode((int)response.StatusCode, responseContent);
                    }
                    catch (JsonException)
                    {
                        // The response was not valid JSON (e.g., HTML error page), so we create a standard error object.
                        var errorResponse = new { message = $"Upstream error from Jellyseerr: {response.ReasonPhrase}" };
                        return StatusCode((int)response.StatusCode, errorResponse);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to connect to Jellyseerr URL for user {jellyfinUserId}: {trimmedUrl}. Error: {ex.Message}");
                }
            }

            return StatusCode(500, "Could not connect to any configured Jellyseerr instance.");
        }

        [HttpGet("jellyseerr/status")]
        public async Task<IActionResult> GetJellyseerrStatus()
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || !config.JellyseerrEnabled || string.IsNullOrEmpty(config.JellyseerrApiKey) || string.IsNullOrEmpty(config.JellyseerrUrls))
            {
                return Ok(new { active = false });
            }

            var urls = config.JellyseerrUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.JellyseerrApiKey);

            foreach (var url in urls)
            {
                try
                {
                    var response = await httpClient.GetAsync($"{url.Trim().TrimEnd('/')}/api/v1/status");
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Info($"Successfully connected to Jellyseerr at {url}. Status is active.");
                        return Ok(new { active = true });
                    }
                }
                catch
                {
                    // Ignore and try next URL
                }
            }

            _logger.Warning("Could not establish a connection with any configured Jellyseerr URL. Status is inactive.");
            return Ok(new { active = false });
        }

        [HttpGet("jellyseerr/validate")]
        public async Task<IActionResult> ValidateJellyseerr([FromQuery] string url, [FromQuery] string apiKey)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
                return BadRequest(new { ok = false, message = "Missing url or apiKey" });

            var http = _httpClientFactory.CreateClient();
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

            try
            {
                var resp = await http.GetAsync($"{url.TrimEnd('/')}/api/v1/user");
                if (resp.IsSuccessStatusCode)
                    return Ok(new { ok = true });

                return StatusCode((int)resp.StatusCode, new { ok = false, message = "Status check failed" });
            }
            catch (Exception ex)
            {
                _logger.Warning($"Jellyseerr validate failed for {url}: {ex.Message}");
                return StatusCode(502, new { ok = false, message = "Unable to reach Jellyseerr" });
            }
        }

        [HttpGet("jellyseerr/user-status")]
        public async Task<IActionResult> GetJellyseerrUserStatus()
        {
            // First check active status
            var activeResult = await GetJellyseerrStatus() as OkObjectResult;
            bool active = false;
            if (activeResult?.Value is not null)
            {
                var json = JsonSerializer.Serialize(activeResult.Value);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("active", out var a))
                    active = a.GetBoolean();
            }
            if (!active) return Ok(new { active = false, userFound = false });

            // Get Jellyfin user id from header
            if (!Request.Headers.TryGetValue("X-Jellyfin-User-Id", out var jellyfinUserIdValues))
                return Ok(new { active = true, userFound = false });

            var jellyfinUserId = jellyfinUserIdValues.FirstOrDefault();
            if (string.IsNullOrEmpty(jellyfinUserId))
            {
                return Ok(new { active = true, userFound = false });
            }
            var jellyseerrUserId = await GetJellyseerrUserId(jellyfinUserId);
            return Ok(new { active = true, userFound = !string.IsNullOrEmpty(jellyseerrUserId) });
        }


        [HttpGet("jellyseerr/search")]
        public Task<IActionResult> JellyseerrSearch([FromQuery] string query)
        {
            return ProxyJellyseerrRequest($"/api/v1/search?query={Uri.EscapeDataString(query)}", HttpMethod.Get);
        }

        [HttpGet("jellyseerr/sonarr")]
        public Task<IActionResult> GetSonarrInstances()
        {
            return ProxyJellyseerrRequest("/api/v1/service/sonarr", HttpMethod.Get);
        }

        [HttpGet("jellyseerr/radarr")]
        public Task<IActionResult> GetRadarrInstances()
        {
            return ProxyJellyseerrRequest("/api/v1/service/radarr", HttpMethod.Get);
        }

        [HttpGet("jellyseerr/{type}/{serverId}")]
        public Task<IActionResult> GetServiceDetails(string type, int serverId)
        {
            return ProxyJellyseerrRequest($"/api/v1/service/{type}/{serverId}", HttpMethod.Get);
        }

        [HttpPost("jellyseerr/request")]
        public async Task<IActionResult> JellyseerrRequest([FromBody] JsonElement requestBody)
        {
            return await ProxyJellyseerrRequest("/api/v1/request", HttpMethod.Post, requestBody.ToString());
        }
        [HttpGet("jellyseerr/tv/{tmdbId}")]
        public Task<IActionResult> GetTvShow(int tmdbId)
        {
            return ProxyJellyseerrRequest($"/api/v1/tv/{tmdbId}", HttpMethod.Get);
        }

        [HttpGet("jellyseerr/tv/{tmdbId}/seasons")]
        public Task<IActionResult> GetTvSeasons(int tmdbId)
        {
            return ProxyJellyseerrRequest($"/api/v1/tv/{tmdbId}/seasons", HttpMethod.Get);
        }

        [HttpPost("jellyseerr/request/tv/{tmdbId}/seasons")]
        public async Task<IActionResult> RequestTvSeasons(int tmdbId, [FromBody] JsonElement requestBody)
        {
            return await ProxyJellyseerrRequest($"/api/v1/request", HttpMethod.Post, requestBody.ToString());
        }

        [HttpGet("spotify/search")]
        public async Task<IActionResult> SpotifySearch([FromQuery] string query, [FromQuery] string type)
        {
            return await ProxySpotifyRequest($"search?q={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(type)}", HttpMethod.Get);
        }

        [HttpGet("spotify/slskdSearch")]
        public async Task<IActionResult> SpotifySearch([FromQuery] string query)
        {
            string searchContent = JsonSerializer.Serialize(new {searchText = query});
            return await ProxySLSKDRequest("searches", HttpMethod.Post, searchContent);
            //return await ProxySpotifyRequest($"search?q={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(type)}", HttpMethod.Get);
        }
        
        [HttpGet("spotify/requestTrack/{trackId}")]
        public async Task<IActionResult> SpotifyRequestTrack(string trackId)
        {
            
            var trackInfo = await ProxySpotifyRequest($"tracks/{Uri.EscapeDataString(trackId)}", HttpMethod.Get) as ContentResult;
            if (trackInfo?.Content == null) {
                return StatusCode(502, new { ok = false, message = $"Track not found for {trackId}" });
            }
            var doc = JsonDocument.Parse(trackInfo.Content);
            if (doc.RootElement.TryGetProperty("album", out var albumObject) && albumObject.TryGetProperty("id", out var albumIdObject)) {
                string albumId = albumIdObject.GetString() ?? "";
                await SpotifyGetAlbum(albumId);
                // This is where we request the album download and whitelist ONLY this track
                return Ok(new { albumId = albumId });
            }
            return StatusCode(502, new { ok = false, message = "No parent album found! Cannot get download!" });
        }

        [HttpGet("spotify/validateSLSKD")]
        public async Task<IActionResult> ValidateSLSKD([FromQuery] string url, [FromQuery] string apiKey)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
                return BadRequest(new { ok = false, message = "Missing url or apiKey" });

            var http = _httpClientFactory.CreateClient();
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

            try
            {
                var resp = await http.GetAsync($"{url.TrimEnd('/')}/api/v0/searches");
                if (resp.IsSuccessStatusCode)
                    return Ok(new { ok = true });

                return StatusCode((int)resp.StatusCode, new { ok = false, message = "Status check failed" });
            }
            catch (Exception ex)
            {
                _logger.Warning($"SLSKD validate failed for {url}: {ex.Message}");
                return StatusCode(502, new { ok = false, message = "Unable to reach SLSKD" });
            }
        }

        [HttpGet("spotify/validateSpotify")]
        public async Task<IActionResult> ValidateSpotify([FromQuery] string clientID, [FromQuery] string clientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientID) || string.IsNullOrWhiteSpace(clientSecret))
                return BadRequest(new { ok = false, message = "Missing client ID or client Secret" });

            var httpClient = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "client_id", clientID },
                { "client_secret", clientSecret }
            });

            try
            {
                var resp = await httpClient.SendAsync(request);
                if (resp.IsSuccessStatusCode)
                    return Ok(new { ok = true });

                return StatusCode((int)resp.StatusCode, new { ok = false, message = "Status check failed" });
            }
            catch (Exception ex)
            {
                _logger.Warning($"Spotify validate failed: {ex.Message}");
                return StatusCode(502, new { ok = false, message = "Unable to reach Spotify" });
            }
        }

        [HttpGet("tmdb/validate")]
        public async Task<IActionResult> ValidateTmdb([FromQuery] string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return BadRequest(new { ok = false, message = "API key is missing" });
            }

            var httpClient = _httpClientFactory.CreateClient();
            try
            {
                var requestUri = $"https://api.themoviedb.org/3/configuration?api_key={apiKey}";
                var response = await httpClient.GetAsync(requestUri);

                if (response.IsSuccessStatusCode)
                {
                    return Ok(new { ok = true });
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return Unauthorized(new { ok = false, message = "Invalid API Key." });
                }

                return StatusCode((int)response.StatusCode, new { ok = false, message = "Failed to connect to TMDB." });
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception during TMDB API key validation: {ex.Message}");
                return StatusCode(500, new { ok = false, message = "Could not reach TMDB services." });
            }
        }

        [HttpGet("script")]
        public ActionResult GetMainScript() => GetScriptResource("js/plugin.js");
        [HttpGet("js/{**path}")]
        public ActionResult GetScript(string path) => GetScriptResource($"js/{path}");
        [HttpGet("version")]
        public ActionResult GetVersion() => Content(JellyfinEnhanced.Instance?.Version.ToString() ?? "unknown");

        [HttpGet("private-config")]
        [Authorize]
        public ActionResult GetPrivateConfig()
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null)
            {
                return StatusCode(503);
            }

            return new JsonResult(new
            {
                // For Jellyfin Elsewhere & Reviews
                config.TMDB_API_KEY,

                // For Arr Links
                config.SonarrUrl,
                config.RadarrUrl,
                config.BazarrUrl
            });
        }
        [HttpGet("public-config")]
        public ActionResult GetPublicConfig()
        {
            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null)
            {
                return StatusCode(503);
            }

            return new JsonResult(new
            {
                // Jellyfin Enhanced Settings
                config.ToastDuration,
                config.HelpPanelAutocloseDelay,
                config.EnableCustomSplashScreen,
                config.SplashScreenImageUrl,

                // Jellyfin Elsewhere Settings
                config.ElsewhereEnabled,
                config.DEFAULT_REGION,
                config.DEFAULT_PROVIDERS,
                config.IGNORE_PROVIDERS,
                config.ClearLocalStorageTimestamp,

                // Default User Settings
                config.AutoPauseEnabled,
                config.AutoResumeEnabled,
                config.AutoPipEnabled,
                config.AutoSkipIntro,
                config.AutoSkipOutro,
                config.LongPress2xEnabled,
                config.RandomButtonEnabled,
                config.RandomIncludeMovies,
                config.RandomIncludeShows,
                config.RandomUnwatchedOnly,
                config.ShowFileSizes,
                config.RemoveContinueWatchingEnabled,
                config.ShowAudioLanguages,
                config.Shortcuts,
                config.ShowReviews,
                config.PauseScreenEnabled,
                config.QualityTagsEnabled,
                config.GenreTagsEnabled,
                config.DisableAllShortcuts,
                config.DefaultSubtitleStyle,
                config.DefaultSubtitleSize,
                config.DefaultSubtitleFont,
                config.DisableCustomSubtitleStyles,

                // Jellyseerr Search Settings
                config.JellyseerrEnabled,
                config.JellyseerrShowAdvanced,
                config.ShowElsewhereOnJellyseerr,

                // Arr Links Settings
                config.ArrLinksEnabled,
                config.ShowArrLinksAsText,

                config.WatchlistEnabled,
                config.KefinTweaksVersion
            });
        }

        [HttpGet("tmdb/{**apiPath}")]
        public async Task<IActionResult> ProxyTmdbRequest(string apiPath)
        {
            if (!Request.Headers.TryGetValue("X-Emby-Token", out var token) || string.IsNullOrEmpty(token))
            {
                return Unauthorized("User authentication required.");
            }

            var config = JellyfinEnhanced.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.TMDB_API_KEY))
            {
                return StatusCode(503, "TMDB API key is not configured.");
            }

            var httpClient = _httpClientFactory.CreateClient();
            var queryString = HttpContext.Request.QueryString;
            var separator = queryString.HasValue ? "&" : "?";
            var requestUri = $"https://api.themoviedb.org/3/{apiPath}{queryString}{separator}api_key={config.TMDB_API_KEY}";

            try
            {
                var response = await httpClient.GetAsync(requestUri);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return Content(content, "application/json");
                }

                return StatusCode((int)response.StatusCode, content);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to proxy TMDB request. Error: {ex.Message}");
                return StatusCode(500, "Failed to connect to TMDB.");
            }
        }

        [HttpGet("locales/{lang}.json")]
        public ActionResult GetLocale(string lang)
        {
            var sanitizedLang = Path.GetFileName(lang); // Basic sanitization
            var resourcePath = $"Jellyfin.Plugin.JellyfinEnhanced.js.locales.{sanitizedLang}.json";
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath);

            if (stream == null)
            {
                _logger.Warning($"Locale file not found for language: {sanitizedLang}");
                return NotFound();
            }

            return new FileStreamResult(stream, "application/json");
        }

        [HttpPost("watchlist")]
        public async Task<QueryResult<BaseItemDto>?> GetWatchlist()
        {
            using var reader = new StreamReader(Request.Body);
            var rawJson = await reader.ReadToEndAsync();

            JObject data = JObject.Parse(rawJson);
            var userId = Guid.Parse(data["UserId"]?.ToString() ?? string.Empty);
            if (userId == Guid.Empty)
            {
                return null;
            }
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return new QueryResult<BaseItemDto>
                {
                    TotalRecordCount = 0,
                    Items = []
                };
            }

            var likedItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode]
            }).Where(i =>
            {
                var userData = _userDataManager.GetUserData(user, i);
                return userData?.Likes == true;
            });
            var dtoOptions = new DtoOptions
            {
                Fields = new List<ItemFields>
                {
                    ItemFields.PrimaryImageAspectRatio
                },
                ImageTypeLimit = 1,
                ImageTypes = new List<ImageType>
                {
                    ImageType.Thumb,
                    ImageType.Backdrop,
                    ImageType.Primary,
                }
            };
            var items = _dtoService.GetBaseItemDtos(likedItems.ToList(), dtoOptions, user);
            return new QueryResult<BaseItemDto>
            {
                TotalRecordCount = items.Count,
                Items = items
            };
        }

        private ActionResult GetScriptResource(string resourcePath)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Jellyfin.Plugin.JellyfinEnhanced.{resourcePath.Replace('/', '.')}");
            return stream == null ? NotFound() : new FileStreamResult(stream, "application/javascript");
        }
    }
}