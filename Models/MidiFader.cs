using System.ComponentModel;

namespace AEPiLightingSystem.Models;

/// <summary>
/// One fader on the (not yet purchased) 8-fader MIDI board. Placeholder for
/// now: nothing feeds Value from real hardware yet, so the GUI just shows
/// whatever it's set to.
/// </summary>
public sealed class MidiFader : INotifyPropertyChanged
{
    private double _value;

    /// <summary>Which page/bank of 8 this fader belongs to - only its own page's boxes should show its icon/effect.</summary>
    public FaderPage Page { get; }

    public int Index { get; }

    /// <summary>Page formatted the way its on-screen page-select button reads - "Pars" for Bass, "Light Bars" for LightBar, etc.</summary>
    public string PageDisplayName => Page switch
    {
        FaderPage.Bass => "Pars",
        FaderPage.Laser => "Laser",
        FaderPage.LightBar => "Light Bars",
        FaderPage.Combo => "Combo",
        _ => "Global",
    };

    /// <summary>0 (all the way down) to 1 (all the way up).</summary>
    public double Value
    {
        get => _value;
        set
        {
            // While invalidated, the real fader is still sitting wherever it
            // was when Invalidate() forced this to 0 - it must be brought
            // all the way back down before this fader can react again, not
            // just nudged. So any write other than an exact 0 is ignored
            // outright here, rather than being allowed through to update
            // _value, which would silently un-pin the displayed value (and
            // let effects fire again) despite IsInvalidated still being set.
            if (IsInvalidated)
            {
                if (value != 0)
                    return;

                IsInvalidated = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInvalidated)));
                return;
            }

            if (_value == value)
                return;

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingValue)));
        }
    }

    /// <summary>1 - Value - the "unfilled" complement, for star-sizing a 2-column progress bar that always fills the full width regardless of the box's actual pixel size.</summary>
    public double RemainingValue => 1 - Value;

    /// <summary>
    /// True after this fader got force-zeroed by <see cref="Invalidate"/>
    /// because it conflicted with another fader effect that just activated -
    /// stays true (effect stays off) until the real fader is physically
    /// moved back to 0%, i.e. until <see cref="Value"/> is set to 0 again
    /// through the normal setter, not through Invalidate() itself.
    /// </summary>
    public bool IsInvalidated { get; private set; }

    /// <summary>Force this fader to 0% and flag it invalidated - called when another fader effect it conflicts with just activated.</summary>
    public void Invalidate()
    {
        _value = 0;
        IsInvalidated = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInvalidated)));
    }

    private double _beatInterval = 1.0;

    /// <summary>
    /// How many beats apart this fader's effect fires - 1 means every beat,
    /// 2 means every other beat (beats 1 and 3 of a 4-beat measure), 4 means
    /// once per measure (beat 1 only), 0.5 means twice per beat. Fractional
    /// values are allowed for exactly that sub-beat case.
    /// </summary>
    public double BeatInterval
    {
        get => _beatInterval;
        set
        {
            if (_beatInterval == value)
                return;
            _beatInterval = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BeatInterval)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BeatIntervalLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
        }
    }

    /// <summary>BeatInterval formatted the way a DJ would say it out loud - "1/2" rather than "0.5".</summary>
    public string BeatIntervalLabel => BeatInterval switch
    {
        0.5 => "1/2",
        0.25 => "1/4",
        0.125 => "1/8",
        0.0625 => "1/16",
        _ when BeatInterval == System.Math.Floor(BeatInterval) => ((int)BeatInterval).ToString(),
        _ => BeatInterval.ToString("0.##"),
    };

    private string? _customDisplayLabel;

    /// <summary>
    /// Overrides <see cref="BeatIntervalLabel"/> in the top-right corner of
    /// this fader's box - for an effect whose knob controls something other
    /// than a beat fraction (e.g. the combo wall color cycle's knob dialing
    /// a duration in seconds, see MainWindow.UpdateComboWallColorCycleFaderEffect),
    /// where showing the unused BeatInterval would just be misleading. Null
    /// (the default) falls back to BeatIntervalLabel as normal.
    /// </summary>
    public string? CustomDisplayLabel
    {
        get => _customDisplayLabel;
        set
        {
            if (_customDisplayLabel == value)
                return;
            _customDisplayLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomDisplayLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
        }
    }

    /// <summary>What the fader box's top-right label actually shows - CustomDisplayLabel if set, else BeatIntervalLabel.</summary>
    public string DisplayLabel => CustomDisplayLabel ?? BeatIntervalLabel;

    public MidiFader(FaderPage page, int index, double value = 0.0)
    {
        Page = page;
        Index = index;
        _value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
