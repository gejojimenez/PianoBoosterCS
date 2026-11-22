// The timing of CGLView::updateMidiTask() and CGLView::timerEvent() of PianoBooster (C++),
// Copyright (c) 2008-2013, L. J. Barman. GPLv3. In C++ a QTimer of the interface thread calls
// it; here it runs in its own thread.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PianoBooster.Core;

namespace PianoBooster.Midi;

/// <summary>Runs <see cref="Song.Task"/> every <see cref="Cfg.TickRate"/> ms in a thread of high
/// priority. The song is not thread safe: everything else that uses it (the interface, the
/// drawing, opening MIDI ports) must do it with <see cref="Invoke"/> or holding <see cref="SyncRoot"/>.</summary>
public sealed partial class ConductorThread : IDisposable
{
    private const long NsecsPerMsec = 1_000_000;

    private readonly Func<long, uint> _task;
    private readonly object _syncRoot = new();
    private Thread? _thread;
    private volatile bool _stopRequested;
    private long _eventBits; // uint, kept in a long for Interlocked
    private int _periodMs;

    private readonly Stopwatch _realtime = new();
    private long _realtimeNsecs;

    public ConductorThread(Song song) : this(song.Task)
    {
    }

    /// <summary>Calls any function with the milliseconds passed (for the tests).</summary>
    public ConductorThread(Func<long, uint> task)
    {
        _task = task;
    }

    /// <summary>The lock that protects the song.</summary>
    public object SyncRoot => _syncRoot;

    /// <summary>Called in the thread of the conductor, without the lock, when <see cref="Song.Task"/>
    /// returns event bits (for example to wake up the interface).</summary>
    public event Action<uint>? EventBitsRaised;

    public bool IsRunning => _thread != null;

    /// <summary>Starts calling the song. The time counts from now (as CGLView::startTimer()).</summary>
    public void Start()
    {
        if (_thread != null)
            return;
        _periodMs = Cfg.TickRate > 0 ? Cfg.TickRate : 4;
        _stopRequested = false;
        _realtime.Restart();
        _realtimeNsecs = 0;
        _thread = new Thread(Run)
        {
            Name = "PianoBooster conductor",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread == null)
            return;
        _stopRequested = true;
        thread.Join();
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Runs an action with the lock of the song.</summary>
    public void Invoke(Action action)
    {
        lock (_syncRoot)
            action();
    }

    /// <summary>Runs a function with the lock of the song.</summary>
    public T Invoke<T>(Func<T> function)
    {
        lock (_syncRoot)
            return function();
    }

    /// <summary>The event bits gathered since the last call (as CGLView's m_eventBits, which the
    /// drawing reads and clears once per frame).</summary>
    public uint TakeEventBits() => (uint)Interlocked.Exchange(ref _eventBits, 0);

    /// <summary>Calls the song with the milliseconds passed, keeping the rest in nanoseconds
    /// (CGLView::updateMidiTask()). It can also be called from the drawing, with the lock.</summary>
    public void UpdateMidiTask()
    {
        uint bits;
        lock (_syncRoot)
        {
            long elapsedNsecs = (long)(_realtime.ElapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency));
            long ticks = (elapsedNsecs - _realtimeNsecs) / NsecsPerMsec;
            _realtimeNsecs += ticks * NsecsPerMsec;
            bits = _task(ticks);
        }
        if (bits != 0)
        {
            long current, updated;
            do
            {
                current = Interlocked.Read(ref _eventBits);
                updated = current | bits;
            } while (Interlocked.CompareExchange(ref _eventBits, updated, current) != current);
            EventBitsRaised?.Invoke(bits);
        }
    }

    private void Run()
    {
        using var timer = PeriodicWaiter.Create();
        long periodNsecs = _periodMs * NsecsPerMsec;
        double nsecsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        long next = periodNsecs;
        while (!_stopRequested)
        {
            UpdateMidiTask();

            long now = (long)(_realtime.ElapsedTicks * nsecsPerTick);
            if (next <= now)
                next = now + periodNsecs; // late (or the first time): wait a whole period
            timer.Wait(next - now);
            next += periodNsecs;
        }
    }

    /// <summary>Waits for a short time. On Windows a high resolution waitable timer (Windows 10
    /// 1803 and later) or, without it, Sleep with a timer resolution of 1 ms. Also used by
    /// <see cref="SectionPlayer"/>.</summary>
    internal abstract class PeriodicWaiter : IDisposable
    {
        public abstract void Wait(long nsecs);
        public virtual void Dispose() { }

        public static PeriodicWaiter Create()
        {
            if (OperatingSystem.IsWindows())
            {
                IntPtr handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (handle != IntPtr.Zero)
                    return new WindowsTimerWaiter(handle);
                return new SleepWaiter(windowsTimerPeriod: true);
            }
            return new SleepWaiter(windowsTimerPeriod: false);
        }
    }

    private sealed class WindowsTimerWaiter : PeriodicWaiter
    {
        private readonly IntPtr _handle;

        public WindowsTimerWaiter(IntPtr handle) => _handle = handle;

        public override void Wait(long nsecs)
        {
            if (nsecs <= 0)
                return;
            long dueTime = -(nsecs / 100); // relative, in units of 100 ns
            if (dueTime == 0)
                dueTime = -1;
            if (SetWaitableTimer(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_handle, Infinite);
            else
                Thread.Sleep(1);
        }

        public override void Dispose() => CloseHandle(_handle);
    }

    private sealed class SleepWaiter : PeriodicWaiter
    {
        private readonly bool _windowsTimerPeriod;

        public SleepWaiter(bool windowsTimerPeriod)
        {
            _windowsTimerPeriod = windowsTimerPeriod;
            if (_windowsTimerPeriod)
                timeBeginPeriod(1);
        }

        public override void Wait(long nsecs)
        {
            if (nsecs > 0)
                Thread.Sleep(TimeSpan.FromTicks(Math.Max(1, nsecs / 100)));
        }

        public override void Dispose()
        {
            if (_windowsTimerPeriod)
                timeEndPeriod(1);
        }
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine,
                                                 IntPtr argToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint period);
}
