#:property TargetFramework=net10.0
#:property SelfContained=false
#:property PublishAot=false
#:property PublishTrimmed=false
#:property UseSharedCompilation=false

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

const string SourceAgentsRelativePath = ".github/agents";
const string TargetAgentsRelativePath = ".codex/agents";
const string SourceSuffix = ".agent.md";

var parsedOptions = ParseArgs(args);
if (parsedOptions is null)
{
    PrintUsage();
    return 1;
}

var workspaceRoot = Path.GetFullPath(parsedOptions.WorkspaceRoot);
var sourceAgentsPath = Path.Combine(workspaceRoot, SourceAgentsRelativePath);
var targetAgentsPath = Path.Combine(workspaceRoot, TargetAgentsRelativePath);

if (!Directory.Exists(sourceAgentsPath))
{
    Console.Error.WriteLine($"Source agents directory was not found: {sourceAgentsPath}");
    return 1;
}

Directory.CreateDirectory(targetAgentsPath);

var sourceFiles = Directory
    .EnumerateFiles(sourceAgentsPath, $"*{SourceSuffix}", SearchOption.TopDirectoryOnly)
    .OrderBy(static path => path, StringComparer.Ordinal)
    .ToArray();

var generatedCount = 0;
var warningCount = 0;
var failureCount = 0;

foreach (var sourceFile in sourceFiles)
{
    var fileName = Path.GetFileName(sourceFile);
    var fallbackName = fileName[..^SourceSuffix.Length];

    try
    {
        var sourceText = File.ReadAllText(sourceFile, Encoding.UTF8);
        var result = ConvertAgent(sourceText, fallbackName);
        warningCount += result.Warnings.Count;

        foreach (var warning in result.Warnings)
        {
            Console.Error.WriteLine($"WARN: {fileName}: {warning}");
        }

        var targetFile = Path.Combine(targetAgentsPath, $"{fallbackName}.toml");
        File.WriteAllText(targetFile, result.Toml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        generatedCount++;
    }
    catch (Exception ex) when (ex is AgentConversionException or IOException or UnauthorizedAccessException)
    {
        failureCount++;
        Console.Error.WriteLine($"ERROR: {fileName}: {ex.Message}");
    }
}

Console.WriteLine($"Generated: {generatedCount}");
Console.WriteLine($"Warnings: {warningCount}");
Console.WriteLine($"Failures: {failureCount}");

return failureCount == 0 ? 0 : 1;

static Options? ParseArgs(string[] args)
{
    if (args.Length != 1 || IsHelp(args[0]))
    {
        return null;
    }

    return new Options(args[0]);
}

static bool IsHelp(string value)
{
    return value is "-h" or "--help" or "/?";
}

static ConversionResult ConvertAgent(string sourceText, string fallbackName)
{
    var normalizedText = NormalizeNewLines(sourceText);
    var lines = normalizedText.Split('\n');

    if (lines.Length == 0 || lines[0].Trim() != "---")
    {
        throw new AgentConversionException("YAML frontmatter must start with '---'.");
    }

    var closingIndex = Array.FindIndex(lines, 1, static line => line.Trim() == "---");
    if (closingIndex < 0)
    {
        throw new AgentConversionException("YAML frontmatter closing '---' was not found.");
    }

    var frontmatterLines = lines.Skip(1).Take(closingIndex - 1).ToArray();
    var body = NormalizeDeveloperInstructions(string.Join('\n', lines.Skip(closingIndex + 1)));
    var frontmatter = ParseFrontmatter(frontmatterLines, out var preservedComments);
    var warnings = new List<string>();
    var unsupportedFrontmatterCommentLines = new List<string>();

    foreach (var key in frontmatter.Keys.OrderBy(static key => key, StringComparer.Ordinal))
    {
        if (!IsSupportedKey(key))
        {
            warnings.Add($"frontmatter key '{key}' has no direct Codex TOML equivalent and will be emitted as a comment.");
            unsupportedFrontmatterCommentLines.AddRange(FormatUnsupportedFrontmatterCommentLines(key, frontmatter[key]));
        }
    }

    var name = GetOptionalValue(frontmatter, "name");
    if (string.IsNullOrWhiteSpace(name))
    {
        name = fallbackName;
    }

    var description = GetOptionalValue(frontmatter, "description");
    if (string.IsNullOrWhiteSpace(description))
    {
        throw new AgentConversionException("frontmatter 'description' is required.");
    }

    var toml = BuildToml(name, description, preservedComments, unsupportedFrontmatterCommentLines, body);
    return new ConversionResult(toml, warnings);
}

static Dictionary<string, FrontmatterEntry> ParseFrontmatter(string[] lines, out IReadOnlyList<string> preservedComments)
{
    var values = new Dictionary<string, FrontmatterEntry>(StringComparer.Ordinal);
    var comments = new List<string>();

    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i];
        var trimmed = line.Trim();

        if (trimmed.Length == 0)
        {
            continue;
        }

        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            comments.Add(line);
            continue;
        }

        var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
        {
            continue;
        }

        var key = line[..colonIndex].Trim();
        var value = line[(colonIndex + 1)..].TrimStart();

        if (IsBlockScalarIndicator(value))
        {
            var blockLines = new List<string>();
            while (i + 1 < lines.Length && IsBlockScalarContinuation(lines[i + 1]))
            {
                i++;
                blockLines.Add(lines[i]);
            }

            values[key] = new FrontmatterEntry(ParseBlockScalar(value, blockLines));
        }
        else if (value.Trim().Length == 0 && i + 1 < lines.Length && IsBlockScalarContinuation(lines[i + 1]))
        {
            var blockLines = new List<string>();
            while (i + 1 < lines.Length && IsBlockScalarContinuation(lines[i + 1]))
            {
                i++;
                blockLines.Add(lines[i]);
            }

            var normalizedBlock = string.Join('\n', RemoveCommonIndent(blockLines)).TrimEnd('\n');
            values[key] = new FrontmatterEntry(normalizedBlock, IsMultiline: normalizedBlock.Contains('\n'));
        }
        else
        {
            values[key] = new FrontmatterEntry(UnquoteYamlScalar(value.Trim()));
        }
    }

    preservedComments = comments;
    return values;
}

static bool IsBlockScalarIndicator(string value)
{
    return value.StartsWith(">", StringComparison.Ordinal) || value.StartsWith("|", StringComparison.Ordinal);
}

static bool IsBlockScalarContinuation(string line)
{
    return line.Length == 0 || char.IsWhiteSpace(line[0]);
}

static string ParseBlockScalar(string indicator, List<string> blockLines)
{
    var unindented = RemoveCommonIndent(blockLines);
    if (indicator.StartsWith("|", StringComparison.Ordinal))
    {
        return string.Join('\n', unindented).TrimEnd('\n');
    }

    var builder = new StringBuilder();
    var previousWasBlank = false;

    foreach (var line in unindented)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            if (builder.Length > 0 && !previousWasBlank)
            {
                builder.Append('\n');
            }

            previousWasBlank = true;
            continue;
        }

        if (builder.Length > 0 && !previousWasBlank)
        {
            builder.Append(' ');
        }

        builder.Append(trimmed);
        previousWasBlank = false;
    }

    return builder.ToString();
}

static List<string> RemoveCommonIndent(List<string> lines)
{
    var minIndent = lines
        .Where(static line => line.Trim().Length > 0)
        .Select(CountLeadingWhitespace)
        .DefaultIfEmpty(0)
        .Min();

    return lines
        .Select(line => line.Length >= minIndent ? line[minIndent..] : string.Empty)
        .ToList();
}

static int CountLeadingWhitespace(string value)
{
    var count = 0;
    while (count < value.Length && char.IsWhiteSpace(value[count]))
    {
        count++;
    }

    return count;
}

static string UnquoteYamlScalar(string value)
{
    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
    {
        return value[1..^1]
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
    {
        return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
    }

    return value;
}

static bool IsSupportedKey(string key)
{
    return key is "name" or "description";
}

static string? GetOptionalValue(Dictionary<string, FrontmatterEntry> values, string key)
{
    return values.TryGetValue(key, out var value) ? value.Value.Trim() : null;
}

static IReadOnlyList<string> FormatUnsupportedFrontmatterCommentLines(string key, FrontmatterEntry entry)
{
    if (entry.Value.Length == 0)
    {
        return [$"- {key} (source frontmatter key present; no direct Codex TOML equivalent)"];
    }

    if (!entry.IsMultiline)
    {
        return [$"- {key} = {entry.Value} (no direct Codex TOML equivalent)"];
    }

    var lines = new List<string>
    {
        $"- {key}: (no direct Codex TOML equivalent)"
    };

    foreach (var line in entry.Value.Split('\n'))
    {
        lines.Add(line.Length == 0 ? "  " : $"  {line}");
    }

    return lines;
}

static string BuildToml(
    string name,
    string description,
    IReadOnlyList<string> preservedComments,
    IReadOnlyList<string> unsupportedFrontmatterCommentLines,
    string developerInstructions)
{
    var builder = new StringBuilder();
    builder.AppendLine("# <auto-generated>");
    builder.AppendLine("# Generated from .github/agents/*.agent.md. Do not edit manually.");
    builder.AppendLine("# </auto-generated>");

    if (preservedComments.Count > 0)
    {
        builder.AppendLine();

        foreach (var comment in preservedComments)
        {
            builder.AppendLine(comment);
        }
    }

    if (unsupportedFrontmatterCommentLines.Count > 0)
    {
        builder.AppendLine();
        builder.AppendLine("# Source frontmatter keys without a direct Codex TOML equivalent:");

        foreach (var line in unsupportedFrontmatterCommentLines)
        {
            builder.Append("# ");
            builder.AppendLine(line);
        }
    }

    builder.AppendLine();
    builder.Append("name = ");
    builder.AppendLine(ToTomlString(name));
    builder.Append("description = ");
    builder.AppendLine(ToTomlString(description));
    builder.AppendLine();
    builder.Append("developer_instructions = ");
    builder.Append("\"\"\"");
    builder.AppendLine();
    builder.AppendLine(ToTomlMultilineBasicString(developerInstructions));
    builder.AppendLine("\"\"\"");
    return builder.ToString();
}

static string ToTomlString(string value)
{
    var builder = new StringBuilder();
    builder.Append('"');

    foreach (var ch in value)
    {
        var escaped = ch switch
        {
            '\\' => "\\\\",
            '"' => "\\\"",
            '\b' => "\\b",
            '\t' => "\\t",
            '\n' => "\\n",
            '\f' => "\\f",
            '\r' => "\\r",
            _ when char.IsControl(ch) => "\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture),
            _ => ch.ToString()
        };

        builder.Append(escaped);
    }

    builder.Append('"');
    return builder.ToString();
}

static string ToTomlMultilineBasicString(string value)
{
    var builder = new StringBuilder();

    for (var i = 0; i < value.Length; i++)
    {
        var ch = value[i];
        var escaped = ch switch
        {
            '\\' => "\\\\",
            '\b' => "\\b",
            '\f' => "\\f",
            '\r' => string.Empty,
            _ when ch != '\n' && ch != '\t' && char.IsControl(ch) => "\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture),
            _ => ch.ToString()
        };

        builder.Append(escaped);
    }

    return builder.ToString().Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
}

static string NormalizeDeveloperInstructions(string value)
{
    return value
        .Trim()
        .Replace("runtime-evidence.agent.md", "runtime-evidence", StringComparison.Ordinal)
        .Replace("integration-test-design.agent.md", "integration-test-design", StringComparison.Ordinal);
}

static string NormalizeNewLines(string value)
{
    return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --file github_agents_to_codex_agents.cs -- <workspace-root>");
}

sealed record Options(string WorkspaceRoot);

sealed record ConversionResult(string Toml, IReadOnlyList<string> Warnings);

sealed record FrontmatterEntry(string Value, bool IsMultiline = false);

sealed class AgentConversionException(string message) : Exception(message);
