#:property TargetFramework=net10.0
#:property SelfContained=false
#:property PublishAot=false
#:property PublishTrimmed=false
#:property UseSharedCompilation=false

#nullable enable

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

var parsedOptions = ParseArgs(args);
if (parsedOptions is null)
{
    PrintUsage();
    return 1;
}

var options = parsedOptions.Value;

try
{
    var inputText = File.ReadAllText(options.InputPath, Encoding.UTF8);
    var decodedText = DecodeContent(inputText);
    File.WriteAllText(options.OutputPath, decodedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static Options? ParseArgs(string[] args)
{
    if (args.Length is not 1 and not 2 || IsHelp(args[0]))
    {
        return null;
    }

    var inputPath = args[0];
    var outputPath = args.Length == 2 ? args[1] : args[0];
    return new Options(inputPath, outputPath);
}

static bool IsHelp(string value)
{
    return value is "-h" or "--help" or "/?";
}

static string DecodeContent(string inputText)
{
    if (TryDecodeJson(inputText, out var decodedJsonText))
    {
        return decodedJsonText;
    }

    if (TryDecodeJsonLines(inputText, out var decodedJsonLinesText))
    {
        return decodedJsonLinesText;
    }

    return WebUtility.HtmlDecode(inputText);
}

static bool TryDecodeJson(string inputText, out string decodedText)
{
    try
    {
        var root = JsonNode.Parse(inputText);
        if (root is null)
        {
            decodedText = inputText;
            return true;
        }

        DecodeJsonNodeStrings(root);
        decodedText = root.ToJsonString(CreateJsonWriterOptions());
        return true;
    }
    catch (JsonException)
    {
        decodedText = string.Empty;
        return false;
    }
}

static bool TryDecodeJsonLines(string inputText, out string decodedText)
{
    var lineEnding = inputText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var endsWithNewline = inputText.EndsWith("\r\n", StringComparison.Ordinal) || inputText.EndsWith("\n", StringComparison.Ordinal);
    var lines = new List<string>();

    using var reader = new StringReader(inputText);
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            lines.Add(line);
            continue;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            decodedText = string.Empty;
            return false;
        }

        if (root is null)
        {
            lines.Add(line);
            continue;
        }

        DecodeJsonNodeStrings(root);
        lines.Add(root.ToJsonString(CreateJsonWriterOptions()));
    }

    decodedText = string.Join(lineEnding, lines);
    if (endsWithNewline)
    {
        decodedText += lineEnding;
    }

    return true;
}

static void DecodeJsonNodeStrings(JsonNode node)
{
    switch (node)
    {
        case JsonObject jsonObject:
            foreach (var entry in jsonObject.ToList())
            {
                if (entry.Value is null)
                {
                    continue;
                }

                if (TryGetStringValue(entry.Value, out var stringValue))
                {
                    jsonObject[entry.Key] = WebUtility.HtmlDecode(stringValue);
                    continue;
                }

                DecodeJsonNodeStrings(entry.Value);
            }
            break;

        case JsonArray jsonArray:
            for (var i = 0; i < jsonArray.Count; i++)
            {
                var item = jsonArray[i];
                if (item is null)
                {
                    continue;
                }

                if (TryGetStringValue(item, out var stringValue))
                {
                    jsonArray[i] = WebUtility.HtmlDecode(stringValue);
                    continue;
                }

                DecodeJsonNodeStrings(item);
            }
            break;
    }
}

static bool TryGetStringValue(JsonNode node, out string value)
{
    if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out value!))
    {
        return true;
    }

    value = string.Empty;
    return false;
}

static JsonSerializerOptions CreateJsonWriterOptions()
{
    return new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run html_entity_decode_overwrite.cs <input-path> [output-path]");
    Console.WriteLine();
    Console.WriteLine("Reads a UTF-8 text, JSON, or JSONL file, decodes HTML entity references such as &#x306E;, and writes UTF-8 text back.");
    Console.WriteLine("For JSON and JSONL, string values are decoded after JSON unescaping so sequences like \\u003C and &#x306E; are both handled.");
    Console.WriteLine("If output-path is omitted, the input file is overwritten in place.");
}

internal readonly record struct Options(string InputPath, string OutputPath);
