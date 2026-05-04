#:property TargetFramework=net10.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

const string ApiKeyEnvName = "YOUTUBE_API_KEY";
const int MaxItemsPerPage = 50;
const int MaxVideoIdsPerRequest = 50;
const int MaxRetryCount = 5;

var parsedOptions = ParseArgs(args);
if (parsedOptions is null)
{
    PrintUsage();
    return 1;
}
var options = parsedOptions.Value;

var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvName);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"Environment variable '{ApiKeyEnvName}' is not set.");
    return 1;
}

if (options.PlaylistInputs.Count == 0)
{
    Console.Error.WriteLine("No playlists were specified.");
    PrintUsage();
    return 1;
}

var playlistIds = options.PlaylistInputs
    .Select(TryParsePlaylistId)
    .Where(id => !string.IsNullOrWhiteSpace(id))
    .Select(id => id!)
    .Distinct(StringComparer.Ordinal)
    .ToList();

if (playlistIds.Count == 0)
{
    Console.Error.WriteLine("No valid playlist IDs were found in inputs.");
    return 1;
}

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};
httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("youtube-playlist-view-total/1.0");

var allRows = new List<CsvRow>();
var playlistSummaries = new List<PlaylistSummary>();
var uniqueViewByVideoId = new Dictionary<string, ulong>(StringComparer.Ordinal);
var warnings = new List<string>();
var playlistItemsRequestCount = 0;
var videosRequestCount = 0;
var succeededPlaylistCount = 0;

foreach (var playlistId in playlistIds)
{
    try
    {
        var playlistTitle = await FetchPlaylistTitleAsync(httpClient, apiKey, playlistId, options.Verbose, CancellationToken.None);
        var playlistItems = await FetchPlaylistItemsAsync(httpClient, apiKey, playlistId, options.Verbose, CancellationToken.None);
        playlistItemsRequestCount += playlistItems.RequestCount;

        var uniqueVideoIdsInPlaylist = playlistItems.VideoIds
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var perVideoStats = await FetchVideoStatsAsync(httpClient, apiKey, uniqueVideoIdsInPlaylist, options.Verbose, CancellationToken.None);
        videosRequestCount += perVideoStats.RequestCount;

        var totalViews = perVideoStats.StatsByVideoId.Values.Aggregate(0UL, (acc, item) => acc + item.ViewCount);
        var resolvedPlaylistTitle = playlistTitle ?? "(unknown)";

        foreach (var pair in perVideoStats.StatsByVideoId)
        {
            var stat = pair.Value;
            allRows.Add(new CsvRow(
                playlistId,
                resolvedPlaylistTitle,
                pair.Key,
                stat.Title,
                stat.ViewCount,
                $"https://www.youtube.com/watch?v={pair.Key}"));

            if (!uniqueViewByVideoId.ContainsKey(pair.Key))
            {
                uniqueViewByVideoId[pair.Key] = stat.ViewCount;
            }
        }

        if (perVideoStats.MissingVideoIds.Count > 0)
        {
            warnings.Add($"Playlist {playlistId}: {perVideoStats.MissingVideoIds.Count} videos were not returned by videos.list (private/deleted/unavailable).");
        }

        playlistSummaries.Add(new PlaylistSummary(
            playlistId,
            resolvedPlaylistTitle,
            playlistItems.VideoIds.Count,
            uniqueVideoIdsInPlaylist.Count,
            perVideoStats.StatsByVideoId.Count,
            totalViews));

        succeededPlaylistCount++;
    }
    catch (YouTubeApiException ex)
    {
        warnings.Add($"Playlist {playlistId} failed: {ex.Message}");
    }
}

if (succeededPlaylistCount == 0)
{
    Console.Error.WriteLine("All playlists failed to fetch. Nothing was aggregated.");
    foreach (var warning in warnings)
    {
        Console.Error.WriteLine($"WARN: {warning}");
    }
    return 1;
}

if (!string.IsNullOrWhiteSpace(options.CsvPath))
{
    WriteCsv(options.CsvPath!, allRows);
}

PrintSummary(playlistSummaries, uniqueViewByVideoId, playlistItemsRequestCount, videosRequestCount, warnings, options.CsvPath);
return 0;

static async Task<string?> FetchPlaylistTitleAsync(
    HttpClient client,
    string apiKey,
    string playlistId,
    bool verbose,
    CancellationToken cancellationToken)
{
    var query = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["part"] = "snippet",
        ["id"] = playlistId,
        ["maxResults"] = "1",
        ["key"] = apiKey
    };

    var uri = BuildUri("https://www.googleapis.com/youtube/v3/playlists", query);
    var response = await SendWithRetryAsync(client, uri, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        throw CreateApiException("playlists.list", response);
    }

    JsonNode root;
    try
    {
        root = JsonNode.Parse(response.Body) ?? new JsonObject();
    }
    catch (JsonException ex)
    {
        throw new YouTubeApiException($"playlists.list returned invalid JSON for playlist {playlistId}.", ex);
    }

    var title = GetString(root, "items", 0, "snippet", "title");
    if (verbose)
    {
        Console.WriteLine($"Fetched playlist metadata for {playlistId}. title={(title ?? "(unknown)")}");
    }
    return title;
}

static async Task<PlaylistItemsResult> FetchPlaylistItemsAsync(
    HttpClient client,
    string apiKey,
    string playlistId,
    bool verbose,
    CancellationToken cancellationToken)
{
    var videoIds = new List<string>();
    string? nextPageToken = null;
    var requestCount = 0;

    do
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["part"] = "contentDetails,snippet",
            ["maxResults"] = MaxItemsPerPage.ToString(CultureInfo.InvariantCulture),
            ["playlistId"] = playlistId,
            ["key"] = apiKey
        };
        if (!string.IsNullOrWhiteSpace(nextPageToken))
        {
            query["pageToken"] = nextPageToken;
        }

        var uri = BuildUri("https://www.googleapis.com/youtube/v3/playlistItems", query);
        var response = await SendWithRetryAsync(client, uri, cancellationToken);
        requestCount++;

        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException("playlistItems.list", response);
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(response.Body) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new YouTubeApiException($"playlistItems.list returned invalid JSON for playlist {playlistId}.", ex);
        }

        var items = root["items"] as JsonArray;
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }

                var videoId = GetString(obj, "contentDetails", "videoId");
                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    videoIds.Add(videoId);
                }
            }
        }

        nextPageToken = GetString(root, "nextPageToken");
        if (verbose)
        {
            Console.WriteLine($"Fetched playlistItems page for {playlistId}. totalVideoIds={videoIds.Count}");
        }
    }
    while (!string.IsNullOrWhiteSpace(nextPageToken));

    return new PlaylistItemsResult(videoIds, requestCount);
}

static async Task<VideoStatsResult> FetchVideoStatsAsync(
    HttpClient client,
    string apiKey,
    IReadOnlyList<string> videoIds,
    bool verbose,
    CancellationToken cancellationToken)
{
    var statsByVideoId = new Dictionary<string, VideoStat>(StringComparer.Ordinal);
    var requestCount = 0;

    for (var i = 0; i < videoIds.Count; i += MaxVideoIdsPerRequest)
    {
        var chunk = videoIds.Skip(i).Take(MaxVideoIdsPerRequest).ToList();
        if (chunk.Count == 0)
        {
            continue;
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["part"] = "statistics,snippet",
            ["id"] = string.Join(",", chunk),
            ["maxResults"] = MaxVideoIdsPerRequest.ToString(CultureInfo.InvariantCulture),
            ["key"] = apiKey
        };

        var uri = BuildUri("https://www.googleapis.com/youtube/v3/videos", query);
        var response = await SendWithRetryAsync(client, uri, cancellationToken);
        requestCount++;

        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException("videos.list", response);
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(response.Body) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new YouTubeApiException("videos.list returned invalid JSON.", ex);
        }

        var items = root["items"] as JsonArray;
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }

                var id = GetString(obj, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var title = GetString(obj, "snippet", "title") ?? "(unknown)";
                var viewCountText = GetString(obj, "statistics", "viewCount");
                var viewCount = ParseUnsignedLongOrZero(viewCountText);
                statsByVideoId[id] = new VideoStat(title, viewCount);
            }
        }

        if (verbose)
        {
            Console.WriteLine($"Fetched videos chunk: {Math.Min(i + chunk.Count, videoIds.Count)}/{videoIds.Count}");
        }
    }

    var missingVideoIds = videoIds
        .Where(id => !statsByVideoId.ContainsKey(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    return new VideoStatsResult(statsByVideoId, missingVideoIds, requestCount);
}

static async Task<ApiResponse> SendWithRetryAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
{
    for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;

        if (!ShouldRetry(status, attempt))
        {
            return new ApiResponse(status, response.IsSuccessStatusCode, body);
        }

        var delay = GetRetryDelay(response, attempt);
        await Task.Delay(delay, cancellationToken);
    }

    throw new InvalidOperationException("Unexpected retry loop exit.");
}

static bool ShouldRetry(HttpStatusCode statusCode, int attempt)
{
    if (attempt >= MaxRetryCount)
    {
        return false;
    }

    return statusCode == (HttpStatusCode)429 || (int)statusCode >= 500;
}

static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
{
    if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
    {
        return delta;
    }

    if (response.Headers.RetryAfter?.Date is { } date)
    {
        var wait = date - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            return wait;
        }
    }

    return TimeSpan.FromSeconds(Math.Min(32, Math.Pow(2, attempt)));
}

static Uri BuildUri(string endpoint, IReadOnlyDictionary<string, string> query)
{
    var queryString = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    return new Uri($"{endpoint}?{queryString}");
}

static Options? ParseArgs(string[] args)
{
    var playlistInputs = new List<string>();
    string? playlistFile = null;
    string? csvPath = null;
    var verbose = false;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--playlist":
                if (!TryGetValueArg(args, ref i, out var playlist))
                {
                    return null;
                }
                playlistInputs.Add(playlist);
                break;
            case "--playlist-file":
                if (!TryGetValueArg(args, ref i, out var file))
                {
                    return null;
                }
                playlistFile = file;
                break;
            case "--csv":
                if (!TryGetValueArg(args, ref i, out var output))
                {
                    return null;
                }
                csvPath = output;
                break;
            case "--verbose":
                verbose = true;
                break;
            case "--help":
            case "-h":
                return null;
            default:
                Console.Error.WriteLine($"Unknown argument: {arg}");
                return null;
        }
    }

    if (!string.IsNullOrWhiteSpace(playlistFile))
    {
        if (!File.Exists(playlistFile))
        {
            Console.Error.WriteLine($"Playlist file does not exist: {playlistFile}");
            return null;
        }

        foreach (var line in File.ReadAllLines(playlistFile))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }
            playlistInputs.Add(trimmed);
        }
    }

    return new Options(playlistInputs, csvPath, verbose);
}

static bool TryGetValueArg(string[] args, ref int index, out string value)
{
    value = string.Empty;
    var next = index + 1;
    if (next >= args.Length)
    {
        Console.Error.WriteLine($"Argument '{args[index]}' requires a value.");
        return false;
    }

    value = args[next];
    index = next;
    return !string.IsNullOrWhiteSpace(value);
}

static string? TryParsePlaylistId(string input)
{
    var text = input.Trim();
    if (string.IsNullOrWhiteSpace(text))
    {
        return null;
    }

    if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
    {
        return text;
    }

    if (!uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
    {
        return text;
    }

    var queryMap = ParseQueryString(uri.Query);
    if (queryMap.TryGetValue("list", out var list) && !string.IsNullOrWhiteSpace(list))
    {
        return list;
    }

    return text;
}

static Dictionary<string, string> ParseQueryString(string query)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(query))
    {
        return dict;
    }

    var trimmed = query.TrimStart('?');
    foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var eq = pair.IndexOf('=');
        if (eq <= 0)
        {
            continue;
        }

        var key = Uri.UnescapeDataString(pair[..eq]);
        var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
        dict[key] = value;
    }

    return dict;
}

static ulong ParseUnsignedLongOrZero(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return 0UL;
    }

    return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0UL;
}

static string? GetString(JsonNode node, params object[] path)
{
    JsonNode? current = node;
    foreach (var part in path)
    {
        if (current is null)
        {
            return null;
        }

        current = part switch
        {
            string key => current[key],
            int index when current is JsonArray arr && index >= 0 && index < arr.Count => arr[index],
            _ => null
        };
    }

    if (current is JsonValue value)
    {
        if (value.TryGetValue<string>(out var s))
        {
            return s;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }
    }

    return null;
}

static YouTubeApiException CreateApiException(string apiName, ApiResponse response)
{
    var bodyShort = response.Body.Length > 500 ? response.Body[..500] + "..." : response.Body;
    return new YouTubeApiException($"{apiName} failed. Status={(int)response.StatusCode} Body={bodyShort}");
}

static void WriteCsv(string path, IReadOnlyList<CsvRow> rows)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
    writer.WriteLine("playlistId,playlistTitle,videoId,videoTitle,viewCount,videoUrl");
    foreach (var row in rows)
    {
        writer.WriteLine(string.Join(",",
            EscapeCsv(row.PlaylistId),
            EscapeCsv(row.PlaylistTitle),
            EscapeCsv(row.VideoId),
            EscapeCsv(row.VideoTitle),
            row.ViewCount.ToString(CultureInfo.InvariantCulture),
            EscapeCsv(row.VideoUrl)));
    }
}

static string EscapeCsv(string value)
{
    if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
    {
        return value;
    }

    return "\"" + value.Replace("\"", "\"\"") + "\"";
}

static void PrintSummary(
    IReadOnlyList<PlaylistSummary> playlistSummaries,
    IReadOnlyDictionary<string, ulong> uniqueViewByVideoId,
    int playlistItemsRequestCount,
    int videosRequestCount,
    IReadOnlyList<string> warnings,
    string? csvPath)
{
    Console.WriteLine("=== Playlist Totals ===");
    foreach (var s in playlistSummaries.OrderBy(x => x.PlaylistId, StringComparer.Ordinal))
    {
        Console.WriteLine($"{s.PlaylistId} | {s.PlaylistTitle}");
        Console.WriteLine($"  playlist-items={s.PlaylistItemCount}, unique-video-ids={s.UniqueVideoIdCount}, stats-returned={s.ReturnedVideoCount}, views-total={s.TotalViews}");
    }

    var globalUniqueViews = uniqueViewByVideoId.Values.Aggregate(0UL, (acc, x) => acc + x);
    Console.WriteLine();
    Console.WriteLine("=== Global Unique Total ===");
    Console.WriteLine($"unique-videos={uniqueViewByVideoId.Count}, views-total={globalUniqueViews}");
    Console.WriteLine();
    Console.WriteLine("=== API Usage Estimate ===");
    Console.WriteLine($"playlists.list requests={playlistSummaries.Count}");
    Console.WriteLine($"playlistItems.list requests={playlistItemsRequestCount}");
    Console.WriteLine($"videos.list requests={videosRequestCount}");
    Console.WriteLine($"estimated quota units={playlistSummaries.Count + playlistItemsRequestCount + videosRequestCount}");

    if (!string.IsNullOrWhiteSpace(csvPath))
    {
        Console.WriteLine();
        Console.WriteLine($"CSV written: {Path.GetFullPath(csvPath)}");
    }

    if (warnings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("=== Warnings ===");
        foreach (var warning in warnings)
        {
            Console.WriteLine($"- {warning}");
        }
    }
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --file youtube_playlist_view_total.cs -- --playlist <playlistIdOrUrl> [--playlist <playlistIdOrUrl> ...] [--playlist-file <path>] [--csv <path>] [--verbose]");
    Console.WriteLine();
    Console.WriteLine("Required:");
    Console.WriteLine($"  Environment variable: {ApiKeyEnvName}");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run --file youtube_playlist_view_total.cs -- --playlist PLxxxxxxxx --playlist https://www.youtube.com/playlist?list=PLyyyyyyyy");
    Console.WriteLine("  dotnet run --file youtube_playlist_view_total.cs -- --playlist-file playlists.txt --csv artifacts/youtube_playlist_views.csv");
}

readonly record struct Options(List<string> PlaylistInputs, string? CsvPath, bool Verbose);
readonly record struct PlaylistItemsResult(List<string> VideoIds, int RequestCount);
readonly record struct VideoStat(string Title, ulong ViewCount);
readonly record struct VideoStatsResult(Dictionary<string, VideoStat> StatsByVideoId, List<string> MissingVideoIds, int RequestCount);
readonly record struct PlaylistSummary(string PlaylistId, string PlaylistTitle, int PlaylistItemCount, int UniqueVideoIdCount, int ReturnedVideoCount, ulong TotalViews);
readonly record struct CsvRow(string PlaylistId, string PlaylistTitle, string VideoId, string VideoTitle, ulong ViewCount, string VideoUrl);
readonly record struct ApiResponse(HttpStatusCode StatusCode, bool IsSuccessStatusCode, string Body);

sealed class YouTubeApiException : Exception
{
    public YouTubeApiException(string message, Exception? inner = null) : base(message, inner) { }
}
