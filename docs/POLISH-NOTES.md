# Roster Riot: visual polish notes

Working copy: `Chase\feature-polish`. Dev server for this copy: `http://localhost:5095`
(5093 was already taken by the buzz session's static file server, so `.claude/launch.json` uses 5095).

## What looked amateur (review at 375x812, dark, before any changes)

1. **Loading** showed the logo a second time with "Loading the league…" instead of the shape of the page.
2. **Load error** left a blank page under a red banner (no message, no retry button).
3. **Empty states** were a single grey sentence in a card ("Team not found.", "Couldn't load NFL games right now.").
4. **Weekly points chart** was an SVG stretched with `preserveAspectRatio="none"`, so its numbers and week labels were squashed.
5. **Team names cut off without "…"** in Standings and Power (the text clipped mid-letter).
6. **Icons were a mix of emoji and hand-drawn SVG**: 🧠 coach, ⚠️/⛔ alerts, 🔥 trending, 🏈 possession, plus uneven tab bar icons (the "My Team" and "NFL" ones read poorly).
7. **Touch targets under 44px**: refresh button 40px, "‹ Back" text link around 24px, League segment buttons about 35px, position chips about 30px.
8. **No pressed feedback** on most tappable rows and cards.
9. **No spacing or type scale**: about 14 different font sizes (10.5 to 13.5px in half steps) and ad hoc padding.
10. **Game log header** used the big score font for "Pts" only, and numbers weren't right-aligned.
11. **The footer** put the navy wordmark on a white tile, which looked pasted onto the dark theme.
12. **Section headings wrapped** ("🔥 Trending pickups across Sleeper (24h)").
13. **No transitions** between tabs or into the player card; scores and bars jumped when live data changed.
14. **No pull to refresh**, only the header button.

## Changes

| # | Change | Files |
|---|---|---|
| 1 | **Design tokens**: colours (plus `--text-2`, `--panel-3`, `--accent`), a 4px spacing scale (`--sp-1`…`--sp-8`), a type scale (`--fs-2xs`…`--fs-lg` body, `--fd-sm`…`--fd-2xl` display), radii, shadows, motion (`--ease`, `--ease-ios`, `--dur-1/2/3`, `--dur-data`), `--tap: 44px`. `--gold` stays as an alias of `--accent`. The file is now organised into sections with a header comment. No class the pages use was renamed. | `wwwroot/css/app.css` |
| 2 | **Loading skeletons** replace the second logo splash: hero-plus-lineup shape on Matchup, title-plus-rows shape elsewhere, and shimmer bars in the player chart while the season loads. | `Components/Skeleton.razor` (new), `Layout/MainLayout.razor`, `Pages/Player.razor` |
| 3 | **Empty and error states**: icon, title, one line of help, and an optional button. A first-load failure now shows "Can't reach Sleeper" with **Try again** instead of a blank page. A refresh failure shows a compact strip with **Retry**. | `Components/EmptyState.razor` (new), `Layout/MainLayout.razor`, `Pages/Home.razor`, `Team.razor`, `Nfl.razor`, `NotFound.razor` |
| 4 | **One icon set** (24px, round 1.9 strokes): new tab bar icons (scoreboard, jersey, trophy, add-player, football) with a tinted fill and an indicator bar when active. Emoji replaced: 🧠 → clipboard-check, ⚠️/⛔ → alert/out, 🔥 → trending arrow, 🏈 → football glyph, `›`/`‹` → chevrons, header refresh. | `Components/Icon.razor` (new), `Layout/MainLayout.razor`, `Pages/Home.razor`, `Team.razor`, `Waivers.razor`, `Nfl.razor`, `Player.razor` |
| 5 | **Weekly points chart** rebuilt in HTML: crisp numbers at any width, and bar heights ease. Same parameters as before. | `Components/WeekBars.razor` |
| 6 | **Touch targets ≥ 44px**: refresh button, Back button (now a chevron + "Back" with a 44px hit area), banner, select and buttons. Segment buttons are 38px inside a 46px control; chips are 36px. List rows are at least 56-60px. | `app.css`, `Pages/Player.razor` |
| 7 | **Pressed states** (iOS style): cards and tiles scale to .975, buttons and chips to .95, list rows get a highlight, Back dims. iOS only applies `:active` when there's a touchstart listener, so the JS adds one. | `app.css`, `wwwroot/js/rr-polish.js` |
| 8 | **Readability**: team names in Standings and Power now end in "…" instead of clipping; small all-caps column headers; right-aligned numbers in the game log and standings; FAAB tip on its own line; "Trending pickups" heading no longer wraps (the "All of Sleeper · 24h" note moved to the right); a slightly brighter secondary text colour. | `app.css`, `Pages/Waivers.razor`, `Pages/League.razor` (wording only) |
| 9 | **Footer**: small RR shield and wordmark text instead of the navy logo on a white tile. | `Layout/MainLayout.razor`, `app.css` |
| 10 | **Page transitions** (View Transitions API, with a CSS fallback): tabs crossfade with a 6px rise (120ms out, 200ms in). Player, team and matchup cards push in from the right (250ms, iOS curve); Back pops out and **restores your scroll position**. The top bar and tab bar stay still. Re-tapping the current tab scrolls to the top. League segments and Pickups position chips fade their content in. | `wwwroot/js/rr-polish.js` (new), `app.css`, `Pages/Player.razor` (`data-nav="back"`), `Pages/League.razor` and `Waivers.razor` (`fade-in` wrapper with `@key`) |
| 11 | **Live data easing**: when scores refresh, numbers count from the old value to the new one (450ms, ease-out). This covers hero scores, lineup points, player rows, score cards, mini cards, NFL scores, win %, and playoff %. Bars and the win ring glide to their new size. Easing only runs right after a data refresh, so switching filters doesn't make numbers spin. | `wwwroot/js/rr-polish.js`, `Layout/MainLayout.razor` (`markLive`), `app.css` |
| 12 | **Pull to refresh on every page**, built on touch events so it works in the home-screen app: rubber-band resistance, a ring that fills as you pull, a 64px release threshold, the content held at 52px with a spinner while `LeagueService.RefreshAsync(full: true)` runs (shown at least 650ms), then a spring back. It is skipped when the pull starts on the tab bar, the top bar, a select, or with a sideways swipe in a carousel. The header refresh button is still there. | `wwwroot/js/rr-polish.js`, `Layout/MainLayout.razor` (`[JSInvokable] PullRefresh`), `app.css` (`.ptr`) |
| 13 | **Reduced motion**: with `prefers-reduced-motion`, transitions, easing and shimmer are off; pull to refresh still works, without animation. | `app.css`, `wwwroot/js/rr-polish.js` |
| 14 | **No false "Something went wrong" bar**: Blazor WebAssembly shows that bar whenever the app writes to `Console.Error`. `LeagueService` logged every handled network failure there, so a dropped refresh showed a scary bar under the friendly message. Those lines now use `Console.WriteLine`. (Four one-line changes; easy to merge.) | `Services/LeagueService.cs` |
| 15 | Dev setup: `.claude/launch.json` runs this copy on **5095** and, for testing the Release build, serves the publish output on **5096**. | `.claude/launch.json` |

Not touched: `index.html` (boot screen), `Program.cs`, `.csproj`, service workers, `Directory.Build.props`.
The JS module is loaded from `MainLayout` with `import(Nav.BaseUri + "js/rr-polish.js")`, so `index.html`
needed no change and the GitHub Pages subfolder base path still works.

## Design system for the Trades and Buzz copies

Build new screens from these classes so they match:

- Page section title: `<h2 class="sec"><Icon Name="..." /> Title <small>right-side note</small></h2>`
- Card: `.card`; list card: `.card list` with row children; card header: `.card-h` (title on the left, tag on the right)
- Tags: `.tag` plus `gold` / `good` / `bad` / `hot`; position chip: `<PosChip>`; injury: `<InjuryTag>`
- Buttons: `.btn` (secondary), `.btn primary` (green); filter chips: `.chips` > `button.chip` (`.on`); segmented control: `.seg` > `button` (`.on`)
- Player row: `<PlayerRow>` (put a badge in its `ChildContent`, which sits top-right like `.upgrade`)
- Empty or error: `<EmptyState Icon="..." Title="..." Text="..." ActionText="..." OnAction/Href />`
- Loading: `<Skeleton Kind="list|rows|chart|matchup" />`
- Icons: `<Icon Name="..." />`. Names: matchup, team, league, pickups, nfl, refresh, chevron-left/right/down, coach, alert, out, trending, football, offline, calendar, search, users, swap (good for Trades), check. Add a case in `Icon.razor` for new ones, in the same stroke style.
- New tab: add a `NavLink class="tab"` with an `<Icon>` in `MainLayout`. The tab bar fits 6 tabs at 375px. Link paths starting `player/`, `team/` or `matchup/` push in; anything else crossfades. Add a detail route to `DETAIL` in `rr-polish.js` if it should push.
- Numbers that should ease on refresh: add `data-ease` to the element whose only text is the number.
- Spacing and type: use the tokens (`--sp-*`, `--fs-*`, `--fd-*`), not raw px.

## Needs a real iPhone to confirm

- **Pull to refresh** in the home-screen app: I tested it with synthetic touch events in a desktop browser (threshold, hold, refresh call, spring back), including in the Release build, but not on a phone.
- **View Transitions** need iOS 18 or later. On iOS 17 the CSS fallback runs instead. The look of both on Safari hasn't been checked.
- **Pressed states** and blur performance on an older iPhone.
- **Safe areas** (notch and home indicator) in standalone mode: the CSS uses `env(safe-area-inset-*)` as before.

## Left alone on purpose

- The colours, logo, fonts and screen layouts stay the same; this is refinement, not a redesign.
- No touchdown flashes, confetti, sounds, or haptics (iOS web has no vibration API).
- Light mode: the app is dark-only, as before.
- `index.html`'s boot screen, `Program.cs`, the `.csproj` and the service workers belong to the speed session.
