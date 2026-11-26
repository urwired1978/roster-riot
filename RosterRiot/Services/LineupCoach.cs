using RosterRiot.Models;

namespace RosterRiot.Services;

/// <summary>
/// Works out the best lineup from this week's projections and says what to change.
/// Players whose game has already kicked off are locked, just like in Sleeper.
/// </summary>
public static class LineupCoach
{
    public static LineupAdvice Advise(LeagueService s, Roster? r)
    {
        var slots = s.StarterSlots;
        var advice = new LineupAdvice();
        if (r is null || slots.Count == 0) return advice;

        var current = Enumerable.Range(0, slots.Count).Select(i => r.Starters is { } st && i < st.Count && st[i] != "0" ? st[i] : null).ToArray();
        var optimal = new string?[slots.Count];
        var used = new HashSet<string>();

        // Locked starters stay where they are.
        for (var i = 0; i < slots.Count; i++)
            if (current[i] is { } pid && s.Locked(pid)) { optimal[i] = pid; used.Add(pid); }

        var pool = (r.Players ?? []).Where(p => !used.Contains(p) && !s.Locked(p) && !(r.Reserve?.Contains(p) ?? false)).ToList();

        // Fill one-position slots first, then flex slots with the best of what's left.
        // Because a flex slot accepts everything the RB/WR/TE slots do, this order gives the best total.
        foreach (var flexPass in new[] { false, true })
            for (var i = 0; i < slots.Count; i++)
            {
                if (optimal[i] is not null || IsFlex(slots[i]) != flexPass) continue;
                var best = pool.Where(p => !used.Contains(p) && s.Fits(slots[i], p)).OrderByDescending(s.ProjOf).FirstOrDefault();
                if (best is null) continue;
                optimal[i] = best;
                used.Add(best);
            }

        advice.CurrentProj = current.Sum(p => s.ProjOf(p));
        advice.OptimalProj = optimal.Sum(p => s.ProjOf(p));

        var ins = optimal.Where(p => p is not null && !current.Contains(p)).OrderByDescending(s.ProjOf).ToList();
        var outs = current.Where(p => p is not null && !optimal.Contains(p)).OrderBy(s.ProjOf).ToList();
        for (var i = 0; i < ins.Count; i++)
        {
            var o = i < outs.Count ? outs[i] : null;
            var gain = s.ProjOf(ins[i]) - s.ProjOf(o);
            if (gain > 0.05) advice.Swaps.Add(new Swap(ins[i]!, o, gain));
        }

        for (var i = 0; i < slots.Count; i++)
        {
            var pid = current[i];
            var slot = LeagueService.SlotLabel(slots[i]);
            if (pid is null) { advice.Alerts.Add(new Alert(AlertLevel.Bad, null, $"Your {slot} spot is empty.")); continue; }
            if (s.Locked(pid)) continue;
            if (s.OnBye(pid)) advice.Alerts.Add(new Alert(AlertLevel.Bad, pid, $"{s.Name(pid)} is on bye this week."));
            else if (s.IsOut(pid)) advice.Alerts.Add(new Alert(AlertLevel.Bad, pid, $"{s.Name(pid)} is listed {s.Injury(pid)}."));
            else if (s.Injury(pid) is "Doubtful") advice.Alerts.Add(new Alert(AlertLevel.Bad, pid, $"{s.Name(pid)} is Doubtful. Have a backup ready."));
            else if (s.Injury(pid) is "Questionable") advice.Alerts.Add(new Alert(AlertLevel.Warn, pid, $"{s.Name(pid)} is Questionable. Check news before kickoff."));
        }
        return advice;
    }

    static bool IsFlex(string slot) => slot.Contains("FLEX");
}

public sealed class LineupAdvice
{
    public List<Swap> Swaps { get; } = [];
    public List<Alert> Alerts { get; } = [];
    public double CurrentProj { get; set; }
    public double OptimalProj { get; set; }
    public double Gain => Math.Max(0, OptimalProj - CurrentProj);
    public bool IsOptimal => Swaps.Count == 0;
}

public sealed record Swap(string In, string? Out, double Gain);
public enum AlertLevel { Warn, Bad }
public sealed record Alert(AlertLevel Level, string? Pid, string Text);
