using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StarfrontCollab.Wire;

public enum CollabFailure { Unauthorized, Refused, Conflict, RateLimited, Unavailable, Timeout, Malformed }

public sealed class CollabException(CollabFailure failure, string message) : Exception(message)
{
    public CollabFailure Failure { get; } = failure;
}

/// The agent's side of a Starfront / AstroCollab collaboration server. Bearer
/// tokens only; redirects are never followed, so a token never leaves the
/// origin it was issued for.
public sealed class CollabClient : IDisposable
{
    private readonly HttpClient http;

    public Uri Server { get; }

    public CollabClient(Uri server, HttpMessageHandler? handler = null)
    {
        Server = server;
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Starfront-TargetScheduler-Collab/" + typeof(CollabClient).Assembly.GetName().Version);
    }

    /// The server's base URL, normalized with a trailing slash. HTTPS only,
    /// except plain HTTP to this machine when explicitly allowed for testing.
    public static Uri Normalize(string text, bool allowLoopbackHttp)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) || url.AbsoluteUri.Length > 2048)
            throw new ArgumentException("Enter the server's full address, starting with https://.");
        if (url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("The server address must not carry a user name, query or fragment.");
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback && allowLoopbackHttp))
            throw new ArgumentException("Use an https:// address. Plain HTTP is allowed only to this computer, for testing.");
        var builder = new UriBuilder(url);
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    public async Task<Health> HealthAsync(CancellationToken token)
    {
        using var reply = await SendAsync(HttpMethod.Get, "health", null, null, token);
        var root = reply.RootElement;
        if (root.OptBool("ok") != true) throw Malformed("The server says it is not healthy.");
        return new(root.OptInt("protocol") ?? 1, root.OptText("version", 120) ?? "", root.OptBool("discord") == true);
    }

    public async Task<LoginStart> StartLoginAsync(CancellationToken token)
    {
        using var reply = await SendAsync(HttpMethod.Post, "auth/login", null, new JsonObject(), token);
        var root = reply.RootElement;
        var code = root.Text("code", 512);
        var seconds = root.Number("expiresIn");
        if (code.Length == 0 || seconds is < 1 or > 3600) throw Malformed("The server's sign-in offer is not readable.");
        if (!Uri.TryCreate(root.Text("url", 4096), UriKind.Absolute, out var url) || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw Malformed("The server's sign-in link is not readable.");
        // The browser link must stay on the server this telescope talks to. A
        // server reached under another name sends links for its own name.
        if (url.Scheme != Server.Scheme || url.Host != Server.Host || url.Port != Server.Port)
            throw Malformed($"This server sends sign-in links for {url.Scheme}://{url.Authority}. Set Server to that address and sign in again.");
        return new(code, url, TimeSpan.FromSeconds(Math.Floor(seconds)));
    }

    public async Task<LoginPoll> PollLoginAsync(string code, CancellationToken token)
    {
        using var reply = await SendAsync(HttpMethod.Get, "auth/poll?code=" + Uri.EscapeDataString(code), null, null, token);
        var root = reply.RootElement;
        var state = root.Text("state", 16);
        if (state is not ("pending" or "done" or "claimed" or "expired")) throw Malformed("The server's sign-in state is not readable.");
        var person = state == "done" ? Secret(root.Text("token", 8192)) : null;
        return new(state, person);
    }

    /// Enrol this telescope under a signed-in person and mint its own token.
    /// Never retried automatically: a lost reply may still have enrolled it.
    public async Task<Enrollment> EnrollAsync(string personToken, string name, CancellationToken token)
    {
        using var reply = await SendAsync(HttpMethod.Post, "agents", personToken, new JsonObject { ["name"] = name }, token);
        var root = reply.RootElement;
        var agent = root.OptObject("agent") ?? throw Json.Bad("agent");
        return new(AgentId(agent.Text("id", 64)), Secret(root.Text("token", 8192)));
    }

    public async Task<HelloReply> HelloAsync(string agentToken, RigProfile profile, Presence? presence, CancellationToken token)
    {
        var body = new JsonObject { ["protocol"] = 1, ["profile"] = profile.ToJson() };
        if (presence is not null) body["presence"] = presence.ToJson();
        using var reply = await SendAsync(HttpMethod.Post, "agent/hello", agentToken, body, token);
        var root = reply.RootElement;
        if (root.OptInt("protocol") is { } protocol && protocol != 1)
            throw Malformed($"The server speaks protocol {protocol}; this plugin speaks 1.");
        return new(AgentId(root.Text("agent", 64)), root.OptText("name", 120) ?? "", root.OptNumber("serverTime"));
    }

    /// Tonight's tasks, as raw JSON for <see cref="TonightReader"/>.
    public async Task<JsonDocument> TonightAsync(string agentToken, NightSky night, CancellationToken token)
    {
        var query = "agent/task?night=" + Uri.EscapeDataString(night.Name);
        if (night.MoonIllumination is { } moon && night.MoonUpFraction is { } up)
            query += "&moon=" + Number(moon) + "&moonUp=" + Number(up);
        return await SendAsync(HttpMethod.Get, query, agentToken, null, token);
    }

    public async Task<IReadOnlyList<OpenProject>> ProjectsAsync(string agentToken, CancellationToken token)
    {
        using var reply = await SendAsync(HttpMethod.Get, "agent/projects", agentToken, null, token);
        return [.. reply.RootElement.OptArray("projects", 256).Select(ReadProject)];
    }

    public async Task JoinAsync(string agentToken, string projectId, NightSky night, IReadOnlyDictionary<string, double> exposures, CancellationToken token)
    {
        var body = new JsonObject { ["night"] = night.Name, ["exposures"] = new JsonObject([.. exposures.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value))]) };
        if (night.MoonIllumination is { } moon && night.MoonUpFraction is { } up) { body["moon"] = moon; body["moonUp"] = up; }
        using var _ = await SendAsync(HttpMethod.Post, $"agent/projects/{Uri.EscapeDataString(projectId)}/join", agentToken, body, token);
    }

    public async Task SetTaskStateAsync(string agentToken, string taskId, string state, CancellationToken token)
    {
        if (state is not ("accepted" or "declined")) throw new ArgumentOutOfRangeException(nameof(state));
        using var _ = await SendAsync(HttpMethod.Post, $"agent/task/{Uri.EscapeDataString(taskId)}", agentToken, new JsonObject { ["state"] = state }, token);
    }

    /// Send contributions and read the verdicts. The reply is positional; a
    /// short or unreadable one is refused so nothing is marked as reported.
    public async Task<IReadOnlyList<Recorded>> ReportAsync(string agentToken, IReadOnlyList<Contribution> contributions, CancellationToken token)
    {
        if (contributions.Count is 0 or > 200) throw new ArgumentOutOfRangeException(nameof(contributions));
        var body = new JsonObject { ["contributions"] = new JsonArray([.. contributions.Select(c => (JsonNode)c.ToJson())]) };
        using var reply = await SendAsync(HttpMethod.Post, "agent/report", agentToken, body, token);
        var rows = reply.RootElement.OptArray("recorded", 200);
        if (rows.Length != contributions.Count) throw Malformed("The server recorded a different number of reports than were sent.");
        return [.. rows.Select(row =>
        {
            var verdict = row.OptObject("verdict");
            var summary = verdict?.OptText("summary", 2000) ?? "";
            return new Recorded(row.Text("id", 64), row.OptBool("accepted") ?? false, row.OptBool("duplicate") ?? false, summary);
        })];
    }

    private static OpenProject ReadProject(JsonElement value)
    {
        var compatibility = value.OptObject("compatibility");
        return new(value.Text("id", 64), value.OptText("name", 120) ?? "", value.OptText("kind", 32) ?? "mosaic",
            value.OptBool("joined") ?? false, compatibility?.OptBool("ok"), compatibility?.OptText("summary", 2000) ?? "",
            value.OptText("coordinator", 120) ?? "", value.OptInt("participants") ?? 0, value.OptInt("participantsOnline") ?? 0,
            Hours(value.OptObject("goals")), Hours(value.OptObject("collected")));
    }

    private static Dictionary<string, double> Hours(JsonElement? value)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (value is { } map)
            foreach (var entry in map.EnumerateObject().Take(64))
                if (Json.AsNumber(entry.Value) is { } hours) result[entry.Name] = hours;
        return result;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string route, string? bearer, JsonNode? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, new Uri(Server, "api/v1/" + route));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret(bearer));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CollabException(CollabFailure.Timeout, "The server did not answer in time."); }
        catch (HttpRequestException error)
        { throw new CollabException(CollabFailure.Unavailable, "The server could not be reached: " + error.Message); }

        using (response)
        {
            byte[] bytes;
            try { bytes = await ReadAsync(response, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new CollabException(CollabFailure.Timeout, "The server did not finish its answer in time."); }
            catch (Exception error) when (error is IOException or HttpRequestException)
            { throw new CollabException(CollabFailure.Unavailable, "The connection to the server dropped: " + error.Message); }
            var status = (int)response.StatusCode;
            if (status is < 200 or > 299)
            {
                var detail = Detail(bytes) ?? response.ReasonPhrase ?? "no detail";
                throw status switch
                {
                    401 => new CollabException(CollabFailure.Unauthorized, "The server rejected this telescope's token."),
                    403 or 404 or 409 => new CollabException(CollabFailure.Conflict, detail),
                    429 => new CollabException(CollabFailure.RateLimited, "The server asked this telescope to slow down."),
                    >= 500 => new CollabException(CollabFailure.Unavailable, $"The server failed ({status}): {detail}"),
                    _ => new CollabException(CollabFailure.Refused, $"The server refused the request ({status}): {detail}"),
                };
            }
            if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentEncoding.Count != 0)
                throw Malformed("The server did not answer with JSON.");
            try { return Json.Parse(bytes); }
            catch (WireException error) { throw Malformed(error.Message); }
        }
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > Json.MaxBodyBytes) throw Malformed("The server's reply is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > Json.MaxBodyBytes) throw Malformed("The server's reply is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string? Detail(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.OptText("detail", 300);
        }
        catch (Exception error) when (error is JsonException or WireException) { return null; }
    }

    public static bool ValidSecret(string value) => value.Length is > 0 and <= 8192 && value.All(c => c is >= '!' and <= '~');

    private static string Secret(string value) => ValidSecret(value) ? value : throw Malformed("A token is not in the expected form.");

    private static string AgentId(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : throw Malformed("The telescope id is not in the expected form.");

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static CollabException Malformed(string message) => new(CollabFailure.Malformed, message);

    public void Dispose() => http.Dispose();
}
