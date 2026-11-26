using System.Text.Json;
using System.Xml.Linq;
using Microsoft.JSInterop;
using RosterRiot.Models;

namespace RosterRiot.Services;

/// <summary>
/// Player Buzz: news and social chatter about your players (and this week's opponent's starters)
/// from the last 7 days. Everything is fetched straight from the phone, so only sources that allow
/// browser requests (CORS) are used. Results are cached on the phone and refreshed every 15 minutes.
/// Buzz is information only. Nothing here feeds the Lineup Coach or the projections.
/// </summary>
public sealed class BuzzService : IAsyncDisposable
{
    const string CacheKey = "rr.buzz.v1";
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    public static readonly TimeSpan AlertWindow = TimeSpan.FromHours(72);
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);
    static readonly TimeSpan MinAge = TimeSpan.FromMinutes(3);    // a manual refresh never refetches anything newer than this
    const int MaxExtra = 5;
    static readonly TimeSpan TeamIdsFresh = TimeSpan.FromDays(3);

    public const string SrcEspnPlayer = "ESPN player news";
    public const string SrcEspnNfl = "ESPN NFL news";
    public const string SrcCbs = "CBS Sports";
    public const string SrcBluesky = "Bluesky";

    readonly HttpClient http;
    readonly LeagueService s;
    readonly IJSRuntime js;
    readonly CancellationTokenSource cts = new();
    readonly SemaphoreSlim gate = new(4);

    BuzzCache cache = new();
    Dictionary<string, List<BuzzItem>> byPlayer = [];
    readonly Dictionary<string, BuzzSource> sources = [];
    bool started, loaded;
    readonly HashSet<string> triedIds = [];   // players we already looked up an ESPN id for this session

    public BuzzService(HttpClient http, LeagueService s, IJSRuntime js)
    {
        this.http = http;
        this.s = s;
        this.js = js;
        s.Changed += OnLeagueChanged;
    }

    public event Action? Changed;
    public bool Refreshing { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public IEnumerable<BuzzSource> Sources => new[] { SrcEspnPlayer, SrcEspnNfl, SrcCbs, SrcBluesky }
        .Select(n => sources.GetValueOrDefault(n) ?? new BuzzSource(n, true, cache.Buckets.Count > 0 ? "Saved on this phone" : "Not checked yet", null));

    // ───────────── Who we follow ─────────────

    static bool IsTeamDefense(string pid) => pid.All(char.IsLetter);

    public HashSet<string> Mine => (s.MyRoster?.Players ?? []).Where(p => p != "0" && !IsTeamDefense(p)).ToHashSet();

    public HashSet<string> OpponentStarters =>
        (s.MyMatchup()?.B?.Lines ?? []).Select(l => l.Pid).OfType<string>().Where(p => !IsTeamDefense(p)).ToHashSet();

    public List<string> Tracked => Mine.Concat(OpponentStarters).Concat(extra).Distinct().ToList();

    readonly List<string> extra = [];

    /// <summary>Also follow a player whose card is open (a free agent, another team's guy). Only the last few are kept.</summary>
    public void Follow(string pid)
    {
        if (IsTeamDefense(pid) || Tracked.Contains(pid)) return;
        extra.Remove(pid);
        extra.Add(pid);
        if (extra.Count > MaxExtra) extra.RemoveAt(0);
        _ = RefreshAsync();
    }

    public bool IsFollowed(string? pid) => pid is not null && Tracked.Contains(pid);

    // ───────────── Reading ─────────────

    public List<BuzzItem> For(string? pid) => pid is not null && byPlayer.TryGetValue(pid, out var l) ? l : [];

    public List<BuzzItem> All(IEnumerable<string> pids) =>
        pids.SelectMany(For).OrderByDescending(i => i.At).ToList();

    /// <summary>A badge-worthy alert: off-field/legal buzz or injury buzz from the last 72 hours.</summary>
    public BuzzAlert? AlertFor(string? pid)
    {
        var recent = For(pid).Where(i => DateTimeOffset.UtcNow - i.At < AlertWindow).ToList();
        var off = recent.Where(i => i.Category is BuzzCategory.OffField or BuzzCategory.Legal).ToList();
        if (off.Count > 0) return new BuzzAlert(BuzzAlertKind.OffField, off.Count, Best(off), off.All(i => i.Social));
        var inj = recent.Where(i => i.Category == BuzzCategory.Injury).ToList();
        if (inj.Count > 0) return new BuzzAlert(BuzzAlertKind.Injury, inj.Count, Best(inj), inj.All(i => i.Social));
        return null;

        // Lead with the newest real news item; fall back to the newest post.
        static BuzzItem Best(List<BuzzItem> l) => l.FirstOrDefault(i => !i.Social) ?? l[0];
    }

    // ───────────── Loading ─────────────

    void OnLeagueChanged()
    {
        if (!s.Ready) return;
        if (!started) _ = StartAsync();
        else Rebuild();   // roster or matchup may have changed; cheap, and the next tick fetches anyone new
    }

    public async Task StartAsync()
    {
        if (started || !s.Ready) return;
        started = true;
        await LoadCache();
        loaded = true;
        Rebuild();
        Changed?.Invoke();
        _ = Loop();
    }

    /// <summary>Wakes once a minute; only sources older than 15 minutes (or new players) get fetched.</summary>
    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            await RefreshAsync();
            try { await Task.Delay(TimeSpan.FromMinutes(1), cts.Token); } catch (TaskCanceledException) { return; }
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (Refreshing || !s.Ready || !loaded) return;   // the loop's first pass right after loading picks up anything waiting
        var pids = Tracked;
        var now = DateTimeOffset.UtcNow;
        bool Stale(string key, TimeSpan? age = null) =>
            !cache.Buckets.TryGetValue(key, out var b) || now - b.At > (force ? MinAge : age ?? Fresh);

        var work = pids.Any(p => Stale($"espn:{p}") && (cache.EspnIds.ContainsKey(p) || !triedIds.Contains(p)) || Stale($"bsky:{p}"))
                   || Stale("espn-nfl") || Stale("cbs");
        if (!work) return;

        Refreshing = true;
        Changed?.Invoke();
        try
        {
            await ResolveEspnIds(pids);

            var tally = new Dictionary<string, (int Ok, int Fail)>();
            void Count(string src, bool ok)
            {
                var (o, f) = tally.GetValueOrDefault(src);
                tally[src] = ok ? (o + 1, f) : (o, f + 1);
            }

            var jobs = new List<Task>();
            foreach (var pid in pids)
            {
                if (s.Info(pid) is not { } info) continue;
                if (Stale($"espn:{pid}") && cache.EspnIds.TryGetValue(pid, out var eid))
                    jobs.Add(Fetch($"espn:{pid}", SrcEspnPlayer, () => EspnPlayer(pid, eid, info), Count));
                if (Stale($"bsky:{pid}"))
                    jobs.Add(Fetch($"bsky:{pid}", SrcBluesky, () => Bluesky(pid, info), Count));
            }
            if (Stale("espn-nfl")) jobs.Add(Fetch("espn-nfl", SrcEspnNfl, EspnNfl, Count));
            if (Stale("cbs")) jobs.Add(Fetch("cbs", SrcCbs, Cbs, Count));
            await Task.WhenAll(jobs);

            foreach (var (src, (ok, fail)) in tally)
            {
                var host = src == SrcBluesky ? "Bluesky" : src == SrcCbs ? "CBS Sports" : "ESPN";
                sources[src] = fail == 0 ? new BuzzSource(src, true, null, now)
                    : ok == 0 ? new BuzzSource(src, false, $"Couldn't reach {host}", now)
                    : new BuzzSource(src, true, $"{fail} of {ok + fail} lookups failed", now);
            }

            Prune(pids);
            await SaveCache();
            Rebuild();
            UpdatedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        Refreshing = false;
        Changed?.Invoke();
    }

    /// <summary>Runs one source call with the concurrency limit. A failure keeps the old cached items and backs off.</summary>
    async Task Fetch(string key, string src, Func<Task<List<BuzzItem>>> f, Action<string, bool> count)
    {
        await gate.WaitAsync();
        try
        {
            var items = await f();
            cache.Buckets[key] = new BuzzBucket { At = DateTimeOffset.UtcNow, Items = items };
            count(src, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Buzz: {key} failed: {ex.Message}");
            count(src, false);
            // Keep what we had, and wait the normal 15 minutes before trying a blocked source again.
            cache.Buckets[key] = new BuzzBucket { At = DateTimeOffset.UtcNow, Items = cache.Buckets.GetValueOrDefault(key)?.Items ?? [] };
        }
        finally { gate.Release(); }
    }

    /// <summary>Sorts cached items into a newest-first list per player, matching league-wide stories by name or ESPN id.</summary>
    void Rebuild()
    {
        var cutoff = DateTimeOffset.UtcNow - Window;
        var map = new Dictionary<string, List<BuzzItem>>();
        var league = cache.Buckets.GetValueOrDefault("espn-nfl")?.Items ?? [];
        var cbs = cache.Buckets.GetValueOrDefault("cbs")?.Items ?? [];

        foreach (var pid in Tracked)
        {
            var list = new List<BuzzItem>();
            list.AddRange(cache.Buckets.GetValueOrDefault($"espn:{pid}")?.Items ?? []);
            list.AddRange(cache.Buckets.GetValueOrDefault($"bsky:{pid}")?.Items ?? []);

            if (s.Info(pid) is { } info)
            {
                var full = BuzzRules.SearchName(info);
                var last = BuzzRules.LastName(info);
                var eid = cache.EspnIds.GetValueOrDefault(pid);
                foreach (var a in league)
                    if (eid is not null && a.Athletes?.Contains(eid) == true && BuzzRules.Mentions(a.Title + " " + a.Snippet, last))
                        list.Add(Copy(a, pid, last));
                foreach (var a in cbs)
                    if (BuzzRules.Mentions(a.Title + " " + a.Snippet, full) || last.Contains('-') && BuzzRules.Mentions(a.Title + " " + a.Snippet, last))
                        list.Add(Copy(a, pid, last));
            }

            map[pid] = list.Where(i => i.At > cutoff)
                .GroupBy(i => i.Id).Select(g => g.First())
                .GroupBy(i => Key(i.Title)).Select(g => g.OrderBy(i => i.Social).First())   // same headline twice → keep one
                .OrderByDescending(i => i.At).ToList();
        }
        byPlayer = map;

        static string Key(string t) => new string(t.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(70).ToArray());
    }

    /// <summary>A league-wide story copied onto one player, re-categorized for him (it may name several players).</summary>
    static BuzzItem Copy(BuzzItem a, string pid, string last) => new()
    {
        Id = a.Id, Pid = pid, Source = a.Source, Author = a.Author, Social = a.Social, At = a.At, Title = a.Title,
        Snippet = a.Snippet, Url = a.Url, Category = BuzzRules.Categorize(a.Title + " " + a.Snippet, last), Engagement = a.Engagement,
    };

    void Prune(List<string> pids)
    {
        var keep = pids.SelectMany(p => new[] { $"espn:{p}", $"bsky:{p}" }).Append("espn-nfl").Append("cbs").ToHashSet();
        foreach (var k in cache.Buckets.Keys.Where(k => !keep.Contains(k)).ToList()) cache.Buckets.Remove(k);
        var cutoff = DateTimeOffset.UtcNow - Window;
        foreach (var b in cache.Buckets.Values) b.Items.RemoveAll(i => i.At < cutoff);
    }

    async Task LoadCache()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", CacheKey);
            if (json is { Length: > 0 } && JsonSerializer.Deserialize(json, BuzzJson.Default.BuzzCache) is { Version: 1 } c) cache = c;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Buzz cache unreadable, starting fresh: {ex.Message}"); }
    }

    async Task SaveCache()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", CacheKey, JsonSerializer.Serialize(cache, BuzzJson.Default.BuzzCache)); }
        catch (Exception ex) { Console.Error.WriteLine($"Buzz cache not saved: {ex.Message}"); }
    }

    // ───────────── ESPN ids ─────────────

    /// <summary>
    /// Sleeper's espn_id is blank for most current players, so match by name on the ESPN team roster
    /// (one call per NFL team, kept 3 days). Sleeper's id and then ESPN search are the fallbacks.
    /// </summary>
    async Task ResolveEspnIds(List<string> pids)
    {
        var missing = pids.Where(p => !cache.EspnIds.ContainsKey(p) && !triedIds.Contains(p) && s.Info(p)?.Team is not null).ToList();
        if (missing.Count == 0) return;
        triedIds.UnionWith(missing);

        var teams = missing.Select(p => s.Info(p)!.Team!).Distinct()
            .Where(t => !cache.Teams.TryGetValue(t, out var m) || DateTimeOffset.UtcNow - m.At > TeamIdsFresh).ToList();
        await Task.WhenAll(teams.Select(async t =>
        {
            await gate.WaitAsync();
            try { cache.Teams[t] = await EspnTeam(t); }
            catch (Exception ex) { Console.Error.WriteLine($"Buzz: ESPN roster {t} failed: {ex.Message}"); }
            finally { gate.Release(); }
        }));

        foreach (var pid in missing)
        {
            var info = s.Info(pid)!;
            var name = BuzzRules.NormName(info.FullName);
            if (cache.Teams.TryGetValue(info.Team!, out var m) && m.Ids.TryGetValue(name, out var id)) cache.EspnIds[pid] = id;
            else if (info.EspnId is { } e) cache.EspnIds[pid] = e.ToString();
            else if (await EspnSearch(info) is { } found) cache.EspnIds[pid] = found;
        }
    }

    async Task<EspnTeamIds> EspnTeam(string team)
    {
        var abbr = team == "WAS" ? "wsh" : team.ToLowerInvariant();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://site.api.espn.com/apis/site/v2/sports/football/nfl/teams/{abbr}/roster", cts.Token));
        var ids = new Dictionary<string, string>();
        foreach (var group in doc.RootElement.GetProperty("athletes").EnumerateArray())
            foreach (var a in group.GetProperty("items").EnumerateArray())
                if (Str(a, "id") is { } id && Str(a, "fullName") is { } n) ids[BuzzRules.NormName(n)] = id;
        return new EspnTeamIds { At = DateTimeOffset.UtcNow, Ids = ids };
    }

    async Task<string?> EspnSearch(PlayerInfo p)
    {
        try
        {
            var q = Uri.EscapeDataString(BuzzRules.SearchName(p));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(
                $"https://site.web.api.espn.com/apis/common/v3/search?query={q}&limit=5&type=player&sport=football&league=nfl", cts.Token));
            var want = BuzzRules.NormName(p.FullName);
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
                if (BuzzRules.NormName(Str(it, "displayName")) == want) return Str(it, "id");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Buzz: ESPN search failed: {ex.Message}"); }
        return null;
    }

    // ───────────── Sources ─────────────

    /// <summary>ESPN's fantasy feed for one player: RotoWire blurbs (practice reports, quotes) plus ESPN stories that name him.</summary>
    async Task<List<BuzzItem>> EspnPlayer(string pid, string espnId, PlayerInfo info)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(
            $"https://site.api.espn.com/apis/fantasy/v2/games/ffl/news/players?playerId={espnId}&limit=20", cts.Token));
        var items = new List<BuzzItem>();
        if (!doc.RootElement.TryGetProperty("feed", out var feed)) return items;
        var last = BuzzRules.LastName(info);
        var cutoff = DateTimeOffset.UtcNow - Window;
        foreach (var f in feed.EnumerateArray())
        {
            if (!DateTimeOffset.TryParse(Str(f, "published"), out var at) || at < cutoff) continue;
            var type = Str(f, "type");
            var head = BuzzRules.Clean(Str(f, "headline"), 300);
            var desc = Str(f, "description");
            var rotowire = type == "Rotowire";
            // Big ESPN columns get tagged to dozens of players; keep only the ones that name him up top.
            if (!rotowire && !BuzzRules.Mentions(head + " " + desc, last)) continue;
            var snippet = rotowire ? BuzzRules.Clean(Str(f, "story")) : BuzzRules.Clean(desc);
            var text = head + " " + snippet;
            if (head.Length == 0 || BuzzRules.IsGambling(text)) continue;
            items.Add(new BuzzItem
            {
                Id = $"espn:{Str(f, "id")}",
                Pid = pid,
                Source = rotowire ? "RotoWire via ESPN" : type == "Media" ? "ESPN video" : "ESPN",
                At = at,
                Title = head,
                Snippet = snippet == head ? null : snippet,
                Url = WebLink(f) ?? $"https://www.espn.com/nfl/player/_/id/{espnId}",
                Category = BuzzRules.Categorize(text, last),
            });
        }
        return items;
    }

    /// <summary>ESPN's league-wide NFL news (one call). Stories carry the ESPN ids of the players they're about.</summary>
    async Task<List<BuzzItem>> EspnNfl()
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync("https://site.api.espn.com/apis/site/v2/sports/football/nfl/news?limit=100", cts.Token));
        var items = new List<BuzzItem>();
        foreach (var a in doc.RootElement.GetProperty("articles").EnumerateArray())
        {
            if (!DateTimeOffset.TryParse(Str(a, "published"), out var at)) continue;
            var athletes = a.TryGetProperty("categories", out var cats)
                ? cats.EnumerateArray().Where(c => Str(c, "type") == "athlete").Select(c => Str(c, "athleteId")).OfType<string>().ToList()
                : [];
            if (athletes.Count == 0) continue;
            var head = BuzzRules.Clean(Str(a, "headline"), 300);
            var desc = BuzzRules.Clean(Str(a, "description"));
            if (BuzzRules.IsGambling(head + " " + desc)) continue;
            items.Add(new BuzzItem
            {
                Id = $"espn:{Str(a, "id")}",
                Source = "ESPN",
                At = at,
                Title = head,
                Snippet = desc.Length > 0 ? desc : null,
                Url = WebLink(a),
                Category = BuzzRules.Categorize(head + " " + desc),
                Athletes = athletes,
            });
        }
        return items;
    }

    /// <summary>CBS Sports NFL headlines RSS (one call). Matched to players by name.</summary>
    async Task<List<BuzzItem>> Cbs()
    {
        var xml = XDocument.Parse(await http.GetStringAsync("https://www.cbssports.com/rss/headlines/nfl/", cts.Token));
        var items = new List<BuzzItem>();
        foreach (var it in xml.Descendants("item"))
        {
            var head = BuzzRules.Clean(it.Element("title")?.Value, 300);
            var desc = BuzzRules.Clean(it.Element("description")?.Value);
            var link = it.Element("link")?.Value.Trim();
            if (head.Length == 0 || !DateTimeOffset.TryParse(it.Element("pubDate")?.Value.Trim(), out var at)) continue;
            if (BuzzRules.IsGambling(head + " " + desc)) continue;
            items.Add(new BuzzItem
            {
                Id = $"cbs:{link ?? head}",
                Source = "CBS Sports",
                At = at,
                Title = head,
                Snippet = desc.Length > 0 && desc != head ? desc : null,
                Url = link,
                Category = BuzzRules.Categorize(head + " " + desc),
            });
        }
        return items;
    }

    /// <summary>
    /// Bluesky's public post search (no login). This is unverified chatter: we keep posts that hit a
    /// category keyword or got real attention, and drop spam, betting, flagged and rude posts.
    /// Text only: images and video from posts are never shown.
    /// </summary>
    async Task<List<BuzzItem>> Bluesky(string pid, PlayerInfo info)
    {
        var name = BuzzRules.SearchName(info);
        var since = (DateTimeOffset.UtcNow - Window).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var query = $"?q={Uri.EscapeDataString($"\"{name}\"")}&sort=latest&limit=40&lang=en&since={since}";
        string body;
        // api.bsky.app answers browser search requests; public.api.bsky.app currently returns 403 for search.
        try { body = await http.GetStringAsync("https://api.bsky.app/xrpc/app.bsky.feed.searchPosts" + query, cts.Token); }
        catch { body = await http.GetStringAsync("https://public.api.bsky.app/xrpc/app.bsky.feed.searchPosts" + query, cts.Token); }

        using var doc = JsonDocument.Parse(body);
        var items = new List<BuzzItem>();
        var seen = new HashSet<string>();
        foreach (var p in doc.RootElement.GetProperty("posts").EnumerateArray())
        {
            if (!p.TryGetProperty("record", out var rec)) continue;
            var raw = Str(rec, "text") ?? "";
            if (!DateTimeOffset.TryParse(Str(rec, "createdAt"), out var at)) continue;
            if (Labels(p).Concat(p.TryGetProperty("author", out var au0) ? Labels(au0) : []).Any(BuzzRules.BadLabels.Contains)) continue;
            if (BuzzRules.IsHard(raw) || BuzzRules.IsGambling(raw) || BuzzRules.IsSpam(raw)) continue;

            var text = BuzzRules.Clean(raw, 280, dropUrls: true);
            if (text.Length < 25 || !seen.Add(new string(text.ToLowerInvariant().Where(char.IsLetter).Take(60).ToArray()))) continue;
            var cat = BuzzRules.Categorize(text, BuzzRules.LastName(info));
            var engagement = Int(p, "likeCount") + Int(p, "repostCount");
            if (cat == BuzzCategory.General && engagement < 10) continue;

            var author = p.GetProperty("author");
            var handle = Str(author, "handle") ?? "";
            var uri = Str(p, "uri") ?? "";
            items.Add(new BuzzItem
            {
                Id = $"bsky:{uri}",
                Pid = pid,
                Source = "Bluesky",
                Author = "@" + handle,
                Social = true,
                At = at,
                Title = text,
                Url = $"https://bsky.app/profile/{handle}/post/{uri[(uri.LastIndexOf('/') + 1)..]}",
                Category = cat,
                Engagement = engagement,
            });
            if (items.Count >= 8) break;
        }
        return items;

        static IEnumerable<string> Labels(JsonElement el) =>
            el.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array
                ? ls.EnumerateArray().Select(l => Str(l, "val") ?? "") : [];
    }

    // ───────────── JSON helpers ─────────────

    static string? WebLink(JsonElement el) =>
        el.TryGetProperty("links", out var l) && l.TryGetProperty("web", out var w) ? Str(w, "href") : null;

    static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    public ValueTask DisposeAsync()
    {
        s.Changed -= OnLeagueChanged;
        cts.Cancel();
        return ValueTask.CompletedTask;
    }
}
