using System.Text.Json;

namespace RosterRiot.Models;

// Shapes of the public Sleeper API (https://docs.sleeper.com). JSON is snake_case; see SleeperClient.Json.

public sealed class NflState
{
    public int Week { get; set; }
    public int DisplayWeek { get; set; }
    public string? Season { get; set; }
    public string? SeasonType { get; set; }
}

public sealed class League
{
    public string LeagueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Season { get; set; }
    public string? Status { get; set; }
    public string? Avatar { get; set; }
    public int TotalRosters { get; set; }
    public List<string> RosterPositions { get; set; } = [];
    public Dictionary<string, double?> ScoringSettings { get; set; } = [];
    public Dictionary<string, JsonElement>? Settings { get; set; }

    public int Setting(string key, int fallback = 0) =>
        Settings is not null && Settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}

public sealed class LeagueUser
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Avatar { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public string? Meta(string key) =>
        Metadata is not null && Metadata.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class Roster
{
    public int RosterId { get; set; }
    public string? OwnerId { get; set; }
    public List<string>? Players { get; set; }
    public List<string>? Starters { get; set; }
    public List<string>? Reserve { get; set; }
    public Dictionary<string, JsonElement>? Settings { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public int Setting(string key) =>
        Settings is not null && Settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    public string? Meta(string key) =>
        Metadata is not null && Metadata.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int Wins => Setting("wins");
    public int Losses => Setting("losses");
    public int Ties => Setting("ties");
    public double PointsFor => Setting("fpts") + Setting("fpts_decimal") / 100.0;
    public double PointsAgainst => Setting("fpts_against") + Setting("fpts_against_decimal") / 100.0;
    public double MaxPoints => Setting("ppts") + Setting("ppts_decimal") / 100.0;
}

public sealed class Matchup
{
    public int RosterId { get; set; }
    public int? MatchupId { get; set; }
    public double Points { get; set; }
    public List<string>? Starters { get; set; }
    public List<double>? StartersPoints { get; set; }
    public List<string>? Players { get; set; }
    public Dictionary<string, double>? PlayersPoints { get; set; }
}

/// <summary>A row from api.sleeper.com/projections or /stats: one player's numbers for one week.</summary>
public sealed class StatRow
{
    public string PlayerId { get; set; } = "";
    public string? Team { get; set; }
    public string? Opponent { get; set; }
    public string? Date { get; set; }
    public int? Week { get; set; }
    public Dictionary<string, double?>? Stats { get; set; }
    public PlayerInfo? Player { get; set; }
}

public sealed class PlayerInfo
{
    public string? PlayerId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Position { get; set; }
    public List<string>? FantasyPositions { get; set; }
    public string? Team { get; set; }
    public string? InjuryStatus { get; set; }
    public string? InjuryBodyPart { get; set; }
    public int? YearsExp { get; set; }
    public string? College { get; set; }
    public int? Number { get; set; }
    /// <summary>ESPN athlete id. Sleeper often leaves this empty; Buzz falls back to ESPN team rosters.</summary>
    public long? EspnId { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
    public string ShortName => string.IsNullOrEmpty(FirstName) ? LastName ?? "" : $"{FirstName[0]}. {LastName}";
}

public sealed class Trending
{
    public string PlayerId { get; set; } = "";
    public int Count { get; set; }
}

public sealed class Transaction
{
    public string TransactionId { get; set; } = "";
    public string? Type { get; set; }
    public string? Status { get; set; }
    public long Created { get; set; }
    public List<int>? RosterIds { get; set; }
    public Dictionary<string, int>? Adds { get; set; }
    public Dictionary<string, int>? Drops { get; set; }
    public Dictionary<string, JsonElement>? Settings { get; set; }

    public int? WaiverBid =>
        Settings is not null && Settings.TryGetValue("waiver_bid", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
