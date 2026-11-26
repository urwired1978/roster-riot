# Roster Riot

*Your team. Every move.*

A live fantasy football app for **Njigbas in Paris** in **LRCA Fantasy Football** (Sleeper league
`1311997611338649600`). It runs on an iPhone like a real app (Add to Home Screen), and anyone else in the
league can pick their own team on the League tab.

## What it does

Five tabs at the bottom, like a native iPhone app:

| Tab | What you get |
|---|---|
| **Matchup** | Live score vs. this week's opponent, win chance, projected finals, slot-by-slot lineups, the "vs. league median" race, every other matchup in the league, and a link to the **NFL scoreboard** (every game this week, live, with your players in each one) |
| **My Team** | Lineup Coach (best lineup from projections, injury and bye alerts), starters and bench with projections and averages, W/L streak, points every week, position strength vs. the league, plus shortcuts to the Trade advisor and Player Buzz |
| **Moves** | Two parts, switched at the top. **Pickups**: FAAB left, players trending across Sleeper, best free agents still to play, and how much each would beat your weakest starter. **Trades**: deadline countdown, trade ideas that help both teams, a trade checker with an Accept / Fair / Decline call, and every player's rest-of-season trade value |
| **Buzz** | News and social chatter about your players (and this week's opponent's starters) from the last 7 days, with off-field and injury heads-ups. See [Buzz](#buzz) |
| **League** | Standings with the playoff line, **playoff chances** (2,000 simulated seasons), power rankings with all-play record and luck, all scores, and recent trades and pickups (Activity) |

Tap any player anywhere for his **player card**: this week, buzz from the last 7 days, a weekly points chart in
league scoring, the game log, who owns him, and a button to trade for him. Pull down on any screen to refresh.

Scores refresh by themselves every 30 seconds while games are live.

## Trade advisor

Open it from **Moves > Trades**, the "Trade advisor" card on My Team, or "Trade for him" / "Shop him in a
trade" on any player card. The banner shows the league's trade deadline (`trade_deadline`, week 13 here).

- **Ideas**: your rank at each position, then 1-for-1, 2-for-1 and 2-for-2 trades where *both* teams' best
  starting lineups go up and the trade values are close, each explained in a sentence or two. "Long shots"
  (big help for you, the other team loses a little but gets about even value) show only when there are any.
- **Checker**: pick a team and players on each side. You get points gained or lost for both lineups (rest of
  season, playoffs and week by week), the value balance, and **Accept / Fair / Decline** for your team.
- **Values**: your players' rest-of-season points and trade value, and the players who'd help you most.

How the math works:

1. **Weekly points guess.** For each week a trade can still help (next week through the last playoff week,
   15-17 here), a player's guess is 50% Sleeper's projection for that week + 30% his season average + 20% his
   last 3 games, all in league scoring. Sleeper serves projections for future weeks
   (`api.sleeper.com/projections/nfl/{season}/{week}`), so byes and playoff matchups are built in. A week he's
   projected for nothing (bye, injury) counts 0. If he has no games this season, it's projection only.
2. **ROS points** = those weeks added up. **Trade value** = ROS points minus the average of the 3 best free
   agents at his position (you don't trade for a guy you could pick up for free). Never below 0.
3. **Judging a trade.** Re-run the best-lineup optimizer (same idea as Lineup Coach) for both teams, every
   remaining week, before and after. Bench points don't count. A team that ends up with too many players
   drops its lowest-ROS guy, and a team left with a hole can use a free agent (the baseline above).
4. **Verdict.** Gain of about 0.6 pts a week or more = Accept, a loss that size = Decline, in between = Fair
   (or Decline if you give up a lot more value than you get).
5. **Ideas** keep trades where you gain at least ~0.6/week, they gain at least ~0.3/week, and the value they
   send is within about 15% of what they get (and you don't overpay by more than about 40%). Ranked by your gain, with a nudge for even value and simple deals.

The four future weeks of projections are about 2 MB each, so they load only when you open Trades (a few
seconds), read with a streaming parser that keeps just player id → points. The code is in
`RosterRiot/Services/TradeService.cs`, `RosterRiot/Pages/Trades.razor` and `RosterRiot/Components/TradePlayer.razor`.

## How it works

It's a .NET 10 **Blazor WebAssembly** app (C#). There's no server: the phone pulls data straight from
**Sleeper's public API** (league, rosters, matchups, projections, trending) and **ESPN's public scoreboard**
(game clock, scores, TV). Both allow that, so there's nothing to sync and nothing to pay for.

```
iPhone ──> Roster Riot (static files) ──> api.sleeper.app / api.sleeper.com   (league + projections)
                                     ├──> site.api.espn.com                    (live NFL games, player news)
                                     ├──> cbssports.com RSS                    (Buzz headlines)
                                     └──> api.bsky.app                         (Buzz chatter)
```

| File | What it is |
|---|---|
| `RosterRiot/Services/LeagueService.cs` | Loads and refreshes everything; win chance, standings, power rankings, playoff odds |
| `RosterRiot/Services/LineupCoach.cs` | Best-lineup math and alerts |
| `RosterRiot/Services/TradeService.cs` | Trade advisor: rest-of-season values, trade ideas, trade checker |
| `RosterRiot/Services/BuzzService.cs` | Buzz: fetches news and chatter, caches it on the phone, works out alerts |
| `RosterRiot/Services/BuzzRules.cs` | Buzz keyword rules: categories, profanity, spam and betting filters |
| `RosterRiot/Services/ApiClients.cs` | Sleeper and ESPN clients |
| `RosterRiot/Pages/` | One file per screen |
| `RosterRiot/wwwroot/css/app.css` | The look (navy and green from the logo) |
| `roster-riot-*.png` | The original logos; app icons are made from the RR shield |

## Buzz

Buzz looks for things about a player that aren't in the stats but might matter this week: injury and
practice reports, suspensions or legal trouble, personal and off-field stuff, role changes, coach quotes and
weather. It covers every player on your roster and this week's opponent's starters. When you open a player's
card it covers that player too. It looks back over the last 7 days.

**It's a heads-up, not a stat.** Buzz never changes projections or the Lineup Coach. It just tells you
something is worth reading before you set your lineup.

Where it shows up:

- **Buzz tab**: a "Heads up" list, then everything newest first. You can filter by category, "News only",
  or switch to your opponent's starters.
- **Badges** next to player names on Matchup, My Team and the player card. A pink megaphone means off-field or
  legal buzz and an amber cross means injury or practice news, both from the last 3 days. The Buzz tab shows a pink count when one of
  your own players has off-field buzz.
- **Player card**: a Buzz section with that player's items.
- **Matchup / My Team**: a "Player Buzz" shortcut card when there's anything to know.

Every item shows where it came from, when, a short snippet, and an **Open ↗** link to the original. News
outlets are marked **NEWS**. Social posts are marked **UNVERIFIED CHATTER**, because anyone can post anything.

### Sources that work (tested from the browser, which is what the iPhone uses)

| Source | What it gives | Calls |
|---|---|---|
| **ESPN fantasy player news** (`site.api.espn.com/apis/fantasy/v2/games/ffl/news/players?playerId=`) | The best source. RotoWire blurbs (practice reports, injury designations, quotes), plus ESPN stories and videos that name the player | 1 per player |
| **ESPN NFL news** (`site.api.espn.com/apis/site/v2/sports/football/nfl/news`) | League-wide stories, tagged with the ESPN ids of the players they're about | 1 |
| **CBS Sports NFL RSS** (`cbssports.com/rss/headlines/nfl/`) | League-wide headlines, matched to players by name | 1 |
| **Bluesky search** (`api.bsky.app/xrpc/app.bsky.feed.searchPosts`, no login) | Fan, beat-writer and bot chatter. Noisy, so it's filtered hard | 1 per player |

ESPN ids are matched by name from ESPN's team rosters (`/teams/{team}/roster`, kept 3 days). Sleeper's
`espn_id` is blank for most current players. Everything is cached on the phone (localStorage, about 150 KB)
and refreshed every 15 minutes while the app is open. That's about 50 calls per refresh for a full roster
plus the opponent's starters. The refresh button re-checks anything older than 3 minutes. If a source fails,
the Buzz tab shows "Couldn't reach …", keeps the last items it had, and waits 15 minutes before trying again.

Also checked and working, but not used because they repeat the sources above: ESPN's NFL news RSS and
RotoWire's NFL RSS (only about 10 latest items). ESPN's athlete `overview` endpoint has a `news` block too.

**Sleeper** has no news text. Its player objects have `injury_status` and `injury_body_part` (the app already
shows those), but `injury_notes` and `practice_participation` were empty for every player we checked, and
`news_updated` is only a timestamp.

### Sources that don't work, and why

| Source | Why it's out |
|---|---|
| **Reddit** (r/fantasyfootball, r/nfl, team subs) | `reddit.com/…json` and `.rss` return **403 Blocked** to anything not logged in, with no CORS. The API needs a registered app and an OAuth secret, which can't live in a phone app |
| **X / Twitter** | Every page redirects to a login screen. The API needs a paid plan and a secret key |
| **TikTok** | No public API (the research API is for approved academics). The web app's requests need signed, bot-checked tokens |
| **Google News RSS** | Works from a server, but sends no CORS header, so the phone's browser can't read it |
| **Bluesky `public.api.bsky.app`** | Search returns 403 there. `api.bsky.app` works and is used instead (the app falls back to the public host if that ever flips) |
| FantasyPros RSS, Pro Football Reference | No feed / no CORS |

We did not scrape around bot protection or use anyone's login, cookies or a scraping service.

**Later, with a small scheduled job.** Reddit (with its own registered app and OAuth, free for
non-commercial use at about 100 requests a minute) and Google News RSS could be fetched server-side by a GitHub
Actions workflow on a schedule, every 30 minutes for example. It would write a `wwwroot/data/buzz.json` that the
app reads from its own site, so CORS doesn't matter. The Reddit secret would live in GitHub Actions secrets.
One caveat: Reddit sometimes blocks cloud IP ranges. An **AI summary** of the chatter ("what people are saying
about Gibbs this week") would also need a server and an API key, so it fits the same job. It hasn't been built.

### How categories are decided

Plain keyword rules in `BuzzRules.cs`. The first match wins, checked in this order:

| Category | Example words |
|---|---|
| Suspension / legal | suspended, arrested, charged with, police, lawsuit, DUI, investigation, fined |
| Off-field | personal reasons, family emergency, birth of, girlfriend, broke up, funeral, excused, away from the team, bender, partying, nightclub, viral video, feud, holdout, trade request, missed curfew |
| Injury / practice | injury, practice, limited, DNP, questionable, doubtful, ruled out, IR, body parts (ankle, hamstring…), concussion, MRI, illness |
| Role / trade | traded, waived, released, signed, depth chart, benched, named starter, snaps, committee, target share |
| Coach says | coach, coordinator, told reporters, press conference |
| Weather | weather, snow, rain, winds, mph, freezing, storm |

Anything else is "General". Two guards cut down wrong flags. Injury, legal and off-field words only count
within 60 characters of the player's name, so a post listing ten players' injuries doesn't flag them all.
Negated phrases like "won't be fined" are skipped. It's still keyword matching, so expect the odd miss
(sarcasm, jokes).

### Keeping it clean

- Headlines and short snippets only. No images or video from social posts. Bluesky posts are shown as text
  and links are stripped.
- Rude words are masked (`s***`). Social posts with slurs or sexual content, or with Bluesky moderation
  labels (porn, nudity, gore, spam), are dropped.
- Betting and promo posts (odds, parlays, promo codes, sportsbooks) and jersey-shop spam are dropped.
- A Bluesky post is only kept if it hits a category or got real attention (10+ likes and reposts). Each
  player gets at most 8.

## Run it on this PC

Double-click **Roster Riot** (the RR shield) in this folder. A small window starts the app and opens it
in your browser. Keep that window open while you use it, and close it to stop the app. Double-clicking again
while it's running just opens the browser. The shortcut runs `scripts/launch-roster-riot.ps1`.

Or from a terminal:

```powershell
dotnet run --project RosterRiot --urls http://localhost:5084
```

Then open http://localhost:5084. Build output goes to `%LOCALAPPDATA%\RosterRiot` so OneDrive doesn't sync it.

## Put it on an iPhone

Roster Riot is hosted free on GitHub Pages: **https://urwired1978.github.io/roster-riot/**

On the iPhone: open that link in **Safari**, tap **Share**, then **Add to Home Screen**. It opens full screen
with the Roster Riot icon, like a real app.

Every push to `main` rebuilds and redeploys it (`.github/workflows/deploy.yml`, about 2-3 minutes).
`scripts/set-base-path.ps1` points the app at the `/roster-riot/` subfolder Pages serves it from. The
phone picks up a new version the next time the app is opened.

## Change the league or team

`LeagueService.cs` has `LeagueId` and `DefaultOwnerId` at the top. In the app, League tab > "Whose app is
this?" switches teams (saved on that phone).

Built with Claude Code.
