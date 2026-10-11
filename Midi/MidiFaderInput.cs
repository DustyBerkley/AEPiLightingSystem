using System;
using System.Threading;
using NAudio.Midi;

namespace AEPiLightingSystem.Midi;

/// <summary>
/// Reads the physical 8-fader MIDI board (Vangoa "VMX8") over the Windows
/// MIDI input API via NAudio. Each fader broadcasts on its own dedicated
/// MIDI channel (1-8, matching its left-to-right physical position) as a
/// 14-bit pitch bend message rather than a control change; each fader's
/// knob broadcasts as a relative control change on channel 1 - Controller
/// 16 for knob 1 up through Controller 23 for knob 8, value 65 meaning one
/// click right and value 1 meaning one click left; and each fader's column
/// of 4 buttons has its top and bottom button broadcast a note-on, also on
/// channel 1 - notes 16-23 (fader 1-8) for the top button and notes 8-15
/// (fader 1-8) for the bottom button. The 3rd button also sends a note-on
/// press, on the very same notes (0-7) used to light its own LED (see
/// below) - the bottommost button, however, really doesn't send anything on
/// press, only accepting a lit/unlit LED command. Both LEDs are lit by
/// sending a note-on back to the board's own "VMX8" MIDI-out port - notes
/// 0-7 (fader 1-8) light the 3rd button in a fixed red, and notes 24-31
/// (fader 1-8) light the bottommost button in a fixed white; neither
/// channel nor velocity changes the color, so these two are used purely as
/// on/off status lights, not RGB indicators. None of this is
/// documented; it was all found by capturing raw input from the board (for
/// the faders/knobs/buttons) and by probing note-on messages back to it
/// (for the two status LEDs). The board exposes two identical MIDI-in
/// ports and two identical MIDI-out ports; only the first of each
/// ("VMX8", not "MIDIIN2 (VMX8)"/"MIDIOUT2 (VMX8)") is opened, since both
/// mirror the exact same events and opening both would just double-fire
/// everything.
/// </summary>
public sealed class MidiFaderInput : IDisposable
{
    private const string DeviceName = "VMX8";
    private const int KnobChannel = 1;
    private const int FirstKnobController = 16;
    private const int KnobTurnedRightValue = 65;
    private const int ButtonChannel = 1;
    private const int FirstTopButtonNote = 16;
    private const int FirstBottomButtonNote = 8;
    private const int LedChannel = 1;
    private const int LedOnVelocity = 127;
    private const int FirstInvalidatedLedNote = 0;
    private const int FirstActiveLedNote = 24;

    private readonly MidiIn? _midiIn;
    private readonly MidiOut? _midiOut;

    /// <summary>Fires with (1-based fader index, 0-1 value) whenever a fader moves.</summary>
    public event Action<int, double>? FaderChanged;

    /// <summary>Fires with (1-based fader index, +1 for a click right or -1 for a click left) whenever a knob turns.</summary>
    public event Action<int, int>? KnobTurned;

    /// <summary>Fires with (1-based fader index, +1 for the top button or -1 for the bottom button) whenever one of those buttons is pressed.</summary>
    public event Action<int, int>? BeatIntervalButtonPressed;

    /// <summary>Fires with (1-based fader index) whenever that fader's 3rd (invalidation-reset) button is pressed.</summary>
    public event Action<int>? InvalidationResetButtonPressed;

    public MidiFaderInput()
    {
        var inputDeviceIndex = FindDeviceIndex(MidiIn.NumberOfDevices, i => MidiIn.DeviceInfo(i).ProductName);
        if (inputDeviceIndex is not null)
        {
            _midiIn = new MidiIn(inputDeviceIndex.Value);
            _midiIn.MessageReceived += OnMessageReceived;
            _midiIn.Start();
        }

        var outputDeviceIndex = FindDeviceIndex(MidiOut.NumberOfDevices, i => MidiOut.DeviceInfo(i).ProductName);
        if (outputDeviceIndex is not null)
        {
            _midiOut = new MidiOut(outputDeviceIndex.Value);

            // The top 2 buttons (double/halve the beat interval) stay lit
            // permanently, unlike the bottom 2 status LEDs - they aren't
            // tied to any fader state, just always-on so they read as "these
            // are active controls". A small pacing delay between each of the
            // 16 sends avoids flooding the board right after opening the
            // port - see LaunchpadInput's identical fix for why (an actual
            // crash from sending too many back-to-back).
            for (var note = FirstBottomButtonNote; note < FirstTopButtonNote + 8; note++)
            {
                SetLed(note, true);
                Thread.Sleep(2);
            }
        }
    }

    private static int? FindDeviceIndex(int deviceCount, Func<int, string> getProductName)
    {
        for (var i = 0; i < deviceCount; i++)
        {
            if (getProductName(i).Equals(DeviceName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    /// <summary>Turns the "active" status LED (the bottommost button) on or off for the given fader.</summary>
    public void SetActiveIndicator(int faderIndex, bool on) => SetLed(FirstActiveLedNote + faderIndex - 1, on);

    /// <summary>Turns the "invalidated" status LED (the 3rd button) on or off for the given fader.</summary>
    public void SetInvalidatedIndicator(int faderIndex, bool on) => SetLed(FirstInvalidatedLedNote + faderIndex - 1, on);

    // A real Note Off message doesn't turn these LEDs off - confirmed while
    // clearing a stuck test light, where a NoteOn with velocity 0 worked but
    // a genuine Note Off didn't - so "off" is sent as NoteOn/velocity 0 too.
    private void SetLed(int note, bool on)
    {
        try
        {
            _midiOut?.Send(MidiMessage.StartNote(note, on ? LedOnVelocity : 0, LedChannel).RawData);
        }
        catch (NAudio.MmException)
        {
            // A short MIDI-out message can transiently fail right after
            // opening the port (confirmed via an actual crash on the
            // Launchpad's identical LED-init loop) - lighting an LED is
            // best-effort, never worth taking the whole app down over.
        }
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        switch (e.MidiEvent)
        {
            case PitchWheelChangeEvent pitchEvent:
                OnFaderMoved(pitchEvent);
                break;
            case ControlChangeEvent controlChangeEvent:
                OnKnobTurned(controlChangeEvent);
                break;
            case NoteOnEvent noteOnEvent:
                OnButtonPressed(noteOnEvent);
                break;
        }
    }

    private void OnFaderMoved(PitchWheelChangeEvent pitchEvent)
    {
        var index = pitchEvent.Channel;
        if (index is < 1 or > 8)
            return;

        var value = Math.Clamp(pitchEvent.Pitch / 16383.0, 0.0, 1.0);
        FaderChanged?.Invoke(index, value);
    }

    private void OnKnobTurned(ControlChangeEvent controlChangeEvent)
    {
        if (controlChangeEvent.Channel != KnobChannel)
            return;

        var index = (int)controlChangeEvent.Controller - FirstKnobController + 1;
        if (index is < 1 or > 8)
            return;

        var direction = (int)controlChangeEvent.ControllerValue >= KnobTurnedRightValue ? 1 : -1;
        KnobTurned?.Invoke(index, direction);
    }

    private void OnButtonPressed(NoteOnEvent noteOnEvent)
    {
        // A release also arrives as a NoteOnEvent with velocity 0 (standard
        // MIDI running-status convention) - only the actual press should
        // trigger anything.
        if (noteOnEvent.Channel != ButtonChannel || noteOnEvent.Velocity == 0)
            return;

        var note = noteOnEvent.NoteNumber;
        if (note is >= FirstTopButtonNote and < FirstTopButtonNote + 8)
            BeatIntervalButtonPressed?.Invoke(note - FirstTopButtonNote + 1, 1);
        else if (note is >= FirstBottomButtonNote and < FirstBottomButtonNote + 8)
            BeatIntervalButtonPressed?.Invoke(note - FirstBottomButtonNote + 1, -1);
        else if (note is >= FirstInvalidatedLedNote and < FirstInvalidatedLedNote + 8)
            InvalidationResetButtonPressed?.Invoke(note - FirstInvalidatedLedNote + 1);
    }

    public void Dispose()
    {
        if (_midiIn is not null)
        {
            _midiIn.Stop();
            _midiIn.MessageReceived -= OnMessageReceived;
            _midiIn.Dispose();
        }

        _midiOut?.Dispose();
    }
}
