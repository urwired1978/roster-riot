using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RosterRiot.Models;

namespace RosterRiot.Services;

/// <summary>
/// Sleeper's public read-only API. No key is needed and it allows browser requests (CORS *),
/// so the phone talks to Sleeper directly. There's no server in between.
/// </summary>
public sealed class SleeperClient(HttpClient http)
{
    const string V1 = "https://api.sleeper.app/v1";
    const string Web = "https://api.sleeper.com";
    const string ProjPositions = "position[]=QB&position[]=RB&position[]=WR&position[]=TE&position[]=K";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    Task<T?> Get<T>(string url) => http.GetFromJsonAsync<T>(url, Json);

    public Task<NflState?> State() => Get<NflState>($"{V1}/state/nfl");
    public Task<League?> League(string id) => Get<League>($"{V1}/league/{id}");
    public Task<List<LeagueUser>?> Users(string id) => Get<List<LeagueUser>>($"{V1}/league/{id}/users");
    public Task<List<Roster>?> Rosters(string id) => Get<List<Roster>>($"{V1}/league/{id}/rosters");
    public Task<List<Matchup>?> Matchups(string id, int week) => Get<List<Matchup>>($"{V1}/league/{id}/matchups/{week}");
    public Task<List<Transaction>?> Transactions(string id, int week) => Get<List<Transaction>>($"{V1}/league/{id}/transactions/{week}");
    public Task<List<Trending>?> TrendingAdds(int limit = 60) => Get<List<Trending>>($"{V1}/players/nfl/trending/add?lookback_hours=24&limit={limit}");

    public Task<List<StatRow>?> Projections(string season, int week) =>
        Get<List<StatRow>>($"{Web}/projections/nfl/{season}/{week}?season_type=regular&{ProjPositions}");

    /// <summary>
    /// One week's projections boiled down to player id → fantasy points under <paramref name="scoring"/>.
    /// The feed is about 2 MB, so it's read with a streaming reader that skips everything but the stats,
    /// which is many times faster on a phone than building full objects.
    /// </summary>
    public async Task<Dictionary<string, double>> ProjectedPoints(string season, int week, IReadOnlyDictionary<string, double?> scoring)
    {
        var bytes = await http.GetByteArrayAsync($"{Web}/projections/nfl/{season}/{week}?season_type=regular&{ProjPositions}");
        return ParsePoints(bytes, scoring);
    }

    static Dictionary<string, double> ParsePoints(byte[] json, IReadOnlyDictionary<string, double?> scoring)
    {
        var result = new Dictionary<string, double>();
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return result;
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? pid = null;
            double pts = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("player_id"u8))
                {
                    reader.Read();
                    pid = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("stats"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var key = reader.GetString()!;
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.Number && scoring.TryGetValue(key, out var m) && m is { } mult)
                            pts += reader.GetDouble() * mult;
                        else reader.Skip();
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            if (pid is not null) result.TryAdd(pid, Math.Round(pts, 2));
        }
        return result;
    }

    /// <summary>One player's stats, keyed by week number ("1".."18"); a week is null when he didn't play.</summary>
    public Task<Dictionary<string, StatRow?>?> PlayerWeeks(string playerId, string season) =>
        Get<Dictionary<string, StatRow?>>($"{Web}/stats/nfl/player/{playerId}?season_type=regular&season={season}&grouping=week");

    public Task<PlayerInfo?> Player(string playerId) => Get<PlayerInfo>($"{Web}/players/nfl/{playerId}");

    public static string Headshot(string playerId) => $"https://sleepercdn.com/content/nfl/players/thumb/{playerId}.jpg";
    public static string TeamLogo(string team) => $"https://sleepercdn.com/images/team_logos/nfl/{team.ToLowerInvariant()}.png";
    public static string Avatar(string? avatarId) => $"https://sleepercdn.com/avatars/thumbs/{avatarId}";
}

/// <summary>ESPN's public NFL scoreboard (also CORS *): kickoff times, live scores, game clock, TV.</summary>
public sealed class EspnClient(HttpClient http)
{
    const string Url = "https://site.api.espn.com/apis/site/v2/sports/football/nfl/scoreboard";

    public async Task<List<NflGame>> Scoreboard(int? week = null)
    {
        var url = week is null ? Url : $"{Url}?week={week}&seasontype=2";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        var games = new List<NflGame>();
        if (!doc.RootElement.TryGetProperty("events", out var events)) return games;
        foreach (var e in events.EnumerateArray())
        {
            var comp = e.GetProperty("competitions")[0];
            var status = e.GetProperty("status");
            var type = status.GetProperty("type");
            var g = new NflGame
            {
                Id = Str(e, "id") ?? "",
                Kickoff = DateTimeOffset.TryParse(Str(e, "date"), out var k) ? k : default,
                State = Str(type, "state") ?? "pre",
                Detail = Str(type, "shortDetail") ?? "",
                Period = status.TryGetProperty("period", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                Clock = Str(status, "displayClock") ?? "",
            };
            if (comp.TryGetProperty("broadcasts", out var b) && b.GetArrayLength() > 0 &&
                b[0].TryGetProperty("names", out var names) && names.GetArrayLength() > 0)
                g.Network = names[0].GetString();
            if (comp.TryGetProperty("odds", out var odds) && odds.ValueKind == JsonValueKind.Array && odds.GetArrayLength() > 0)
                g.Odds = Str(odds[0], "details");
            if (comp.TryGetProperty("situation", out var sit))
            {
                g.DownDistance = Str(sit, "shortDownDistanceText") ?? Str(sit, "downDistanceText");
                g.PossessionTeamId = Str(sit, "possession");
                g.RedZone = sit.TryGetProperty("isRedZone", out var rz) && rz.ValueKind == JsonValueKind.True;
            }
            foreach (var c in comp.GetProperty("competitors").EnumerateArray())
            {
                var team = c.GetProperty("team");
                var side = new NflSide
                {
                    Id = Str(team, "id") ?? "",
                    Abbr = Normalize(Str(team, "abbreviation") ?? ""),
                    Name = Str(team, "shortDisplayName") ?? Str(team, "displayName") ?? "",
                    Logo = Str(team, "logo"),
                    Color = Str(team, "color"),
                    Score = int.TryParse(Str(c, "score"), out var s) ? s : 0,
                };
                if (c.TryGetProperty("records", out var recs) && recs.GetArrayLength() > 0) side.Record = Str(recs[0], "summary");
                if (Str(c, "homeAway") == "home") g.Home = side; else g.Away = side;
            }
            games.Add(g);
        }
        return games.OrderBy(x => x.Kickoff).ToList();
    }

    /// <summary>ESPN and Sleeper agree on team codes except Washington.</summary>
    static string Normalize(string abbr) => abbr == "WSH" ? "WAS" : abbr;

    static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;
}
