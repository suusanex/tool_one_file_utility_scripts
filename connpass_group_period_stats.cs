#:property TargetFramework=net10.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

const string ApiKeyEnvName = "CONNPASS_API_KEY";
const int PageSize = 100;
const int MaxRetryCount = 5;

if (args.Length < 2)
{
    PrintUsage();
    return 1;
}

var groupName = args[0].Trim();
if (string.IsNullOrWhiteSpace(groupName))
{
    Console.Error.WriteLine("グループ名が空です。");
    PrintUsage();
    return 1;
}

if (!TryParsePeriod(args.Skip(1).ToArray(), out var period, out var periodError))
{
    Console.Error.WriteLine(periodError);
    PrintUsage();
    return 1;
}

var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvName);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"環境変数 {ApiKeyEnvName} が設定されていません。");
    return 1;
}

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};

httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("connpass-group-period-stats/1.0");
httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);

var monthKeys = EnumerateMonthKeys(period.Start, period.End).ToArray();
var allEvents = new List<JsonObject>();

foreach (var ym in monthKeys)
{
    var monthly = await FetchEventsForMonthAsync(httpClient, groupName, ym, CancellationToken.None);
    allEvents.AddRange(monthly);

    // 連続アクセスを避けるための短い待機
    await Task.Delay(TimeSpan.FromSeconds(1.2));
}

var filtered = allEvents
    .GroupBy(e => GetEventIdOrFallback(e), StringComparer.Ordinal)
    .Select(g => g.First())
    .Where(e => IsEventInPeriod(e, period.Start, period.End))
    .Where(e => IsGroupMatch(e, groupName))
    .ToList();

var eventCount = filtered.Count;
var applicantsTotal = filtered.Sum(GetApplicants);

Console.WriteLine($"グループ: {groupName}");
Console.WriteLine($"対象期間: {period.Start:yyyy-MM-dd} 〜 {period.End:yyyy-MM-dd} (両端含む)");
Console.WriteLine($"イベント開催数: {eventCount}");
Console.WriteLine($"申込者合計数: {applicantsTotal}");

return 0;

static async Task<List<JsonObject>> FetchEventsForMonthAsync(HttpClient client, string groupName, string ym, CancellationToken cancellationToken)
{
    const string endpoint = "https://connpass.com/api/v2/events/";
    return await FetchFromEndpointAsync(client, groupName, ym, endpoint, cancellationToken);
}

static async Task<List<JsonObject>> FetchFromEndpointAsync(HttpClient client, string groupName, string ym, string endpoint, CancellationToken cancellationToken)
{
    var results = new List<JsonObject>();
    var start = 1;

    while (true)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subdomain"] = groupName,
            ["ym"] = ym,
            ["count"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["start"] = start.ToString(CultureInfo.InvariantCulture),
            ["order"] = "2"
        };

        var uri = BuildUri(endpoint, query);
        var response = await SendApiRequestAsync(client, uri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new EndpointUnavailableException(endpoint, response.StatusCode, response.Body);
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(response.Body) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new EndpointUnavailableException(endpoint, response.StatusCode, response.Body, ex);
        }

        var events = ExtractEventObjects(root);
        if (events.Count == 0)
        {
            break;
        }

        results.AddRange(events);

        var returned = GetInt(root, "results_returned") ?? events.Count;
        var available = GetInt(root, "results_available") ?? -1;

        if (returned <= 0)
        {
            break;
        }

        start += returned;

        if (available > 0 && start > available)
        {
            break;
        }

        if (available < 0 && returned < PageSize)
        {
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(1.2), cancellationToken);
    }

    return results;
}

static async Task<ApiResponse> SendApiRequestAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
{
    for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != (HttpStatusCode)429 || attempt == MaxRetryCount)
        {
            return new ApiResponse(response.StatusCode, response.IsSuccessStatusCode, body);
        }

        var wait = GetRetryDelay(response, attempt);
        await Task.Delay(wait, cancellationToken);
    }

    throw new InvalidOperationException("Unexpected retry loop exit.");
}

static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
{
    if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
    {
        return delta;
    }

    if (response.Headers.RetryAfter?.Date is { } date)
    {
        var delay = date - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            return delay;
        }
    }

    return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
}

static List<JsonObject> ExtractEventObjects(JsonNode root)
{
    JsonArray? eventArray = root["events"] as JsonArray;

    if (eventArray is null)
    {
        if (root["results"] is JsonArray resultsArray)
        {
            eventArray = resultsArray;
        }
        else if (root["data"] is JsonArray dataArray)
        {
            eventArray = dataArray;
        }
    }

    if (eventArray is null)
    {
        return new List<JsonObject>();
    }

    var list = new List<JsonObject>(eventArray.Count);
    foreach (var node in eventArray)
    {
        if (node is JsonObject obj)
        {
            list.Add(obj);
        }
    }

    return list;
}

static Uri BuildUri(string endpoint, IReadOnlyDictionary<string, string> query)
{
    var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    return new Uri($"{endpoint}?{queryString}");
}

static IEnumerable<string> EnumerateMonthKeys(DateOnly start, DateOnly end)
{
    var cursor = new DateOnly(start.Year, start.Month, 1);
    var last = new DateOnly(end.Year, end.Month, 1);

    while (cursor <= last)
    {
        yield return cursor.ToString("yyyyMM", CultureInfo.InvariantCulture);
        cursor = cursor.AddMonths(1);
    }
}

static bool TryParsePeriod(string[] periodArgs, out (DateOnly Start, DateOnly End) period, out string error)
{
    period = default;
    error = string.Empty;

    if (periodArgs.Length == 1)
    {
        var tokens = periodArgs[0].Split("..", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2)
        {
            error = "対象期間は 'yyyy-MM-dd..yyyy-MM-dd' または 'yyyyMMdd..yyyyMMdd' 形式、もしくは開始日 終了日の2引数で指定してください。";
            return false;
        }

        if (!TryParseDate(tokens[0], out var start) || !TryParseDate(tokens[1], out var end))
        {
            error = "日付の解釈に失敗しました。対応形式: yyyy-MM-dd / yyyyMMdd";
            return false;
        }

        if (end < start)
        {
            error = "終了日は開始日以降を指定してください。";
            return false;
        }

        period = (start, end);
        return true;
    }

    if (periodArgs.Length >= 2)
    {
        if (!TryParseDate(periodArgs[0], out var start) || !TryParseDate(periodArgs[1], out var end))
        {
            error = "日付の解釈に失敗しました。対応形式: yyyy-MM-dd / yyyyMMdd";
            return false;
        }

        if (end < start)
        {
            error = "終了日は開始日以降を指定してください。";
            return false;
        }

        period = (start, end);
        return true;
    }

    error = "対象期間の引数が不足しています。";
    return false;
}

static bool TryParseDate(string text, out DateOnly value)
{
    return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
        || DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}

static bool IsEventInPeriod(JsonObject eventObj, DateOnly start, DateOnly end)
{
    var startedAt = GetString(eventObj, "started_at");
    if (string.IsNullOrWhiteSpace(startedAt))
    {
        return false;
    }

    if (!TryParseApiDateTime(startedAt, out var dt))
    {
        return false;
    }

    var date = DateOnly.FromDateTime(dt.DateTime);
    return date >= start && date <= end;
}

static bool TryParseApiDateTime(string text, out DateTimeOffset value)
{
    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
    {
        return true;
    }

    if (DateTimeOffset.TryParseExact(
        text,
        "yyyy-MM-ddTHH:mm:ssK",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal,
        out value))
    {
        return true;
    }

    return DateTimeOffset.TryParseExact(
        text,
        "yyyy-MM-dd HH:mm:ssK",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal,
        out value);
}

static bool IsGroupMatch(JsonObject eventObj, string groupName)
{
    var normalized = NormalizeGroup(groupName);

    var eventUrl = GetString(eventObj, "url") ?? GetString(eventObj, "event_url");
    if (MatchesByConnpassSubdomain(eventUrl, normalized))
    {
        return true;
    }

    var ownerNickname = GetString(eventObj, "owner_nickname");
    if (NormalizeGroup(ownerNickname) == normalized)
    {
        return true;
    }

    var series = eventObj["series"] as JsonObject;
    if (series is not null)
    {
        var seriesUrl = GetString(series, "url");
        if (MatchesByConnpassSubdomain(seriesUrl, normalized))
        {
            return true;
        }

        var seriesTitle = GetString(series, "title");
        if (NormalizeGroup(seriesTitle) == normalized)
        {
            return true;
        }
    }

    return false;
}

static bool MatchesByConnpassSubdomain(string? urlText, string normalizedGroup)
{
    if (string.IsNullOrWhiteSpace(urlText))
    {
        return false;
    }

    if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
    {
        return false;
    }

    var host = uri.Host;
    if (!host.EndsWith(".connpass.com", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var subdomain = host[..^".connpass.com".Length];
    return string.Equals(NormalizeGroup(subdomain), normalizedGroup, StringComparison.Ordinal);
}

static int GetApplicants(JsonObject eventObj)
{
    var accepted = GetInt(eventObj, "accepted");
    var waiting = GetInt(eventObj, "waiting");

    if (accepted.HasValue && waiting.HasValue)
    {
        return accepted.Value + waiting.Value;
    }

    if (accepted.HasValue)
    {
        return accepted.Value;
    }

    var participants = GetInt(eventObj, "participants");
    if (participants.HasValue)
    {
        return participants.Value;
    }

    return 0;
}

static string GetEventIdOrFallback(JsonObject eventObj)
{
    var id = GetString(eventObj, "event_id");
    if (!string.IsNullOrWhiteSpace(id))
    {
        return $"event_id:{id}";
    }

    var id2 = GetString(eventObj, "id");
    if (!string.IsNullOrWhiteSpace(id2))
    {
        return $"id:{id2}";
    }

    var url = GetString(eventObj, "url") ?? GetString(eventObj, "event_url");
    if (!string.IsNullOrWhiteSpace(url))
    {
        return $"url:{url}";
    }

    var title = GetString(eventObj, "title") ?? "";
    var startedAt = GetString(eventObj, "started_at") ?? "";
    return $"fallback:{title}:{startedAt}";
}

static string NormalizeGroup(string? text)
{
    return (text ?? string.Empty).Trim().ToLowerInvariant();
}

static int? GetInt(JsonNode node, string propertyName)
{
    var prop = node[propertyName];
    if (prop is null)
    {
        return null;
    }

    if (prop is JsonValue value)
    {
        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (value.TryGetValue<long>(out var l) && l <= int.MaxValue && l >= int.MinValue)
        {
            return (int)l;
        }

        if (value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
    }

    return null;
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

static void PrintUsage()
{
    Console.Error.WriteLine("使い方:");
    Console.Error.WriteLine("  dotnet run --file connpass_group_period_stats.cs -- <groupName> <startDate> <endDate>");
    Console.Error.WriteLine("  dotnet run --file connpass_group_period_stats.cs -- <groupName> <startDate..endDate>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("日付形式: yyyy-MM-dd または yyyyMMdd");
    Console.Error.WriteLine("例:");
    Console.Error.WriteLine("  dotnet run --file connpass_group_period_stats.cs -- dotnetlab 2026-01-01 2026-03-31");
    Console.Error.WriteLine("  dotnet run --file connpass_group_period_stats.cs -- dotnetlab 20260101..20260331");
}

readonly record struct ApiResponse(HttpStatusCode StatusCode, bool IsSuccessStatusCode, string Body);

sealed class EndpointUnavailableException : Exception
{
    public EndpointUnavailableException(string endpoint, HttpStatusCode statusCode, string responseBody, Exception? inner = null)
        : base($"Endpoint '{endpoint}' failed. Status={(int)statusCode}. Body={Truncate(responseBody, 280)}", inner)
    {
        Endpoint = endpoint;
        StatusCode = statusCode;
    }

    public string Endpoint { get; }

    public HttpStatusCode StatusCode { get; }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max] + "...";
    }
}
