namespace AEPiLightingSystem.Dmx;

/// <summary>
/// The current value of every DMX512 channel (1-512), plus the leading
/// start code a receiver expects before channel data. Plain byte writes are
/// atomic in .NET, so this is safely written from the UI thread (as par
/// light colors change) and read from the DMX sender thread without a lock -
/// worst case a frame goes out with one channel a tick stale, which is
/// invisible at DMX refresh rates.
/// </summary>
public sealed class DmxUniverse
{
    public const int ChannelCount = 512;

    // Index 0 is the DMX start code (0x00 = "standard dimmer packet"),
    // indices 1-512 are channels 1-512.
    private readonly byte[] _frame = new byte[ChannelCount + 1];

    // DMX doesn't require a frame to cover all 512 channels - a receiver
    // only cares about its own address range - so only as many bytes as the
    // highest channel actually in use get sent. A full 512-channel frame
    // takes ~22.6ms on the wire at 250000 baud; trimmed to the ~28 channels
    // 4 par lights actually use, that drops to ~1.3ms, which both raises the
    // achievable refresh rate and - more importantly - cuts how long each
    // frame is exposed to whatever's corrupting transmissions on this line.
    private int _highestChannelSet;

    /// <summary>Sets a single channel (1-512) to a value (0-255).</summary>
    public void SetChannel(int channel, byte value)
    {
        _frame[channel] = value;

        if (channel > _highestChannelSet)
            _highestChannelSet = channel;
    }

    /// <summary>The start-code-prefixed frame, ready to write to the serial port.</summary>
    public byte[] Frame => _frame;

    /// <summary>How many bytes of <see cref="Frame"/> (from index 0) are actually in use.</summary>
    public int FrameLength => _highestChannelSet + 1;
}
