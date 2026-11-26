using System.Text.Json.Serialization;

namespace RosterRiot.Models;

/// <summary>What a buzz item is about. Decided by simple keyword rules in BuzzRules.</summary>
public enum BuzzCategory
{
    General,
    Injury,      // injury reports, practice participation
    Legal,       // suspension, arrest, discipline
    OffField,    // personal, family, partying, feuds, away from the team
    Role,        // trade, release, depth chart, snaps
    Coach,       // what the coaches are saying
    Weather,
}

/// <summary>One news story or social post about one player.</summary>
public sealed class BuzzItem
{
    public string Id { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Source { get; set; } = "";        // "RotoWire via ESPN", "ESPN", "CBS Sports", "Bluesky"
    public string? Author { get; set; }             // Bluesky handle or news byline
    public bool Social { get; set; }                // true = unverified chatter
    public DateTimeOffset At { get; set; }
    public string Title { get; set; } = "";
    public string? Snippet { get; set; }
    public string? Url { get; set; }
    public BuzzCategory Category { get; set; }
    public int Engagement { get; set; }             // likes + reposts, social only
    public List<string>? Athletes { get; set; }     // ESPN athlete ids tagged on league-wide stories
}

/// <summary>A cached batch of items from one source call.</summary>
public sealed class BuzzBucket
{
    public DateTimeOffset At { get; set; }
    public List<BuzzItem> Items { get; set; } = [];
}

/// <summary>ESPN athlete ids for one NFL team, keyed by normalized player name.</summary>
public sealed class EspnTeamIds
{
    public DateTimeOffset At { get; set; }
    public Dictionary<string, string> Ids { get; set; } = [];
}

/// <summary>Everything Buzz keeps in localStorage so reopening the app doesn't refetch.</summary>
public sealed class BuzzCache
{
    public int Version { get; set; } = 1;
    public Dictionary<string, BuzzBucket> Buckets { get; set; } = [];
    public Dictionary<string, string> EspnIds { get; set; } = [];
    public Dictionary<string, EspnTeamIds> Teams { get; set; } = [];
}

public sealed record BuzzSource(string Name, bool Ok, string? Note, DateTimeOffset? At);

public enum BuzzAlertKind { Injury, OffField }

public sealed record BuzzAlert(BuzzAlertKind Kind, int Count, BuzzItem Top, bool OnlyChatter);

/// <summary>Source-generated JSON for the cache, so Release trimming can't strip what it needs.</summary>
[JsonSerializable(typeof(BuzzCache))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class BuzzJson : JsonSerializerContext;
