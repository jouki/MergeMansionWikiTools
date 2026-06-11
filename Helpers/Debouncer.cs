using System;
using System.Windows.Threading;

namespace MergeMansionWikiTools.Helpers;

/// <summary>
/// Odloží akci o zadaný interval; opakované volání <see cref="Trigger"/> restartuje časovač
/// (debounce pro TextChanged apod.). Akce běží na dispatcher threadu.
/// </summary>
public sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private Action? _action;

    public Debouncer(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => { _timer.Stop(); _action?.Invoke(); };
    }

    /// <summary>Naplánuje (resp. přeplánuje) akci — vykoná se až po uplynutí intervalu od posledního Trigger.</summary>
    public void Trigger(Action action)
    {
        _action = action;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Zruší čekající akci (pokud nějaká je).</summary>
    public void Cancel() => _timer.Stop();
}
