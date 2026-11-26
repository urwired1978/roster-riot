using Microsoft.JSInterop;
using RosterRiot.Models;

namespace RosterRiot.Services;

/// <summary>
/// Everything the app knows about the league, loaded once and then kept fresh:
/// every 30 seconds while an NFL game is live, every 5 minutes otherwise.
/// Pages read from here and redraw when <see cref="Changed"/> fires.
/// </summary>
public sealed class LeagueService(SleeperClient sleeper, EspnClient espn, IJSRuntime js) : IAsyncDisposable
{
    public const string LeagueId = "1311997611338649600";      // LRCA Fantasy Football
    public const string DefaultOwnerId = "989253930330701824";  // Njigbas in Paris
    const string OwnerKey = "njhq.owner";

    public string MyOwnerId { get; private set; } = DefaultOwnerId;
    public League? League { get; private set; }
    public NflState? State { get; private set; }
    public int Week { get; private set; } = 1;
    public string Season { get; private set; } = DateTime.Now.Year.ToString();
    public List<LeagueUser> Users { get; private set; } = [];
    public List<Roster> Rosters { get; private set; } = [];
    public Dictionary<int, List<Matchup>> Weeks { get; } = [];
    public Dictionary<string, StatRow> Proj { get; private set; } = [];
    public Dictionary<string, PlayerInfo> Players { get; } = [];
    public List<NflGame> Games { get; private set; } = [];
    public List<Trending> Trending { get; private set; } = [];
    public List<Transaction> Moves { get; private set; } = [];
    public DateTimeOffset? UpdatedAt { get; private set; }
    public bool Refreshing { get; private set; }
    public string? Error { get; private set; }
    public bool Ready => League is not null && Rosters.Count > 0;

    public event Action? Changed;

    Task? loadTask;
    readonly CancellationTokenSource cts = new();
    DateTimeOffset projLoadedAt;
    Dictionary<string, double> projPts = [];
    Dictionary<string, Roster> ownerOf = [];
    Dictionary<string, NflGame> gameByTeam = [];
    Dictionary<string, (double Total, int Games)> seasonPts = [];
    readonly Dictionary<string, Dictionary<string, StatRow?>> playerWeeks = [];

    public Task EnsureLoadedAsync() => loadTask ??= LoadAllAsync();

    async Task LoadAllAsync()
    {
        try
        {
            try { MyOwnerId = await js.InvokeAsync<string?>("localStorage.getItem", OwnerKey) ?? DefaultOwnerId; } catch { }

            var stateT = sleeper.State();
            var leagueT = sleeper.League(LeagueId);
            var usersT = sleeper.Users(LeagueId);
            var rostersT = sleeper.Rosters(LeagueId);
            var gamesT = SafeGames();
            await Task.WhenAll(stateT, leagueT, usersT, rostersT, gamesT);

            State = stateT.Result;
            League = leagueT.Result;
            Users = usersT.Result ?? [];
            Rosters = rostersT.Result ?? [];
            Games = gamesT.Result;
            Season = League?.Season ?? State?.Season ?? Season;
            Week = Math.Clamp(State?.SeasonType == "regular" ? State.Week : League?.Setting("last_scored_leg", 1) ?? 1, 1, 18);

            var projT = sleeper.Projections(Season, Week);
            // Past weeks for history, plus the rest of the regular season's schedule for playoff odds.
            var lastWeek = Math.Clamp(Math.Max(Week, (League?.Setting("playoff_week_start", 15) ?? 15) - 1), 1, 18);
            var weekTasks = Enumerable.Range(1, lastWeek).ToDictionary(w => w, w => sleeper.Matchups(LeagueId, w));
            var trendT = Safe(() => sleeper.TrendingAdds());
            var movesT = Task.WhenAll(Safe(() => sleeper.Transactions(LeagueId, Week)), Safe(() => sleeper.Transactions(LeagueId, Math.Max(1, Week - 1))));
            await Task.WhenAll(weekTasks.Values.Cast<Task>().Append(projT).Append(trendT).Append(movesT));

            foreach (var (w, t) in weekTasks) Weeks[w] = t.Result ?? [];
            SetProjections(projT.Result ?? []);
            Trending = trendT.Result ?? [];
            Moves = movesT.Result.SelectMany(x => x ?? []).Where(m => m.Status == "complete").OrderByDescending(m => m.Created).ToList();

            await FillMissingPlayers();
            Rebuild();
            UpdatedAt = DateTimeOffset.Now;
            Error = null;
        }
        catch (Exception ex)
        {
            Error = "Couldn't reach Sleeper. Check your connection and tap refresh.";
            Console.Error.WriteLine(ex);
            loadTask = null;
        }
        Changed?.Invoke();
        if (Ready) _ = LiveLoop();
    }

    bool looping;
    async Task LiveLoop()
    {
        if (looping) return;
        looping = true;
        while (!cts.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var delay = Games.Any(g => g.IsLive) ? TimeSpan.FromSeconds(30)
                : Games.Any(g => !g.HasStarted && g.Kickoff - now < TimeSpan.FromMinutes(20) && g.Kickoff > now - TimeSpan.FromHours(1)) ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromMinutes(5);
            try { await Task.Delay(delay, cts.Token); } catch (TaskCanceledException) { return; }
            await RefreshAsync(full: false);
        }
    }

    /// <summary>Re-pulls live scores, lineups and NFL games. A full refresh also re-pulls projections and league moves.</summary>
    public async Task RefreshAsync(bool full = true)
    {
        if (!Ready) { loadTask = null; await EnsureLoadedAsync(); return; }
        if (Refreshing) return;
        Refreshing = true;
        Changed?.Invoke();
        try
        {
            var mT = sleeper.Matchups(LeagueId, Week);
            var rT = sleeper.Rosters(LeagueId);
            var gT = SafeGames();
            var needProj = full || DateTimeOffset.Now - projLoadedAt > TimeSpan.FromMinutes(15);
            var pT = needProj ? sleeper.Projections(Season, Week) : Task.FromResult<List<StatRow>?>(null);
            var tT = full ? Task.WhenAll(Safe(() => sleeper.Transactions(LeagueId, Week)), Safe(() => sleeper.Transactions(LeagueId, Math.Max(1, Week - 1))))
                          : Task.FromResult<List<Transaction>?[]>([]);
            var trT = full ? Safe(() => sleeper.TrendingAdds()) : Task.FromResult<List<Trending>?>(null);
            await Task.WhenAll(mT, rT, gT, pT, tT, trT);

            Weeks[Week] = mT.Result ?? Weeks.GetValueOrDefault(Week) ?? [];
            Rosters = rT.Result ?? Rosters;
            if (gT.Result.Count > 0) Games = gT.Result;
            if (pT.Result is { Count: > 0 } p) SetProjections(p);
            if (full)
            {
                Moves = tT.Result.SelectMany(x => x ?? []).Where(m => m.Status == "complete").OrderByDescending(m => m.Created).ToList();
                if (trT.Result is { } tr) Trending = tr;
            }
            await FillMissingPlayers();
            Rebuild();
            UpdatedAt = DateTimeOffset.Now;
            Error = null;
        }
        catch (Exception ex)
        {
            Error = "Refresh failed. Showing the last scores we had.";
            Console.Error.WriteLine(ex);
        }
        Refreshing = false;
        Changed?.Invoke();
    }

    async Task<List<NflGame>> SafeGames()
    {
        try { return await espn.Scoreboard(); } catch (Exception ex) { Console.Error.WriteLine(ex); return []; }
    }

    static async Task<T?> Safe<T>(Func<Task<T?>> f) where T : class
    {
        try { return await f(); } catch (Exception ex) { Console.Error.WriteLine(ex); return null; }
    }

    void SetProjections(List<StatRow> rows)
    {
        Proj = rows.GroupBy(r => r.PlayerId).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in rows)
            if (r.Player is { } p) { p.PlayerId = r.PlayerId; Players[r.PlayerId] = p; }
        projLoadedAt = DateTimeOffset.Now;
    }

    /// <summary>Anyone on a roster or in a recent move who isn't in the projections feed gets looked up one by one.</summary>
    async Task FillMissingPlayers()
    {
        var ids = Rosters.SelectMany(r => r.Players ?? [])
            .Concat(Moves.SelectMany(m => (m.Adds?.Keys ?? Enumerable.Empty<string>()).Concat(m.Drops?.Keys ?? Enumerable.Empty<string>())))
            .Where(id => id != "0" && !Players.ContainsKey(id)).Distinct().Take(40).ToList();
        if (ids.Count == 0) return;
        var found = await Task.WhenAll(ids.Select(async id => (id, p: await Safe(() => sleeper.Player(id)))));
        foreach (var (id, p) in found)
            Players[id] = p ?? new PlayerInfo { PlayerId = id, LastName = id.All(char.IsLetter) ? $"{id} D/ST" : "Unknown player", Position = id.All(char.IsLetter) ? "DEF" : "?" };
    }

    void Rebuild()
    {
        var scoring = League?.ScoringSettings ?? [];
        projPts = Proj.ToDictionary(kv => kv.Key, kv => Score(kv.Value.Stats));
        ownerOf = [];
        foreach (var r in Rosters)
            foreach (var p in r.Players ?? []) ownerOf[p] = r;
        gameByTeam = [];
        foreach (var g in Games) { gameByTeam[g.Home.Abbr] = g; gameByTeam[g.Away.Abbr] = g; }

        // Season points per player, from the league's own weekly scoring (weeks already finished).
        seasonPts = [];
        foreach (var (w, ms) in Weeks)
        {
            if (w >= Week) continue;
            var seen = new HashSet<string>();
            foreach (var m in ms)
                foreach (var (pid, pts) in m.PlayersPoints ?? [])
                {
                    if (pts == 0 || !seen.Add(pid)) continue;
                    var (t, n) = seasonPts.GetValueOrDefault(pid);
                    seasonPts[pid] = (t + pts, n + 1);
                }
        }
    }

    // ───────────── Scoring and player helpers ─────────────

    /// <summary>Fantasy points for a stat line under this league's scoring rules.</summary>
    public double Score(Dictionary<string, double?>? stats)
    {
        if (stats is null || League is null) return 0;
        double total = 0;
        foreach (var (k, v) in stats)
            if (v is { } val && League.ScoringSettings.TryGetValue(k, out var mult) && mult is { } m) total += val * m;
        return Math.Round(total, 2);
    }

    public PlayerInfo? Info(string? pid) => pid is not null && Players.TryGetValue(pid, out var p) ? p : null;
    public string Name(string? pid) => Info(pid)?.FullName is { Length: > 0 } n ? n : pid == "0" || pid is null ? "Empty" : "Player";
    public string Short(string? pid) => Info(pid)?.ShortName is { Length: > 0 } n ? n : Name(pid);
    public string Last(string? pid) => Info(pid)?.LastName is { Length: > 0 } n ? n : Name(pid);
    public string Pos(string? pid) => Info(pid)?.Position ?? "";
    public string? NflTeam(string? pid) => Info(pid)?.Team;
    public string? Injury(string? pid) => Info(pid)?.InjuryStatus;

    static readonly HashSet<string> OutTags = ["Out", "IR", "Sus", "PUP", "NA", "DNR", "COV"];
    public bool IsOut(string? pid) => Injury(pid) is { } s && OutTags.Contains(s);

    public NflGame? Game(string? pid) => NflTeam(pid) is { } t && gameByTeam.TryGetValue(t, out var g) ? g : null;
    public NflGame? GameForTeam(string team) => gameByTeam.GetValueOrDefault(team);
    public bool OnBye(string? pid) => NflTeam(pid) is { } t && Games.Count > 0 && !gameByTeam.ContainsKey(t);
    public bool Locked(string? pid) => Game(pid)?.HasStarted ?? false;

    /// <summary>Projected points this week, with injured-out and bye players counted as 0.</summary>
    public double ProjOf(string? pid) =>
        pid is null || IsOut(pid) || OnBye(pid) ? 0 : projPts.GetValueOrDefault(pid);

    public string? Opponent(string? pid)
    {
        var team = NflTeam(pid);
        if (team is null) return null;
        if (Game(pid) is { } g) return g.Home.Abbr == team ? $"vs {g.Away.Abbr}" : $"@ {g.Home.Abbr}";
        return OnBye(pid) ? "BYE" : null;
    }

    public double LivePoints(string? pid)
    {
        if (pid is null) return 0;
        foreach (var m in ThisWeek)
            if (m.PlayersPoints?.TryGetValue(pid, out var p) == true) return p;
        return 0;
    }

    public double SeasonAvg(string pid) => seasonPts.TryGetValue(pid, out var s) && s.Games > 0 ? s.Total / s.Games : 0;
    public double SeasonTotal(string pid) => seasonPts.TryGetValue(pid, out var s) ? s.Total : 0;
    public int TrendCount(string pid) => Trending.FirstOrDefault(t => t.PlayerId == pid)?.Count ?? 0;

    // ───────────── Teams ─────────────

    public Roster? MyRoster => Rosters.FirstOrDefault(r => r.OwnerId == MyOwnerId) ?? Rosters.FirstOrDefault(r => r.OwnerId == DefaultOwnerId);
    public Roster? RosterById(int id) => Rosters.FirstOrDefault(r => r.RosterId == id);
    public Roster? OwnerOf(string pid) => ownerOf.GetValueOrDefault(pid);
    public bool IsMine(Roster? r) => r is not null && r.RosterId == MyRoster?.RosterId;
    public LeagueUser? User(Roster? r) => Users.FirstOrDefault(u => u.UserId == r?.OwnerId);

    public string TeamName(Roster? r)
    {
        var u = User(r);
        return u?.Meta("team_name") is { Length: > 0 } t ? t.Trim() : u?.DisplayName ?? $"Team {r?.RosterId}";
    }

    public string? AvatarUrl(Roster? r)
    {
        var u = User(r);
        return u?.Meta("avatar") is { Length: > 0 } a ? a : u?.Avatar is { Length: > 0 } id ? SleeperClient.Avatar(id) : null;
    }

    public int FaabLeft(Roster? r) => (League?.Setting("waiver_budget") ?? 0) - (r?.Setting("waiver_budget_used") ?? 0);

    public async Task SetMyTeamAsync(string ownerId)
    {
        MyOwnerId = ownerId;
        try { await js.InvokeVoidAsync("localStorage.setItem", OwnerKey, ownerId); } catch { }
        Changed?.Invoke();
    }

    // ───────────── Lineup slots ─────────────

    public List<string> StarterSlots => League?.RosterPositions.Where(p => p is not ("BN" or "IR" or "TAXI")).ToList() ?? [];

    public static string SlotLabel(string slot) => slot switch
    {
        "FLEX" => "FLEX",
        "SUPER_FLEX" => "SFLX",
        "REC_FLEX" => "W/T",
        "WRRB_FLEX" => "W/R",
        "DEF" => "DST",
        _ => slot,
    };

    public bool Fits(string slot, string pid)
    {
        var positions = Info(pid)?.FantasyPositions ?? (Info(pid)?.Position is { } p ? [p] : []);
        string[] allowed = slot switch
        {
            "FLEX" => ["RB", "WR", "TE"],
            "SUPER_FLEX" => ["QB", "RB", "WR", "TE"],
            "REC_FLEX" => ["WR", "TE"],
            "WRRB_FLEX" => ["RB", "WR"],
            _ => [slot],
        };
        return positions.Any(allowed.Contains);
    }

    // ───────────── Matchups ─────────────

    public List<Matchup> ThisWeek => Weeks.GetValueOrDefault(Week) ?? [];

    public MatchupView? MyMatchup()
    {
        var mine = ThisWeek.FirstOrDefault(m => m.RosterId == MyRoster?.RosterId);
        return mine?.MatchupId is { } id ? BuildMatchup(id) : null;
    }

    public MatchupView? BuildMatchup(int matchupId, int? week = null)
    {
        var w = week ?? Week;
        var pair = (Weeks.GetValueOrDefault(w) ?? []).Where(m => m.MatchupId == matchupId).ToList();
        if (pair.Count == 0) return null;
        // Put "my" team on the left when it's in this game.
        pair = pair.OrderByDescending(m => m.RosterId == MyRoster?.RosterId).ToList();
        var a = BuildSide(pair[0], w);
        var b = pair.Count > 1 ? BuildSide(pair[1], w) : null;
        var win = b is null ? 1 : WinChance(a, b);
        return new MatchupView(matchupId, w, a, b, win);
    }

    MatchupSide BuildSide(Matchup m, int week)
    {
        var slots = StarterSlots;
        var lines = new List<SlotLine>();
        var live = week == Week;
        for (var i = 0; i < slots.Count; i++)
        {
            var pid = m.Starters is { } s && i < s.Count ? s[i] : "0";
            var pts = m.StartersPoints is { } sp && i < sp.Count ? sp[i] : 0;
            var proj = live && pid != "0" ? ProjOf(pid) : 0;
            var rem = live && pid != "0" ? Game(pid)?.RemainingFraction ?? 0 : 0;
            lines.Add(new SlotLine(slots[i], pid == "0" ? null : pid, pts, proj, pts + proj * rem, Math.Pow(0.5 * proj, 2) * rem, rem));
        }
        var roster = RosterById(m.RosterId);
        return new MatchupSide(roster, m.RosterId, TeamName(roster), AvatarUrl(roster), m.Points, lines);
    }

    /// <summary>
    /// Chance the left team wins: each player's remaining projection is treated as a guess with about
    /// a 50% spread, then we ask how often the left team ends up ahead (a normal curve).
    /// </summary>
    static double WinChance(MatchupSide a, MatchupSide b)
    {
        var diff = a.Expected - b.Expected;
        var sd = Math.Sqrt(a.Variance + b.Variance);
        if (sd < 0.01) return diff > 0 ? 1 : diff < 0 ? 0 : 0.5;
        return Phi(diff / sd);
    }

    static double Phi(double x)
    {
        // Abramowitz-Stegun approximation of the normal CDF.
        var t = 1 / (1 + 0.2316419 * Math.Abs(x));
        var d = 0.3989423 * Math.Exp(-x * x / 2);
        var p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
        return x > 0 ? 1 - p : p;
    }

    /// <summary>This week's projected final for every team, for the "beat the league median" race.</summary>
    public List<(Roster Roster, double Expected)> MedianRace() =>
        ThisWeek.Select(m => (RosterById(m.RosterId)!, BuildSide(m, Week).Expected)).Where(x => x.Item1 is not null)
            .OrderByDescending(x => x.Expected).ToList();

    public bool PlaysMedian => League?.Setting("league_average_match") == 1;

    // ───────────── Standings and power rankings ─────────────

    public List<StandingRow> Standings()
    {
        var done = Weeks.Where(kv => kv.Key < Week && kv.Value.Any(m => m.Points > 0)).OrderBy(kv => kv.Key).ToList();
        var rows = Rosters.Select(r =>
        {
            int apW = 0, apL = 0;
            var recent = new List<double>();
            var weekly = new List<double>();
            foreach (var (_, ms) in done)
            {
                var me = ms.FirstOrDefault(m => m.RosterId == r.RosterId);
                if (me is null) continue;
                weekly.Add(me.Points);
                foreach (var o in ms.Where(o => o.RosterId != r.RosterId))
                {
                    if (me.Points > o.Points) apW++;
                    else if (me.Points < o.Points) apL++;
                }
            }
            var games = r.Wins + r.Losses + r.Ties;
            var apPct = apW + apL == 0 ? 0 : (double)apW / (apW + apL);
            var last3 = weekly.TakeLast(3).DefaultIfEmpty(0).Average();
            return new StandingRow(r, TeamName(r), AvatarUrl(r), r.Wins, r.Losses, r.Ties, r.PointsFor, r.PointsAgainst,
                apW, apL, apPct, r.Wins - apPct * games, last3, r.Meta("streak"), r.Meta("record"), weekly);
        }).OrderByDescending(x => x.Wins).ThenByDescending(x => x.PointsFor).ToList();

        var maxLast3 = rows.Select(r => r.Last3Avg).DefaultIfEmpty(1).Max();
        var power = rows.OrderByDescending(r => r.AllPlayPct * 0.7 + (maxLast3 > 0 ? r.Last3Avg / maxLast3 : 0) * 0.3).ToList();
        for (var i = 0; i < rows.Count; i++) { rows[i].Rank = i + 1; }
        for (var i = 0; i < power.Count; i++) { power[i].PowerRank = i + 1; }
        return rows;
    }

    /// <summary>
    /// Plays out the rest of the regular season 2,000 times. This week uses live scores plus projections;
    /// later weeks use each team's real schedule, scoring average and week-to-week spread.
    /// A fixed random seed keeps the numbers from jumping around on every refresh.
    /// </summary>
    public Dictionary<int, PlayoffChance> PlayoffOdds(int sims = 2000)
    {
        var rng = new Random(12);
        var teams = Rosters.Select(r => r.RosterId).ToList();
        var form = Standings().ToDictionary(x => x.Roster.RosterId, x =>
        {
            var w = x.Weekly.Count > 0 ? x.Weekly : [100.0];
            var mean = w.Average() * 0.6 + w.TakeLast(4).Average() * 0.4;
            var sd = w.Count > 1 ? Math.Sqrt(w.Sum(v => (v - w.Average()) * (v - w.Average())) / (w.Count - 1)) : 20;
            return (mean, sd: Math.Max(sd, 8));
        });
        var thisWeek = ThisWeek.ToDictionary(m => m.RosterId, m => BuildSide(m, Week));
        var remaining = Enumerable.Range(Week, Math.Max(0, PlayoffStart - Week)).Where(w => Weeks.ContainsKey(w)).ToList();
        var hits = teams.ToDictionary(t => t, _ => 0);
        var byes = teams.ToDictionary(t => t, _ => 0);
        var seedSum = teams.ToDictionary(t => t, _ => 0.0);

        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

        for (var s = 0; s < sims; s++)
        {
            var wins = Rosters.ToDictionary(r => r.RosterId, r => r.Wins + r.Ties * 0.5);
            var pf = Rosters.ToDictionary(r => r.RosterId, r => r.PointsFor);
            foreach (var w in remaining)
            {
                var score = new Dictionary<int, double>();
                foreach (var t in teams)
                    score[t] = w == Week && thisWeek.TryGetValue(t, out var side)
                        ? side.Expected + Math.Sqrt(side.Variance) * Gauss()
                        : form[t].mean + form[t].sd * Gauss();
                foreach (var g in Weeks[w].GroupBy(m => m.MatchupId).Where(g => g.Key is not null && g.Count() == 2))
                {
                    var (x, y) = (g.First().RosterId, g.Last().RosterId);
                    if (score[x] > score[y]) wins[x]++; else wins[y]++;
                }
                if (PlaysMedian)
                    foreach (var t in score.OrderByDescending(kv => kv.Value).Take(teams.Count / 2)) wins[t.Key]++;
                foreach (var t in teams) pf[t] += score[t];
            }
            var order = teams.OrderByDescending(t => wins[t]).ThenByDescending(t => pf[t]).ToList();
            for (var i = 0; i < order.Count; i++)
            {
                seedSum[order[i]] += i + 1;
                if (i < PlayoffTeams) hits[order[i]]++;
                if (i < 2) byes[order[i]]++;
            }
        }
        return teams.ToDictionary(t => t, t => new PlayoffChance(hits[t] / (double)sims, byes[t] / (double)sims, seedSum[t] / sims));
    }

    public int PlayoffTeams => League?.Setting("playoff_teams", 6) ?? 6;
    public int PlayoffStart => League?.Setting("playoff_week_start", 15) ?? 15;

    // ───────────── Free agents ─────────────

    public IEnumerable<string> FreeAgents => Proj.Keys.Where(pid => !ownerOf.ContainsKey(pid));

    /// <summary>The lowest-projected of my starters who could be swapped out for a player at this position.</summary>
    public (string? Pid, double Proj) WeakestStarterFor(string position)
    {
        var r = MyRoster;
        if (r?.Starters is null) return (null, 0);
        var slots = StarterSlots;
        (string? pid, double proj) worst = (null, double.MaxValue);
        for (var i = 0; i < slots.Count && i < r.Starters.Count; i++)
        {
            var slot = slots[i];
            var ok = slot == position || (slot == "FLEX" && position is "RB" or "WR" or "TE")
                     || (slot == "SUPER_FLEX" && position is "QB" or "RB" or "WR" or "TE");
            if (!ok) continue;
            var pid = r.Starters[i];
            var proj = pid == "0" ? 0 : ProjOf(pid);
            if (proj < worst.proj) worst = (pid == "0" ? null : pid, proj);
        }
        return worst.proj == double.MaxValue ? (null, 0) : worst;
    }

    // ───────────── One player's season ─────────────

    public async Task<List<WeekPoints>> PlayerSeasonAsync(string pid)
    {
        if (!playerWeeks.TryGetValue(pid, out var weeks))
        {
            weeks = await Safe(() => sleeper.PlayerWeeks(pid, Season)) ?? [];
            playerWeeks[pid] = weeks;
        }
        var list = new List<WeekPoints>();
        for (var w = 1; w <= Math.Max(Week, 1); w++)
        {
            var row = weeks.GetValueOrDefault(w.ToString());
            double? league = null;
            foreach (var m in Weeks.GetValueOrDefault(w) ?? [])
                if (m.PlayersPoints?.TryGetValue(pid, out var p) == true) { league = p; break; }
            if (row is null && w < Week || w == Week && !Locked(pid)) { list.Add(new WeekPoints(w, null, row?.Opponent, null)); continue; }
            var pts = league ?? (row is null ? null : Score(row.Stats));
            var rank = row?.Stats?.GetValueOrDefault("pos_rank_ppr");
            list.Add(new WeekPoints(w, pts, row?.Opponent, rank is { } rk ? (int)rk : null));
        }
        return list;
    }

    public ValueTask DisposeAsync()
    {
        cts.Cancel();
        return ValueTask.CompletedTask;
    }
}

public sealed record SlotLine(string Slot, string? Pid, double Points, double Proj, double Expected, double Variance, double Remaining);

public sealed record MatchupSide(Roster? Roster, int RosterId, string Name, string? Avatar, double Points, List<SlotLine> Lines)
{
    public double Expected => Lines.Sum(l => l.Expected);
    public double Variance => Lines.Sum(l => l.Variance);
    public double Projected => Lines.Sum(l => l.Points + l.Proj * l.Remaining);
    public int YetToPlay => Lines.Count(l => l.Pid is not null && l.Remaining >= 0.999 && l.Proj > 0);
    public int Playing => Lines.Count(l => l.Pid is not null && l.Remaining is > 0 and < 0.999);
}

public sealed record MatchupView(int MatchupId, int Week, MatchupSide A, MatchupSide? B, double WinA);

public sealed record StandingRow(Roster Roster, string Name, string? Avatar, int Wins, int Losses, int Ties, double PointsFor,
    double PointsAgainst, int AllPlayWins, int AllPlayLosses, double AllPlayPct, double Luck, double Last3Avg, string? Streak,
    string? RecordString, List<double> Weekly)
{
    public int Rank { get; set; }
    public int PowerRank { get; set; }
}

public sealed record PlayoffChance(double MakePlayoffs, double TopTwo, double AvgSeed);

public sealed record WeekPoints(int Week, double? Points, string? Opponent, int? PosRank);
