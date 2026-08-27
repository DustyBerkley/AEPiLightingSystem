using System;
using System.Threading;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Runs <paramref name="tick"/> repeatedly on its own background thread,
/// paced to roughly <paramref name="intervalMs"/> by measuring how long each
/// tick actually took and sleeping only the remainder - self-correcting
/// rather than accumulating drift.
///
/// Exists so screen-capture work (BitBlt, pixel sampling, bitmap creation)
/// never runs synchronously inside a DispatcherTimer.Tick on the UI thread:
/// that GDI work is heavy enough, especially at 60Hz, to stall WPF's own
/// render pump and make every visible capture on screen look jumpy - not
/// just the one whose timer is "slow", since they all share the same UI
/// thread. <paramref name="tick"/> should do its capture/computation here
/// and marshal only the resulting UI-element updates back via
/// Dispatcher.BeginInvoke itself.
/// </summary>
public sealed class CaptureLoop
{
    private readonly Thread _thread;
    private volatile bool _running;

    public CaptureLoop(string name, int intervalMs, Action tick)
    {
        _running = true;
        _thread = new Thread(() =>
        {
            while (_running)
            {
                var frameStart = DateTime.UtcNow;
                tick();

                var elapsedMs = (int)(DateTime.UtcNow - frameStart).TotalMilliseconds;
                var remainingMs = intervalMs - elapsedMs;
                if (remainingMs > 0)
                    Thread.Sleep(remainingMs);
            }
        })
        { IsBackground = true, Name = name };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread.Join(200);
    }
}
