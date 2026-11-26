# Roster Riot

*Your team. Every move.*

A live fantasy football app for **Njigbas in Paris** in **LRCA Fantasy Football** (Sleeper league
`1311997611338649600`). It runs on an iPhone like a real app (Add to Home Screen), and anyone else in the
league can pick their own team on the League tab.

## What it does

| Tab | What you get |
|---|---|
| **Matchup** | Live score vs. this week's opponent, win chance, projected finals, slot-by-slot lineups, the "vs. league median" race, and every other matchup in the league |
| **My Team** | Lineup Coach (best lineup from projections, injury and bye alerts), starters and bench with projections and averages, W/L streak, points every week, position strength vs. the league |
| **League** | Standings with the playoff line, **playoff chances** (2,000 simulated seasons), power rankings with all-play record and luck, all scores, recent trades and pickups |
| **Pickups** | FAAB left, players trending across Sleeper, best free agents still to play, and how much each would beat your weakest starter |
| **Trades** | Trade deadline countdown, trade ideas that help both teams, a trade checker with an Accept / Fair / Decline call, and every player's rest-of-season trade value |
| **NFL** | Every game this week with live scores, down and distance, and which of your (and your opponent's) players are in it |
| **Player card** | Tap any player: this week, a weekly points chart in league scoring, the game log, and who owns him |

Scores refresh by themselves every 30 seconds while games are live.

## Trade advisor

Open it from the **Trades** tab, the "Trade advisor" card on My Team, or "Trade for him" / "Shop him in a
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
                                     └──> site.api.espn.com                    (live NFL games)
```

| File | What it is |
|---|---|
| `RosterRiot/Services/LeagueService.cs` | Loads and refreshes everything; win chance, standings, power rankings, playoff odds |
| `RosterRiot/Services/LineupCoach.cs` | Best-lineup math and alerts |
| `RosterRiot/Services/TradeService.cs` | Trade advisor: rest-of-season values, trade ideas, trade checker |
| `RosterRiot/Services/ApiClients.cs` | Sleeper and ESPN clients |
| `RosterRiot/Pages/` | One file per screen |
| `RosterRiot/wwwroot/css/app.css` | The look (navy and green from the logo) |
| `roster-riot-*.png` | The original logos; app icons are made from the RR shield |

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

The app has to be on the web for the phone to reach it from anywhere. The plan is the same as the earlier
NFL app: a GitHub repo with GitHub Pages (free). `.github/workflows/deploy.yml` publishes it on every
push, and `scripts/set-base-path.ps1` fixes paths for the Pages subfolder. **Nothing has been published yet.**

On the iPhone: open the link in **Safari**, tap **Share**, then **Add to Home Screen**. It opens full screen
with the Roster Riot icon.

## Change the league or team

`LeagueService.cs` has `LeagueId` and `DefaultOwnerId` at the top. In the app, League tab > "Whose app is
this?" switches teams (saved on that phone).

Built with Claude Code.
