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

const string AccessTokenEnvName = "QIITA_API_KEY";
const int PerPage = 100;
const int MaxPage = 100;
const int MaxRetryCount = 5;

var parsedOptions = ParseArgs(args);
if (parsedOptions is null)
{
    PrintUsage();
    return 1;
}

var options = parsedOptions.Value;
var accessToken = Environment.GetEnvironmentVariable(AccessTokenEnvName);

if (string.IsNullOrWhiteSpace(accessToken))
{
    Console.Error.WriteLine($"WARN: Environment variable '{AccessTokenEnvName}' is not set. Public items can be fetched, but the unauthenticated Qiita API rate limit is lower.");
}

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};

httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("qiita-user-period-items/1.0");
if (!string.IsNullOrWhiteSpace(accessToken))
{
    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}

try
{
    var result = await FetchItemsAsync(httpClient, options, CancellationToken.None);
    WriteCsv(options.CsvPath, result.Rows);
    PrintSummary(options, result);
    return 0;
}
catch (QiitaApiException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<FetchResult> FetchItemsAsync(HttpClient client, Options options, CancellationToken cancellationToken)
{
    var rows = new List<CsvRow>();
    var requestCount = 0;
    var reachedOlderThanStart = false;
    var reachedMaxPage = false;
    var jst = GetJapanTimeZone();

    for (var page = 1; page <= MaxPage; page++)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["per_page"] = PerPage.ToString(CultureInfo.InvariantCulture)
        };

        var endpoint = $"https://qiita.com/api/v2/users/{Uri.EscapeDataString(options.UserId)}/items";
        var uri = BuildUri(endpoint, query);
        var response = await SendWithRetryAsync(client, uri, cancellationToken);
        requestCount++;

        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response);
        }

        JsonArray items;
        try
        {
            items = JsonNode.Parse(response.Body) as JsonArray ?? new JsonArray();
        }
        catch (JsonException ex)
        {
            throw new QiitaApiException("Qiita API returned invalid JSON.", ex);
        }

        if (items.Count == 0)
        {
            break;
        }

        foreach (var item in items)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var createdAtText = GetString(obj, "created_at");
            if (!TryParseApiDateTime(createdAtText, out var createdAt))
            {
                continue;
            }

            var createdDateJst = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(createdAt, jst).DateTime);
            if (createdDateJst < options.From)
            {
                reachedOlderThanStart = true;
                continue;
            }

            if (createdDateJst > options.To)
            {
                continue;
            }

            rows.Add(new CsvRow(
                GetString(obj, "title") ?? string.Empty,
                GetString(obj, "url") ?? string.Empty,
                GetInt64(obj, "page_views_count") ?? 0L,
                GetInt64(obj, "likes_count") ?? 0L));
        }

        if (options.Verbose)
        {
            Console.WriteLine($"Fetched page {page}. matched={rows.Count}");
        }

        if (reachedOlderThanStart || items.Count < PerPage)
        {
            break;
        }

        if (page == MaxPage)
        {
            reachedMaxPage = true;
        }

        await Task.Delay(TimeSpan.FromSeconds(1.0), cancellationToken);
    }

    return new FetchResult(rows, requestCount, reachedMaxPage);
}

static async Task<ApiResponse> SendWithRetryAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
{
    for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!ShouldRetry(response.StatusCode, attempt))
        {
            return new ApiResponse(response.StatusCode, response.IsSuccessStatusCode, body);
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

static QiitaApiException CreateApiException(ApiResponse response)
{
    var bodyShort = Truncate(response.Body, 500);
    return new QiitaApiException($"Qiita API failed. Status={(int)response.StatusCode}. Body={bodyShort}");
}

static Uri BuildUri(string endpoint, IReadOnlyDictionary<string, string> query)
{
    var queryString = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    return new Uri($"{endpoint}?{queryString}");
}

static Options? ParseArgs(string[] args)
{
    string? userId = null;
    string? fromText = null;
    string? toText = null;
    string? csvPath = null;
    var verbose = false;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--user":
                if (!TryGetValueArg(args, ref i, out userId))
                {
                    return null;
                }
                break;
            case "--from":
                if (!TryGetValueArg(args, ref i, out fromText))
                {
                    return null;
                }
                break;
            case "--to":
                if (!TryGetValueArg(args, ref i, out toText))
                {
                    return null;
                }
                break;
            case "--csv":
                if (!TryGetValueArg(args, ref i, out csvPath))
                {
                    return null;
                }
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

    if (string.IsNullOrWhiteSpace(userId))
    {
        Console.Error.WriteLine("Argument '--user' is required.");
        return null;
    }

    if (string.IsNullOrWhiteSpace(csvPath))
    {
        Console.Error.WriteLine("Argument '--csv' is required.");
        return null;
    }

    if (!TryParseDate(fromText, out var from))
    {
        Console.Error.WriteLine("Argument '--from' must be yyyy-MM-dd or yyyyMMdd.");
        return null;
    }

    if (!TryParseDate(toText, out var to))
    {
        Console.Error.WriteLine("Argument '--to' must be yyyy-MM-dd or yyyyMMdd.");
        return null;
    }

    if (to < from)
    {
        Console.Error.WriteLine("Argument '--to' must be the same as or later than '--from'.");
        return null;
    }

    return new Options(userId.Trim(), from, to, csvPath, verbose);
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

static bool TryParseDate(string? text, out DateOnly value)
{
    value = default;
    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
        || DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}

static bool TryParseApiDateTime(string? text, out DateTimeOffset value)
{
    value = default;
    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
}

static TimeZoneInfo GetJapanTimeZone()
{
    try
    {
        return TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    }
    catch (TimeZoneNotFoundException)
    {
        return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    }
}

static string? GetString(JsonNode node, string propertyName)
{
    var prop = node[propertyName];
    if (prop is null)
    {
        return null;
    }

    if (prop is JsonValue value)
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

static long? GetInt64(JsonNode node, string propertyName)
{
    var prop = node[propertyName];
    if (prop is null)
    {
        return null;
    }

    if (prop is JsonValue value)
    {
        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (value.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
    }

    return null;
}

static void WriteCsv(string path, IReadOnlyList<CsvRow> rows)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
    writer.WriteLine("title,url,page_views_count,likes_count");
    foreach (var row in rows)
    {
        writer.WriteLine(string.Join(",",
            EscapeCsv(row.Title),
            EscapeCsv(row.Url),
            row.PageViewsCount.ToString(CultureInfo.InvariantCulture),
            row.LikesCount.ToString(CultureInfo.InvariantCulture)));
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

static void PrintSummary(Options options, FetchResult result)
{
    Console.WriteLine("=== Qiita Items ===");
    Console.WriteLine($"user={options.UserId}");
    Console.WriteLine($"period={options.From:yyyy-MM-dd}..{options.To:yyyy-MM-dd} (created_at JST, inclusive)");
    Console.WriteLine($"items={result.Rows.Count}");
    Console.WriteLine($"requests={result.RequestCount}");
    Console.WriteLine($"CSV written: {Path.GetFullPath(options.CsvPath)}");

    if (result.ReachedMaxPage)
    {
        Console.WriteLine();
        Console.WriteLine($"WARN: Reached Qiita API page limit ({MaxPage}). Some older items may not have been fetched.");
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --file qiita_user_period_items.cs -- --user <userId> --from <yyyy-MM-dd|yyyyMMdd> --to <yyyy-MM-dd|yyyyMMdd> --csv <path> [--verbose]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Optional:");
    Console.Error.WriteLine($"  Environment variable: {AccessTokenEnvName}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Examples:");
    Console.Error.WriteLine("  dotnet run --file qiita_user_period_items.cs -- --user Qiita --from 2026-01-01 --to 2026-03-31 --csv artifacts/qiita_items.csv");
    Console.Error.WriteLine("  dotnet run --file qiita_user_period_items.cs -- --user Qiita --from 20260101 --to 20260331 --csv artifacts/qiita_items.csv --verbose");
}

static string Truncate(string text, int max)
{
    if (string.IsNullOrEmpty(text) || text.Length <= max)
    {
        return text;
    }

    return text[..max] + "...";
}

readonly record struct Options(string UserId, DateOnly From, DateOnly To, string CsvPath, bool Verbose);
readonly record struct CsvRow(string Title, string Url, long PageViewsCount, long LikesCount);
readonly record struct FetchResult(List<CsvRow> Rows, int RequestCount, bool ReachedMaxPage);
readonly record struct ApiResponse(HttpStatusCode StatusCode, bool IsSuccessStatusCode, string Body);

sealed class QiitaApiException : Exception
{
    public QiitaApiException(string message, Exception? inner = null) : base(message, inner) { }
}
