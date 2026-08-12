using System.Text;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: HyperMemory.Bridge <health|upsert|query|summarize|integrity> [--endpoint URL]");
    Console.WriteLine("For POST commands, provide the request JSON on standard input.");
    return 0;
}

var endpoint = GetOption(args, "--endpoint")
    ?? Environment.GetEnvironmentVariable("HYPERMEMORY_API")
    ?? "http://127.0.0.1:5077";
using var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
var token = GetOption(args, "--token") ?? Environment.GetEnvironmentVariable("HYPERMEMORY_TOKEN");
if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Add("X-HyperMemory-Token", token);
var route = args[0].ToLowerInvariant() switch
{
    "health" => "health",
    "integrity" => "memory/integrity",
    "upsert" => "memory/upsert",
    "query" => "memory/query",
    "summarize" => "memory/summarize",
    _ => throw new ArgumentException($"Unknown command: {args[0]}")
};

using HttpResponseMessage response = args[0] is "health" or "integrity"
    ? await client.GetAsync(route)
    : await PostStandardInputAsync(client, route);
var body = await response.Content.ReadAsStringAsync();
Console.WriteLine(body);
return response.IsSuccessStatusCode ? 0 : 1;

static async Task<HttpResponseMessage> PostStandardInputAsync(HttpClient client, string route)
{
    var json = await Console.In.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("A JSON request is required on standard input.");
    return await client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
}

static string? GetOption(string[] values, string name)
{
    for (var i = 0; i < values.Length - 1; i++)
        if (string.Equals(values[i], name, StringComparison.OrdinalIgnoreCase)) return values[i + 1];
    return null;
}
