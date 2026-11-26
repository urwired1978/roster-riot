using Microsoft.AspNetCore.Components;
using RosterRiot.Services;

namespace RosterRiot.Components;

/// <summary>Base for pages: redraws whenever the league data refreshes.</summary>
public abstract class LivePage : ComponentBase, IDisposable
{
    [Inject] protected LeagueService S { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected BuzzService Buzz { get; set; } = default!;

    protected override void OnInitialized()
    {
        S.Changed += OnChanged;
        Buzz.Changed += OnChanged;
    }
    void OnChanged() => InvokeAsync(StateHasChanged);
    public virtual void Dispose()
    {
        S.Changed -= OnChanged;
        Buzz.Changed -= OnChanged;
    }

    protected static string F1(double v) => v.ToString("0.0");
    protected static string F2(double v) => v.ToString("0.00");
}
