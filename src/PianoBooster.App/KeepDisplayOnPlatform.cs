// Ported from KeepDisplayOn.cpp of PianoBooster (C++). GPLv3.
//
// Tells the operating system that the display is in use while a song is playing (Windows only,
// as in the C++). The conductor calls KeepDisplayOn from its own thread, but
// SetThreadExecutionState(ES_CONTINUOUS) belongs to the thread that calls it, so the calls are
// made in the thread of the interface.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using PianoBooster.Core;

namespace PianoBooster.App;

internal static partial class KeepDisplayOnPlatform
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsContinuous = 0x80000000;

    // The idle timer only needs resetting now and then, not on every note.
    private const long UserActivityIntervalMs = 10000;

    private static bool s_required;
    private static readonly Stopwatch s_lastActivity = new();

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint esFlags);

    /// <summary>Gives the actions of KeepDisplayOn (Core) their implementation.</summary>
    public static void Install()
    {
        if (!OperatingSystem.IsWindows())
            return;
        KeepDisplayOn.SetRequiredAction = required => Dispatcher.UIThread.Post(() => SetRequired(required));
        KeepDisplayOn.UserActivityAction = () =>
        {
            lock (s_lastActivity)
            {
                if (s_lastActivity.IsRunning && s_lastActivity.ElapsedMilliseconds < UserActivityIntervalMs)
                    return;
                s_lastActivity.Restart();
            }
            // Without ES_CONTINUOUS this just resets the display and system idle timers.
            Dispatcher.UIThread.Post(() => SetThreadExecutionState(EsDisplayRequired | EsSystemRequired));
        };
    }

    private static void SetRequired(bool required)
    {
        if (required == s_required)
            return;
        s_required = required;
        // ES_CONTINUOUS keeps the state until it is changed again; calling it
        // with only ES_CONTINUOUS clears the request.
        if (required)
            SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);
        else
            SetThreadExecutionState(EsContinuous);
    }
}
