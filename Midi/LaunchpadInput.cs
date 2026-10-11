using System;
using System.Threading;
using NAudio.Midi;

namespace AEPiLightingSystem.Midi;

/// <summary>
/// Reads a Novation Launchpad MK2 over the Windows MIDI input API via
/// NAudio, and lights its pads/buttons over MIDI output. Layout is
/// Novation's own documented "Session mode" default, not reverse-engineered:
/// - The 8x8 grid sends/receives Note On on channel 1, note = 10*(row+1) +
///   (column+1), row 0 = bottom, column 0 = left (so notes run 11-18 for
///   the bottom row up through 81-88 for the top row).
/// - The top row of 8 round buttons sends/receives Control Change on
///   channel 1, controllers 104-111, left to right.
/// - The right-hand column of 8 round buttons sends/receives Note On on
///   channel 1, note = 10*(row+1) + 9, top to bottom.
/// A velocity/value of 127 means pressed, 0 means released. Lighting a pad
/// or button is just sending that same message back with velocity/value
/// set to a color index (0-127) from the Launchpad's fixed palette; 0 turns
/// it off.
/// </summary>
public sealed class LaunchpadInput : IDisposable
{
    private const string DeviceName = "Launchpad MK2";
    private const int Channel = 1;
    private const int FirstTopButtonController = 104;

    private readonly MidiIn? _midiIn;
    private readonly MidiOut? _midiOut;

    /// <summary>Fires with (row 0-7 bottom-to-top, column 0-7 left-to-right, pressed) whenever a grid pad is pressed or released.</summary>
    public event Action<int, int, bool>? GridPadChanged;

    /// <summary>Fires with (index 0-7 left-to-right, pressed) whenever one of the top row of round buttons is pressed or released.</summary>
    public event Action<int, bool>? TopButtonChanged;

    /// <summary>Fires with (index 0-7 top-to-bottom, pressed) whenever one of the right column of round buttons is pressed or released.</summary>
    public event Action<int, bool>? SideButtonChanged;

    public LaunchpadInput()
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

            // Start from a clean slate rather than whatever the grid was
            // left showing by a previous run - this happens before
            // MainWindow paints anything of its own onto specific pads, since
            // it's a field initializer and so runs before MainWindow's own
            // constructor body. A small pacing delay between each of the 64
            // sends avoids flooding the board right after opening the port -
            // sending all of them back-to-back with no gap was observed to
            // make the very next midiOutShortMsg call fail outright
            // (NAudio.MmException), which is exactly what SetPadColor now
            // guards against anyway, but avoiding it in the first place means
            // the grid still actually ends up cleared rather than silently
            // skipped.
            for (var row = 0; row < 8; row++)
            {
                for (var column = 0; column < 8; column++)
                {
                    SetPadColor(row, column, 0);
                    Thread.Sleep(2);
                }
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

    /// <summary>Lights (or turns off, with colorIndex 0) a grid pad using the Launchpad's fixed 0-127 color palette.</summary>
    public void SetPadColor(int row, int column, int colorIndex) =>
        TrySend(MidiMessage.StartNote(GridNote(row, column), colorIndex, Channel));

    /// <summary>Lights (or turns off, with colorIndex 0) one of the top row of round buttons.</summary>
    public void SetTopButtonColor(int index, int colorIndex)
    {
        var status = 0xB0 | (Channel - 1);
        TrySend(new MidiMessage(status, FirstTopButtonController + index, colorIndex));
    }

    /// <summary>Lights (or turns off, with colorIndex 0) one of the right column of round buttons.</summary>
    public void SetSideButtonColor(int index, int colorIndex) =>
        TrySend(MidiMessage.StartNote(SideButtonNote(index), colorIndex, Channel));

    private static int GridNote(int row, int column) => 10 * (row + 1) + (column + 1);
    private static int SideButtonNote(int index) => 10 * (index + 1) + 9;

    // A short MIDI-out message can transiently fail right after opening the
    // port (confirmed via an actual crash: sending 64 back-to-back at
    // startup threw NAudio.MmException on one of them) - lighting a pad is
    // best-effort, never worth taking the whole app down over.
    private void TrySend(MidiMessage message)
    {
        try
        {
            _midiOut?.Send(message.RawData);
        }
        catch (NAudio.MmException)
        {
        }
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        switch (e.MidiEvent)
        {
            case NoteOnEvent noteOnEvent when noteOnEvent.Channel == Channel:
                OnNoteEvent(noteOnEvent.NoteNumber, noteOnEvent.Velocity > 0);
                break;
            case ControlChangeEvent controlChangeEvent when controlChangeEvent.Channel == Channel:
                OnTopButtonEvent((int)controlChangeEvent.Controller, controlChangeEvent.ControllerValue > 0);
                break;
        }
    }

    private void OnNoteEvent(int note, bool pressed)
    {
        var row = note / 10 - 1;
        var column = note % 10 - 1;
        if (row is < 0 or > 7)
            return;

        if (column is >= 0 and <= 7)
            GridPadChanged?.Invoke(row, column, pressed);
        else if (column == 8)
            SideButtonChanged?.Invoke(row, pressed);
    }

    private void OnTopButtonEvent(int controller, bool pressed)
    {
        var index = controller - FirstTopButtonController;
        if (index is >= 0 and <= 7)
            TopButtonChanged?.Invoke(index, pressed);
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
