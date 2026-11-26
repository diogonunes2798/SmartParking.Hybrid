using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Runs a REST Client <c>.http</c> file against a test host and returns each request's expected and actual status
/// (spec 003, AC-18). It supports the parts of the format that the project's files use:
/// <list type="bullet">
/// <item>file variables (<c>@name = value</c>) and blocks split by <c>###</c>;</item>
/// <item><c># @name</c> to name a response and <c># @expect &lt;status&gt;</c> for the expected status;</item>
/// <item>references <c>{{name.response.body.field}}</c>, <c>{{name.response.body.$.items[0].field}}</c> and
/// <c>{{name.response.headers.Header}}</c>;</item>
/// <item>the system variables <c>$guid</c>, <c>$randomInt min max</c> and <c>$datetime iso8601</c>.</item>
/// </list>
/// The scheme and host of each URL are dropped, so the request goes to the test server.
/// </summary>
internal static partial class HttpFileRunner
{
    public sealed record Exchange(int Line, string Request, int? Expected, int Actual);

    private sealed record Block(int Line, string? Name, int? Expected, string Method, string Url, List<(string Name, string Value)> Headers, string? Body);

    private sealed record Response(JsonElement? Body, HttpResponseHeaders Headers, HttpContentHeaders? ContentHeaders);

    public static async Task<List<Exchange>> RunAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var (variables, blocks) = Parse(await File.ReadAllLinesAsync(path, cancellationToken));
        var responses = new Dictionary<string, Response>(StringComparer.Ordinal);
        List<Exchange> exchanges = [];

        foreach (var block in blocks)
        {
            string Resolve(string text) => Reference().Replace(text, match => Value(match.Groups[1].Value.Trim(), variables, responses, Resolve));

            var url = new Uri(Resolve(block.Url), UriKind.RelativeOrAbsolute);
            using var request = new HttpRequestMessage(new HttpMethod(block.Method), url.IsAbsoluteUri ? new Uri(url.PathAndQuery, UriKind.Relative) : url);
            string? contentType = null;
            foreach (var (name, value) in block.Headers)
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = Resolve(value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(name, Resolve(value));
                }
            }

            if (block.Body is not null)
            {
                request.Content = new StringContent(Resolve(block.Body), Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (block.Name is not null)
            {
                JsonElement? body = text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true
                    ? JsonDocument.Parse(text).RootElement.Clone()
                    : null;
                responses[block.Name] = new Response(body, response.Headers, response.Content.Headers);
            }

            exchanges.Add(new Exchange(block.Line, $"{block.Method} {request.RequestUri}", block.Expected, (int)response.StatusCode));
        }

        return exchanges;
    }

    private static (Dictionary<string, string> Variables, List<Block> Blocks) Parse(string[] lines)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        List<Block> blocks = [];

        var index = 0;
        while (index < lines.Length)
        {
            // Outside a request: file variables, comments and blank lines until a request line.
            var line = lines[index].Trim();
            if (FileVariable().Match(line) is { Success: true } variable)
            {
                variables[variable.Groups["name"].Value] = variable.Groups["value"].Value.Trim();
                index++;
                continue;
            }

            if (!line.StartsWith("###", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            index++;
            string? name = null;
            int? expected = null;
            while (index < lines.Length && IsCommentOrBlank(lines[index]))
            {
                if (NameDirective().Match(lines[index]) is { Success: true } named)
                {
                    name = named.Groups[1].Value;
                }

                if (ExpectDirective().Match(lines[index]) is { Success: true } expectation)
                {
                    expected = int.Parse(expectation.Groups[1].Value, CultureInfo.InvariantCulture);
                }

                index++;
            }

            if (index >= lines.Length || lines[index].StartsWith("###", StringComparison.Ordinal) || RequestLine().Match(lines[index]) is not { Success: true } requestLine)
            {
                continue;
            }

            var start = index + 1;
            index++;
            List<(string, string)> headers = [];
            while (index < lines.Length && lines[index].Trim().Length > 0 && !lines[index].StartsWith("###", StringComparison.Ordinal))
            {
                if (IsCommentOrBlank(lines[index]))
                {
                    index++;
                    continue;
                }

                var colon = lines[index].IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new FormatException($"Line {index + 1}: expected a 'Name: value' header or a blank line before the body, got '{lines[index]}'.");
                }

                headers.Add((lines[index][..colon].Trim(), lines[index][(colon + 1)..].Trim()));
                index++;
            }

            List<string> body = [];
            while (index < lines.Length && !lines[index].StartsWith("###", StringComparison.Ordinal))
            {
                body.Add(lines[index]);
                index++;
            }

            var bodyText = string.Join('\n', body).Trim();
            blocks.Add(new Block(start, name, expected, requestLine.Groups["method"].Value, requestLine.Groups["url"].Value, headers, bodyText.Length > 0 ? bodyText : null));
        }

        return (variables, blocks);
    }

    private static string Value(string reference, Dictionary<string, string> variables, Dictionary<string, Response> responses, Func<string, string> resolve)
    {
        if (reference.StartsWith('$'))
        {
            var parts = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts[0] switch
            {
                "$guid" => Guid.NewGuid().ToString(),
                "$randomInt" => Random.Shared.Next(int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture))
                    .ToString(CultureInfo.InvariantCulture),
                "$datetime" when parts.Length > 1 && parts[1] == "iso8601" => TimeProvider.System.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                _ => throw new NotSupportedException($"System variable {reference} is not supported by the test runner."),
            };
        }

        if (variables.TryGetValue(reference, out var fileValue))
        {
            return resolve(fileValue);
        }

        var segments = reference.Split('.', 4);
        if (segments.Length < 4 || segments[1] != "response" || !responses.TryGetValue(segments[0], out var response))
        {
            throw new InvalidOperationException($"Unknown reference {{{{{reference}}}}}: no variable or earlier named response.");
        }

        return segments[2] switch
        {
            "headers" => response.Headers.TryGetValues(segments[3], out var values) || (response.ContentHeaders?.TryGetValues(segments[3], out values) ?? false)
                ? string.Join(',', values!)
                : throw new InvalidOperationException($"Response {segments[0]} has no header {segments[3]}."),
            "body" => BodyValue(response.Body ?? throw new InvalidOperationException($"Response {segments[0]} has no JSON body."), segments[3]),
            _ => throw new NotSupportedException($"Reference {reference} is not supported by the test runner."),
        };
    }

    /// <summary><c>id</c>, <c>a.b</c> or the JSONPath form <c>$.items[0].spotId</c>.</summary>
    private static string BodyValue(JsonElement body, string path)
    {
        var element = body;
        foreach (Match step in PathStep().Matches(path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path))
        {
            element = step.Groups["property"].Success
                ? element.GetProperty(step.Groups["property"].Value)
                : element[int.Parse(step.Groups["index"].Value, CultureInfo.InvariantCulture)];
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
    }

    private static bool IsCommentOrBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || (trimmed.StartsWith('#') && !trimmed.StartsWith("###", StringComparison.Ordinal)) || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^@(?<name>[A-Za-z_][\w]*)\s*=\s*(?<value>.*)$")]
    private static partial Regex FileVariable();

    [GeneratedRegex(@"^\s*(?:#|//)\s*@name\s+(\S+)")]
    private static partial Regex NameDirective();

    [GeneratedRegex(@"^\s*(?:#|//)\s*@expect\s+(\d{3})")]
    private static partial Regex ExpectDirective();

    [GeneratedRegex(@"^(?<method>GET|POST|PUT|DELETE|PATCH)\s+(?<url>\S+)")]
    private static partial Regex RequestLine();

    [GeneratedRegex(@"\{\{(.+?)\}\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"(?<property>[^.\[\]]+)|\[(?<index>\d+)\]")]
    private static partial Regex PathStep();
}
