using System.Runtime.CompilerServices;
using RosterRiot.Models;

namespace RosterRiot.Services;

/// <summary>
/// The trade advisor.
///
/// Every player gets a points guess for each week left (through the fantasy playoffs):
///   50% Sleeper's projection for that week + 30% his season average + 20% his last 3 games.
/// A week where Sleeper projects him for nothing (bye, injured, cut) counts as 0.
/// Add those up and you get his rest-of-season (ROS) points. His trade value is ROS points above
/// the best free agents at his position, because nobody trades for a guy you could pick up for free.
///
/// A trade is judged by what it does to each team's best starting lineup, week by week:
/// we re-run the lineup optimizer on both rosters before and after the swap.
/// </summary>
public sealed class TradeService(LeagueService s, SleeperClient sleeper)
{
    public const double ProjWeight = 0.5, SeasonWeight = 0.3, RecentWeight = 0.2;
    static readonly string[] RankPositions = ["QB", "RB", "WR", "TE", "K"];

    // What the Trades page was showing, so it survives a trip to a player card and back.
    public string Tab { get; set; } = "ideas";
    public int? PartnerId { get; set; }
    public HashSet<string> Give { get; } = [];
    public HashSet<string> Get { get; } = [];

    Task? loadTask;
    readonly Dictionary<int, Dictionary<string, double>> weekProj = [];
    List<int> loadedWeeks = [];
    public bool Ready { get; private set; }
    public string? Error { get; private set; }

    // ───────────── League calendar ─────────────

    public int Deadline => s.League?.Setting("trade_deadline", 0) ?? 0;
    public int PlayoffStart => s.PlayoffStart;

    /// <summary>Last fantasy playoff week: 8 playoff teams means 3 rounds (weeks 15, 16, 17).</summary>
    public int LastWeek => Math.Min(18, PlayoffStart + (int)Math.Ceiling(Math.Log2(Math.Max(2, s.PlayoffTeams))) - 1);

    /// <summary>Once anyone in the league has points this week, this week is mostly locked, so a trade helps from next week on.</summary>
    public bool WeekStarted => s.ThisWeek.Any(m => m.Points > 0);
    public int FirstWeek => WeekStarted ? s.Week + 1 : s.Week;

    /// <summary>The weeks a trade made now can still help.</summary>
    public List<int> Weeks { get; private set; } = [];
    public int PlayoffWeeks => Weeks.Count(w => w >= PlayoffStart);

    public string DeadlineText
    {
        get
        {
            var d = Deadline;
            if (d <= 0) return "none set";
            var gap = d - s.Week;
            return gap switch { < 0 => "passed", 0 => "this week", 1 => "next week", _ => $"in {gap} weeks" };
        }
    }

    // ───────────── Loading ─────────────

    public Task EnsureLoadedAsync() => loadTask ??= LoadAsync();

    async Task LoadAsync()
    {
        try
        {
            await s.EnsureLoadedAsync();
            var weeks = Enumerable.Range(s.Week, Math.Max(0, LastWeek - s.Week + 1)).ToList();
            // This week's projections are already loaded (and kept fresh) by LeagueService.
            var scoring = s.League?.ScoringSettings ?? [];
            var tasks = weeks.Where(w => w != s.Week).ToDictionary(w => w, w => sleeper.ProjectedPoints(s.Season, w, scoring));
            await Task.WhenAll(tasks.Values);
            foreach (var (w, t) in tasks) weekProj[w] = t.Result;
            loadedWeeks = weeks;
            Ready = true;
            Error = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Error = "Couldn't load projections for the rest of the season. Tap refresh to try again.";
            loadTask = null;
        }
    }

    // ───────────── Caches (rebuilt when rosters or projections change) ─────────────

    string stamp = "";
    readonly Dictionary<string, PlayerData> data = [];
    Dictionary<string, List<double>> history = [];
    Dictionary<string, double> baseline = [];
    Dictionary<int, Dictionary<string, PosRank>> ranks = [];
    Dictionary<int, Dictionary<string, double>> startShare = [];
    List<TradeIdea>? ideas;
    List<Target> targets = [];
    int[] slotOrder = [];
    PlayerData[] freeAgents = [];

    void Sync()
    {
        var key = $"{loadedWeeks.Count}|{s.Week}|{FirstWeek}|{s.MyRoster?.RosterId}|{RuntimeHelpers.GetHashCode(s.Proj)}|" +
                  string.Join(";", s.Rosters.Select(r => $"{r.RosterId}:{string.Join(",", r.Players ?? [])}:{string.Join(",", r.Reserve ?? [])}"));
        if (key == stamp) return;
        stamp = key;
        data.Clear();
        ideas = null;
        targets = [];
        startShare = [];
        Weeks = loadedWeeks.Where(w => w >= FirstWeek).ToList();

        var slots = s.StarterSlots;
        slotOrder = Enumerable.Range(0, slots.Count).OrderBy(i => slots[i].Contains("FLEX") ? 1 : 0).ToArray();

        // Each player's games this season in league scoring, oldest first (from the league's own matchups).
        history = [];
        for (var w = 1; w < s.Week; w++)
        {
            var seen = new HashSet<string>();
            foreach (var m in s.Weeks.GetValueOrDefault(w) ?? [])
                foreach (var (pid, pts) in m.PlayersPoints ?? [])
                    if (pts != 0 && seen.Add(pid))
                        (history.TryGetValue(pid, out var list) ? list : history[pid] = []).Add(pts);
        }

        // Free-agent baseline: the average ROS points of the 3 best free agents at each position.
        var owned = s.Rosters.SelectMany(r => r.Players ?? []).ToHashSet();
        var pool = weekProj.Values.SelectMany(d => d.Keys).Concat(s.Proj.Keys).Distinct().Where(p => !owned.Contains(p)).ToList();
        baseline = RankPositions.ToDictionary(pos => pos, pos =>
            pool.Where(p => s.Pos(p) == pos).Select(p => Data(p).Ros).OrderByDescending(x => x).Take(3).DefaultIfEmpty(0).Average());

        // If a trade leaves a hole, a team would grab a free agent, so every lineup can use one
        // stand-in free agent per position who scores the baseline spread evenly over the weeks left.
        freeAgents = RankPositions.Select(pos => new PlayerData($"FA:{pos}", pos,
            Enumerable.Repeat(baseline[pos] / Math.Max(1, Weeks.Count), Weeks.Count).ToArray(), new double[Weeks.Count],
            null, null, 0, slots.Select(slot => SlotTakes(slot, pos)).ToArray(), baseline[pos], 0)).ToArray();

        ranks = s.Rosters.ToDictionary(r => r.RosterId, BuildRanks);
    }

    static bool SlotTakes(string slot, string pos) => slot switch
    {
        "FLEX" => pos is "RB" or "WR" or "TE",
        "SUPER_FLEX" => pos is "QB" or "RB" or "WR" or "TE",
        "REC_FLEX" => pos is "WR" or "TE",
        "WRRB_FLEX" => pos is "RB" or "WR",
        _ => slot == pos,
    };

    // ───────────── Player values ─────────────

    public PlayerData Data(string pid)
    {
        if (data.TryGetValue(pid, out var d)) return d;
        var games = history.GetValueOrDefault(pid) ?? [];
        double? season = games.Count > 0 ? games.Average() : null;
        double? recent = games.Count > 0 ? games.TakeLast(3).Average() : null;
        var proj = new double[Weeks.Count];
        var pts = new double[Weeks.Count];
        for (var i = 0; i < Weeks.Count; i++)
        {
            var w = Weeks[i];
            proj[i] = w == s.Week ? s.ProjOf(pid) : weekProj.GetValueOrDefault(w)?.GetValueOrDefault(pid) ?? 0;
            pts[i] = proj[i] < 0.5 ? 0 : Blend(proj[i], season, recent);
        }
        var fits = s.StarterSlots.Select(slot => s.Fits(slot, pid)).ToArray();
        var playoff = Weeks.Select((w, i) => w >= PlayoffStart ? pts[i] : 0).Sum();
        d = new PlayerData(pid, s.Pos(pid), pts, proj, season, recent, games.Count, fits, pts.Sum(), playoff);
        data[pid] = d;
        return d;
    }

    static double Blend(double proj, double? season, double? recent)
    {
        double sum = ProjWeight * proj, weight = ProjWeight;
        if (season is { } a) { sum += SeasonWeight * a; weight += SeasonWeight; }
        if (recent is { } r) { sum += RecentWeight * r; weight += RecentWeight; }
        return sum / weight;
    }

    /// <summary>Rest-of-season points above the best free agents at his position (never below 0).</summary>
    public double Value(string pid) { Sync(); return V(pid); }

    double V(string pid)
    {
        var d = Data(pid);
        return Math.Max(0, d.Ros - baseline.GetValueOrDefault(d.Pos));
    }

    public double Ros(string pid) { Sync(); return Data(pid).Ros; }
    public double Baseline(string pos) { Sync(); return baseline.GetValueOrDefault(pos); }
    double ValueOf(IEnumerable<string> pids) => pids.Sum(V);

    // ───────────── Lineups ─────────────

    public List<string> Pool(Roster r) => (r.Players ?? []).Where(p => !(r.Reserve?.Contains(p) ?? false)).ToList();
    int MaxRoster => s.League?.RosterPositions.Count(p => p is not ("IR" or "TAXI")) ?? 15;

    /// <summary>Best starting lineup's points for each remaining week. Same idea as Lineup Coach: fill
    /// one-position slots with the best player who fits, then the flex spots with the best left.
    /// A stand-in free agent can fill any spot where the roster has nobody better.</summary>
    double[] Lineup(IReadOnlyList<string> pids, Dictionary<string, int>? starts = null)
    {
        var team = pids.Select(Data).Concat(freeAgents).ToArray();
        var used = new bool[team.Length];
        var totals = new double[Weeks.Count];
        for (var w = 0; w < Weeks.Count; w++)
        {
            Array.Clear(used);
            double total = 0;
            foreach (var k in slotOrder)
            {
                int best = -1;
                double bv = 0;
                for (var i = 0; i < team.Length; i++)
                    if (!used[i] && team[i].Fits[k] && team[i].Pts[w] > bv) { best = i; bv = team[i].Pts[w]; }
                if (best < 0) continue;
                used[best] = true;
                total += bv;
                if (starts is not null && !team[best].Pid.StartsWith("FA:")) starts[team[best].Pid] = starts.GetValueOrDefault(team[best].Pid) + 1;
            }
            totals[w] = total;
        }
        return totals;
    }

    /// <summary>The roster after a trade. A team that gets more players than it sends drops its lowest-ROS guys to make room.</summary>
    (List<string> After, List<string> Dropped) AfterTrade(List<string> pool, IReadOnlyCollection<string> gives, IReadOnlyCollection<string> gets)
    {
        var after = pool.Where(p => !gives.Contains(p)).Concat(gets).ToList();
        var dropped = new List<string>();
        var limit = Math.Max(MaxRoster, pool.Count);
        while (after.Count > limit)
        {
            var low = after.Where(p => !gets.Contains(p)).MinBy(p => Data(p).Ros);
            if (low is null) break;
            after.Remove(low);
            dropped.Add(low);
        }
        return (after, dropped);
    }

    /// <summary>How often (0 to 1) a player is in his team's best lineup over the weeks left.</summary>
    public double StartShare(Roster r, string pid)
    {
        Sync();
        if (!startShare.TryGetValue(r.RosterId, out var share))
        {
            var counts = new Dictionary<string, int>();
            Lineup(Pool(r), counts);
            share = counts.ToDictionary(kv => kv.Key, kv => kv.Value / (double)Math.Max(1, Weeks.Count));
            startShare[r.RosterId] = share;
        }
        return share.GetValueOrDefault(pid);
    }

    // ───────────── Team strength ─────────────

    /// <summary>Rest-of-season points from each team's top QB, 3 RBs, 3 WRs, TE and K (the two FLEX spots usually go to RBs and WRs).</summary>
    static int Need(string pos) => pos is "RB" or "WR" ? 3 : 1;

    Dictionary<string, PosRank> BuildRanks(Roster me)
    {
        double Sum(Roster t, string pos) => Pool(t).Where(p => s.Pos(p) == pos).Select(p => Data(p).Ros).OrderByDescending(x => x).Take(Need(pos)).Sum();
        return RankPositions.ToDictionary(pos => pos, pos =>
        {
            var mine = Sum(me, pos);
            var rank = 1 + s.Rosters.Count(t => t.RosterId != me.RosterId && Sum(t, pos) > mine);
            return new PosRank(pos, rank, mine);
        });
    }

    public Dictionary<string, PosRank> Ranks(Roster r) { Sync(); return ranks.GetValueOrDefault(r.RosterId) ?? []; }

    // ───────────── Checking a trade ─────────────

    /// <summary>Team A sends <paramref name="aGives"/> to team B for <paramref name="bGives"/>.</summary>
    public TradeCheck Evaluate(Roster a, IReadOnlyCollection<string> aGives, Roster b, IReadOnlyCollection<string> bGives)
    {
        Sync();
        var aPool = Pool(a);
        var bPool = Pool(b);
        var (aAfter, aDrop) = AfterTrade(aPool, aGives, bGives);
        var (bAfter, bDrop) = AfterTrade(bPool, bGives, aGives);
        return new TradeCheck([.. Weeks], PlayoffStart, Lineup(aPool), Lineup(aAfter), Lineup(bPool), Lineup(bAfter),
            aDrop, bDrop, ValueOf(bGives), ValueOf(aGives));
    }

    /// <summary>Gains under this many points over the rest of the season are "about even" (about 0.6 a week).</summary>
    public double Threshold => Math.Max(2, 0.6 * Weeks.Count);

    /// <summary>Accept / Fair / Decline from team A's side, with the reason in plain words.</summary>
    public Verdict Judge(TradeCheck c)
    {
        var thr = Threshold;
        var n = Math.Max(1, c.Weeks.Length);
        var g = c.AGain;
        var vb = c.ValueIn - c.ValueOut;
        if (g >= thr)
        {
            var why = $"Your starting lineup scores about {g:0} more points the rest of the way ({g / n:+0.0} a week)";
            why += c.APlayoffGain >= 1 ? $", {c.APlayoffGain:0} of them in the playoff weeks." : ".";
            why += c.BGain <= -thr ? " They lose points, so don't expect a yes." : c.BGain >= thr ? " It helps them too, so they might say yes." : "";
            return new Verdict(VerdictKind.Accept, why);
        }
        if (g <= -thr)
            return new Verdict(VerdictKind.Decline, $"Your starting lineup scores about {-g:0} fewer points the rest of the way ({g / n:0.0} a week)." +
                (vb > 10 ? " You'd get more total value, but it doesn't help you win games this season." : ""));
        if (vb <= -12)
            return new Verdict(VerdictKind.Decline, $"It barely changes your lineup ({g:+0.0;-0.0} pts), and you give up more value than you get back.");
        return new Verdict(VerdictKind.Fair, $"It barely changes your lineup ({g:+0.0;-0.0} pts the rest of the way)." +
            (vb >= 12 ? " You do get more total value, which helps if someone gets hurt." : " Only worth it if you like his upside."));
    }

    // ───────────── Trade ideas ─────────────

    public IReadOnlyList<Target> Targets() { Ideas(); return targets; }

    /// <summary>
    /// For each other team: find their players who'd raise your best lineup, and your players who'd raise theirs.
    /// Try every 1-for-1 and 2-for-1 made from those, keep the ones where both lineups go up and the trade
    /// values are close enough that the other team might say yes, and rank them by how much they help you.
    /// </summary>
    public List<TradeIdea> Ideas()
    {
        Sync();
        if (ideas is not null) return ideas;
        var me = s.MyRoster;
        ideas = [];
        targets = [];
        if (me is null || Weeks.Count == 0) return ideas;

        var n = Weeks.Count;
        var thrMe = Threshold;
        var thrThem = Math.Max(1, 0.3 * n);
        var myPool = Pool(me);
        var myBase = Lineup(myPool).Sum();
        var all = new List<TradeIdea>();
        bool Tradeable(string p) => s.Pos(p) is "QB" or "RB" or "WR" or "TE" && Data(p).Ros > 0;

        foreach (var o in s.Rosters.Where(r => r.RosterId != me.RosterId))
        {
            var oPool = Pool(o);
            var oBase = Lineup(oPool).Sum();
            double MyGain(string[] give, string[] get) => Lineup(AfterTrade(myPool, give, get).After).Sum() - myBase;
            double TheirGain(string[] give, string[] get) => Lineup(AfterTrade(oPool, get, give).After).Sum() - oBase;

            var ins = oPool.Where(Tradeable).Select(p => (p, g: MyGain([], [p]))).Where(x => x.g >= thrMe).OrderByDescending(x => x.g).ToList();
            targets.AddRange(ins.Select(x => new Target(x.p, o, x.g)));
            var outs = myPool.Where(Tradeable).Where(q => TheirGain([q], []) > 0.5).ToList();
            var mySpare = myPool.Where(Tradeable).Where(q => V(q) > 0).ToList();
            var theirSpare = oPool.Where(Tradeable).Where(p => V(p) > 0).ToList();

            void Try(string[] give, string[] get)
            {
                var vIn = ValueOf(get);
                var vOut = ValueOf(give);
                // Would they say yes on names alone? Don't ask for much more than you give, and don't overpay a ton.
                if (vOut < vIn * 0.85 - 4 || vOut > vIn * 1.4 + 6) return;
                var mine = MyGain(give, get);
                if (mine < thrMe) return;
                var theirs = TheirGain(give, get);
                // Long shot: big help for you, and they only lose a little (about half a point a week at most).
                var longShot = theirs < thrThem;
                // ...and they get about as much trade value as they send, or more. That's your pitch.
                if (longShot && (theirs < -0.6 * n || mine < 2 * thrMe || vOut < vIn * 0.95 - 2)) return;
                var score = mine + 0.5 * Math.Min(theirs, mine) - 0.1 * Math.Abs(vIn - vOut) - (give.Length + get.Length - 2) * 1.5;
                all.Add(new TradeIdea(o, give.ToList(), get.ToList(), Evaluate(me, give, o, get), "", score, longShot));
            }

            foreach (var (p, _) in ins)
            {
                foreach (var q in outs) Try([q], [p]);
                // Two of yours for one of theirs (they get depth, you get the better player).
                for (var i = 0; i < outs.Count; i++)
                    foreach (var q2 in mySpare)
                        if (q2 != outs[i] && (!outs.Contains(q2) || outs.IndexOf(q2) > i)) Try([outs[i], q2], [p]);
                // One of yours for two of theirs.
                foreach (var p2 in theirSpare)
                    if (p2 != p && (!ins.Any(x => x.p == p2) || string.CompareOrdinal(p2, p) > 0))
                        foreach (var q in outs) Try([q], [p, p2]);
            }
            // Two for two: one of the trades above plus an extra player from each side.
            foreach (var (p, _) in ins.Take(5))
                foreach (var q in outs)
                    foreach (var q2 in mySpare)
                        if (q2 != q && (!outs.Contains(q2) || outs.IndexOf(q2) > outs.IndexOf(q)))
                            foreach (var p2 in theirSpare)
                                if (p2 != p && (!ins.Take(5).Any(x => x.p == p2) || string.CompareOrdinal(p2, p) > 0))
                                    Try([q, q2], [p, p2]);
        }

        // Best first, but keep variety: at most 2 ideas per team and 1 per player you'd get.
        foreach (var (longShot, max) in new[] { (false, 8), (true, 4) })
        {
            var perTeam = new Dictionary<int, int>();
            var usedGet = new HashSet<string>();
            var count = 0;
            foreach (var idea in all.Where(x => x.LongShot == longShot).OrderByDescending(x => x.Score))
            {
                if (perTeam.GetValueOrDefault(idea.Partner.RosterId) >= 2 || idea.Get.Any(usedGet.Contains)) continue;
                perTeam[idea.Partner.RosterId] = perTeam.GetValueOrDefault(idea.Partner.RosterId) + 1;
                foreach (var p in idea.Get) usedGet.Add(p);
                ideas.Add(idea with { Why = Explain(me, idea) });
                if (++count == max) break;
            }
        }
        targets = targets.OrderByDescending(t => t.Gain).Take(12).ToList();
        return ideas;
    }

    string Explain(Roster me, TradeIdea idea)
    {
        var n = Math.Max(1, Weeks.Count);
        var team = s.TeamName(idea.Partner);
        var myPool = Pool(me);
        var myBase = Lineup(myPool).Sum();
        // The player who matters most is the one who'd add the most to your lineup on his own.
        var main = idea.Get.MaxBy(p => Lineup(AfterTrade(myPool, [], [p]).After).Sum() - myBase)!;
        var posIn = s.Pos(main);
        var myRank = Ranks(me).GetValueOrDefault(posIn)?.Rank ?? 0;
        var give = string.Join(" and ", idea.Give.Select(s.Short));
        var bestGive = idea.Give.MaxBy(p => Data(p).Ros)!;
        var posOut = s.Pos(bestGive);
        var theirRank = Ranks(idea.Partner).GetValueOrDefault(posOut)?.Rank ?? 0;
        var plural = idea.Give.Count > 1;
        var themWk = $"+{idea.Check.BGain / n:0.0} a week";

        // Who you get that would actually be in your lineup most weeks.
        var starts = new Dictionary<string, int>();
        Lineup(AfterTrade(myPool, idea.Give, idea.Get).After, starts);
        var startersIn = idea.Get.Where(p => starts.GetValueOrDefault(p) >= 0.6 * n).OrderByDescending(p => p == main).ToList();
        var weak = myRank >= 7 ? $" at {posIn}, one of your weakest spots ({Ordinal(myRank)} of {s.Rosters.Count})" : "";
        var first = (startersIn.Count >= 2
                        ? $"{string.Join(" and ", startersIn.Select(s.Short))} would both start for you" +
                          (myRank >= 7 ? $" ({posIn} is one of your weakest spots, {Ordinal(myRank)} of {s.Rosters.Count})" : "")
                        : $"{s.Short(main)} would start for you{(weak.Length > 0 ? weak : $" at {posIn}")}") +
                    $", and the trade adds about {idea.Check.AGain / n:0.0} pts a week to your lineup";
        // Be honest when part of the deal is a step down at another position (like swapping QBs).
        var down = idea.Get.Where(x => x != main && s.Pos(x) != posIn)
            .Select(x => (x, g: idea.Give.Where(g => s.Pos(g) == s.Pos(x)).OrderByDescending(g => Data(g).Ros).FirstOrDefault()))
            .FirstOrDefault(t => t.g is not null && Data(t.x).Ros < Data(t.g).Ros - 3);
        // And when you send a starter without getting one back at his spot, say who takes over.
        var hole = idea.Give.FirstOrDefault(g => StartShare(me, g) >= 0.6 && !idea.Get.Any(x => s.Pos(x) == s.Pos(g)) && s.Pos(g) != posIn);
        if (down.g is not null)
            first += $", even though {s.Short(down.x)} is a small step down from {s.Short(down.g)} at {s.Pos(down.x)}.";
        else if (hole is not null)
        {
            var next = starts.Where(kv => s.Pos(kv.Key) == s.Pos(hole) && !idea.Get.Contains(kv.Key)).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            first += next is not null ? $", with {s.Short(next)} taking over for {s.Short(hole)} at {s.Pos(hole)}." : $", if you pick up a free-agent {s.Pos(hole)} for {s.Short(hole)}'s spot.";
        }
        else first += ".";

        var benchHere = idea.Give.All(q => StartShare(me, q) <= 0.4);
        var benchThere = StartShare(idea.Partner, main) <= 0.4;
        string second;
        if (idea.LongShot)
        {
            var pitch = idea.Check.ValueOut >= idea.Check.ValueIn + 3 ? "they get more trade value" : "the trade values are about even";
            second = (idea.Check.BGain < -0.05
                ? $"{team} would lose about {-idea.Check.BGain / n:0.0} pts a week, so you'd have to sell it."
                : $"It barely changes {team}'s lineup, so you'd have to sell it.") +
                $" Your pitch: {pitch} ({idea.Check.ValueOut:0} for {idea.Check.ValueIn:0}).";
        }
        else if (idea.Get.Count > idea.Give.Count && idea.Get.All(p => Data(p).Ros < Data(bestGive).Ros))
            second = $"{team} gets the best player in the deal, {give}, and still gains {themWk}.";
        else if (benchThere)
            second = $"He's mostly on {team}'s bench, and {give} would start for them ({themWk}).";
        else if (theirRank >= 7)
            second = $"{team} is {Ordinal(theirRank)} at {posOut}, so {give} {(plural ? "help" : "helps")} them too ({themWk}).";
        else
            second = $"{team} gets {themWk} from {give}, so they have a reason to say yes.";
        if (benchHere && !idea.LongShot) second = second.TrimEnd('.') + $", and {(plural ? "they mostly sit" : "he mostly sits")} on your bench anyway.";
        return $"{first} {second}";
    }

    public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

public sealed record PlayerData(string Pid, string Pos, double[] Pts, double[] Proj, double? SeasonAvg, double? RecentAvg, int Games,
    bool[] Fits, double Ros, double PlayoffPts);

public sealed record PosRank(string Pos, int Rank, double Pts);

public sealed record Target(string Pid, Roster Owner, double Gain);

public sealed record TradeIdea(Roster Partner, List<string> Give, List<string> Get, TradeCheck Check, string Why, double Score, bool LongShot = false);

/// <summary>Before and after lineup points per remaining week for both teams. "In" = what team A receives.</summary>
public sealed record TradeCheck(int[] Weeks, int PlayoffStart, double[] ABefore, double[] AAfter, double[] BBefore, double[] BAfter,
    List<string> ADrops, List<string> BDrops, double ValueIn, double ValueOut)
{
    public double AGain => AAfter.Sum() - ABefore.Sum();
    public double BGain => BAfter.Sum() - BBefore.Sum();
    public double APlayoffGain => Gain(AAfter, ABefore, playoff: true);
    public double BPlayoffGain => Gain(BAfter, BBefore, playoff: true);
    public double AWeekGain(int i) => AAfter[i] - ABefore[i];
    public double BWeekGain(int i) => BAfter[i] - BBefore[i];
    double Gain(double[] after, double[] before, bool playoff) =>
        Enumerable.Range(0, Weeks.Length).Where(i => (Weeks[i] >= PlayoffStart) == playoff).Sum(i => after[i] - before[i]);
}

public enum VerdictKind { Accept, Fair, Decline }

public sealed record Verdict(VerdictKind Kind, string Reason);
