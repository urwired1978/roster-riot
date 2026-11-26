namespace RosterRiot.Models;

/// <summary>One NFL game from ESPN's public scoreboard, flattened to what the app shows.</summary>
public sealed class NflGame
{
    public string Id { get; set; } = "";
    public DateTimeOffset Kickoff { get; set; }
    public string State { get; set; } = "pre";      // pre | in | post
    public string Detail { get; set; } = "";        // "1:51 - 4th", "Final", "11/29 - 1:00 PM EST"
    public int Period { get; set; }
    public string Clock { get; set; } = "";
    public string? Network { get; set; }
    public string? Odds { get; set; }
    public string? DownDistance { get; set; }
    public string? PossessionTeamId { get; set; }
    public bool RedZone { get; set; }
    public NflSide Home { get; set; } = new();
    public NflSide Away { get; set; } = new();

    public bool IsLive => State == "in";
    public bool IsFinal => State == "post";
    public bool HasStarted => State != "pre";

    /// <summary>Share of the game still to play, 1 before kickoff and 0 at the final whistle.</summary>
    public double RemainingFraction
    {
        get
        {
            if (State == "pre") return 1;
            if (State == "post") return 0;
            if (Period > 4) return 0.03;
            var secs = 0;
            var parts = Clock.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s)) secs = m * 60 + s;
            var left = (4 - Math.Max(Period, 1)) * 900 + secs;
            return Math.Clamp(left / 3600.0, 0, 1);
        }
    }

    public NflSide? Side(string team) => Home.Abbr == team ? Home : Away.Abbr == team ? Away : null;
    public NflSide Other(string team) => Home.Abbr == team ? Away : Home;
}

public sealed class NflSide
{
    public string Id { get; set; } = "";
    /// <summary>Sleeper-style abbreviation (ESPN's WSH becomes WAS).</summary>
    public string Abbr { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Logo { get; set; }
    public string? Color { get; set; }
    public string? Record { get; set; }
    public int Score { get; set; }
}
