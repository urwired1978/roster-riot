using System.Net;
using System.Text.RegularExpressions;
using RosterRiot.Models;

namespace RosterRiot.Services;

/// <summary>
/// Plain keyword rules for Buzz: what an item is about, what to hide, and how to tidy the text.
/// First matching category wins, checked in this order: Legal, Off-field, Injury, Role, Coach, Weather.
/// </summary>
public static partial class BuzzRules
{
    const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly (BuzzCategory Cat, Regex Rx)[] Rules =
    [
        (BuzzCategory.Legal, new(@"\b(suspen(d|ded|sion)|arrest(ed)?|charged with|police|court|lawsuit|sued|dui|dwi|investigat(ion|ed|ing)|league discipline|personal conduct|fined|banned|ped|violation of|jail|warrant|plea|attorney|allegation|accused)\b", Opt)),
        (BuzzCategory.OffField, new(@"\b(personal (reason|reasons|matter)|family (matter|emergency|reasons)|birth of|his (child|baby|son|daughter|wife|girlfriend|mother|father|mom|dad)|girlfriend|broke up|break-?up|split from|divorce|funeral|bereavement|passed away|death of|excused from|away from the team|left the team|not with the team|bender|partying|nightclub|night club|hungover|drunk|viral (video|clip|post)|deleted (his|a) (post|tweet)|instagram|social media|feud|beef with|altercation|fight(ing)? with|sideline (spat|argument|outburst)|insubordinat\w*|hold-?out|holding out|unhappy|trade request|requested a trade|wants out|missed (a )?(meeting|team flight|curfew)|late to (practice|meetings?))\b", Opt)),
        (BuzzCategory.Injury, new(@"\b(injur\w*|practice[ds]?|practicing|limited|dnp|did not practice|full participant|questionable|doubtful|ruled out|out for|game-time decision|injured reserve|ir\b|designation|hamstring|ankle|knee|concussion|protocol|sprain\w*|strain\w*|mri|x-?rays?|surgery|illness|flu|carted|sidelined|banged up|dinged|hurt|neck|shoulder|groin|calf|quad|thumb|foot|toe|hip|wrist|ribs?|achilles|acl|mcl|back spasms|rest day|activated|return(s|ed)? to practice)\b", Opt)),
        (BuzzCategory.Role, new(@"\b(trade[ds]?|trading|waived|released|signed|was cut|been cut|cut by|depth chart|benched|demoted|promoted|starting role|named (the )?starter|lead back|backup|workload|snap (count|share)|snaps|committee|target share|usage|elevated|practice squad|first-team|rb1|wr1|te1|qb1|taking over)\b", Opt)),
        (BuzzCategory.Coach, new(@"\b(coach(es|ing)?|coordinator|offensive coordinator|head coach|told reporters|press conference|presser)\b", Opt)),
        (BuzzCategory.Weather, new(@"\b(weather|snow(y|ing|storm|fall)?|rain(y|ing|ed)?|winds|windy|wind chill|mph|frigid|freezing|storms?|stormy|blizzard|lightning|forecast|cold game|heat index)\b", Opt)),
    ];

    /// <summary>
    /// Picks the category. With a <paramref name="subject"/> (the player's last name), injury, legal and
    /// off-field words only count when they're within 60 characters of his name, so a post listing ten
    /// players' injuries, or "X won't be fined for a hit on him", doesn't flag the wrong guy.
    /// </summary>
    public static BuzzCategory Categorize(string text, string? subject = null)
    {
        foreach (var (cat, rx) in Rules)
            foreach (Match m in rx.Matches(text))
            {
                if (Negated(text, m.Index)) continue;
                if (subject is { Length: > 0 } && cat is BuzzCategory.Injury or BuzzCategory.Legal or BuzzCategory.OffField
                    && !Near(text, m.Index, subject, 60)) continue;
                return cat;
            }
        return BuzzCategory.General;
    }

    static bool Near(string text, int at, string name, int dist)
    {
        for (var i = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.OrdinalIgnoreCase))
            if (Math.Abs(i - at) <= dist) return true;
        return false;
    }

    static readonly string[] Negations = ["won't be", "won’t be", "will not be", "not be", "wasn't", "wasn’t", "avoid", "escape", "no "];

    static bool Negated(string text, int at)
    {
        var before = text[Math.Max(0, at - 14)..at].ToLowerInvariant();
        return Negations.Any(before.Contains);
    }

    public static string Label(BuzzCategory c) => c switch
    {
        BuzzCategory.Injury => "Injury / practice",
        BuzzCategory.Legal => "Suspension / legal",
        BuzzCategory.OffField => "Off-field",
        BuzzCategory.Role => "Role / trade",
        BuzzCategory.Coach => "Coach says",
        BuzzCategory.Weather => "Weather",
        _ => "General",
    };

    public static string Css(BuzzCategory c) => c.ToString().ToLowerInvariant();

    // ───────────── Keeping it clean ─────────────

    // Words that get masked ("s***") wherever they show up.
    static readonly Regex Mild = new(@"\b(f+u+c+k\w*|motherf\w*|sh[i1]+t\w*|bullsh\w*|b[i1]tch\w*|a+ss+hole\w*|bastard\w*|pr[i1]ck\w*|piss\w*|goddamn\w*|damn\w*|wtf|stfu|lmf?ao)\b", Opt);

    // Social posts with any of these are dropped entirely.
    static readonly Regex Hard = new(@"\b(n[i1]gg\w*|f[a@]gg?\w*|retard\w*|porn\w*|onlyfans|nudes?|nsfw|sexy|sex|hookers?|c[u\*]nt\w*|wh[o0]re\w*|slut\w*|rape\w*|cocaine|weed|stoned)\b", Opt);

    // Betting and gambling promos: off-topic for a teen's app, and mostly spam anyway.
    static readonly Regex Gambling = new(@"(\b(promo code|sportsbook|parlay|best bets?|betting|odds|prop bets?|player props|kalshi|prizepicks|draftkings|fanduel|bet365|caesars|underdog fantasy|moneyline|over/under|anytime td|pick of the day|bonus bets?|wager\w*)\b|\s[+-]1\d\d\b)", Opt);

    // Jersey shops and other sales spam that name-drops players.
    static readonly Regex Spam = new(@"(\b(jerseys?|stitched|shop now|buy now|giveaway|discount|merch|limited edition|use code)\b|#\w*jersey|\d+% off)", Opt);

    /// <summary>Bluesky moderation labels that mean we should never show the post.</summary>
    public static readonly HashSet<string> BadLabels = ["porn", "sexual", "nudity", "graphic-media", "gore", "nsfl", "spam", "!hide", "!warn"];

    public static bool IsGambling(string text) => Gambling.IsMatch(text);
    public static bool IsSpam(string text) => Spam.IsMatch(text);
    public static bool IsHard(string text) => Hard.IsMatch(text);

    public static string Mask(string text) =>
        Mild.Replace(text, m => m.Value.Length <= 1 ? "*" : m.Value[0] + new string('*', Math.Min(m.Value.Length - 1, 4)));

    // ───────────── Text tidying ─────────────

    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"(https?://\S+|\b[\w-]+\.(com|be|net|org|ly|co|tv|io|gg|me)/\S*)")] private static partial Regex Urls();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// <summary>Strips HTML and links, decodes entities, squashes whitespace, masks rude words, trims to length.</summary>
    public static string Clean(string? text, int max = 240, bool dropUrls = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = WebUtility.HtmlDecode(Tags().Replace(text, " "));
        if (dropUrls) t = Urls().Replace(t, " ");
        t = Mask(Spaces().Replace(t, " ").Trim());
        if (t.Length <= max) return t;
        var cut = t.LastIndexOf(' ', max);
        return t[..(cut > max / 2 ? cut : max)].TrimEnd(',', '.', ';', ':', '-') + "…";
    }

    // ───────────── Names ─────────────

    static readonly HashSet<string> Suffixes = ["jr", "sr", "ii", "iii", "iv", "v"];

    /// <summary>"Anthony Richardson Sr." → "anthony richardson"; "Amon-Ra St. Brown" → "amon-ra st brown".</summary>
    public static string NormName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.ToLowerInvariant().Replace(".", "").Replace("'", "").Replace("’", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !Suffixes.Contains(p));
        return string.Join(' ', parts);
    }

    /// <summary>Name for searches and headline matching, without "Jr." and friends.</summary>
    public static string SearchName(PlayerInfo p)
    {
        var parts = $"{p.FirstName} {p.LastName}".Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !Suffixes.Contains(x.ToLowerInvariant().TrimEnd('.')));
        return string.Join(' ', parts);
    }

    /// <summary>"Richardson Sr." → "Richardson"; "St. Brown" stays "St. Brown".</summary>
    public static string LastName(PlayerInfo p) =>
        string.Join(' ', (p.LastName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => !Suffixes.Contains(x.ToLowerInvariant().TrimEnd('.'))));

    public static bool Mentions(string text, string phrase) =>
        phrase.Length > 0 && text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
