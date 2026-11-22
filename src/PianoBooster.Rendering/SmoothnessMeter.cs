// Measures how evenly the score scrolls, like it was measured for PianoBooster 1.3.8 ("the
// score now keeps within 0.24 pixels of an even motion, it was 0.71"). Part of the .NET port
// of PianoBooster. GPLv3.
//
// Each frame gives the time it is shown and how far the score has scrolled. Over each window
// of time (one second by default) a straight line is fitted to the positions (an even motion at
// the speed of that window, so that tempo changes do not count), and the distance of each frame
// from the line is its deviation.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PianoBooster.Rendering;

/// <summary>The result of <see cref="SmoothnessMeter.Compute"/>.</summary>
public readonly record struct SmoothnessResult(
    int Frames,
    int Windows,
    double RmsDeviation,   // pixels
    double P99Deviation,   // pixels
    double MaxDeviation,   // pixels
    double MeanFrameMs,
    double P99FrameMs,
    double MaxFrameMs,
    double PixelsPerSecond)
{
    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "{0} frames ({1} windows): deviation from an even motion RMS {2:0.000} px, 99% {3:0.000} px, max {4:0.000} px; " +
        "frame time mean {5:0.00} ms, 99% {6:0.00} ms, max {7:0.00} ms; speed {8:0.0} px/s",
        Frames, Windows, RmsDeviation, P99Deviation, MaxDeviation, MeanFrameMs, P99FrameMs, MaxFrameMs, PixelsPerSecond);
}

/// <summary>Collects (time, position) of the frames and measures the deviation from an even motion.</summary>
public sealed class SmoothnessMeter
{
    private readonly List<(double Seconds, double Position)> _samples = new();

    /// <summary>The length of the windows in which the motion is taken as even.</summary>
    public double WindowSeconds { get; set; } = 1.0;

    public int Count => _samples.Count;

    public void Clear() => _samples.Clear();

    /// <summary>A frame: when it is shown (seconds) and how far the score has scrolled (pixels).</summary>
    public void Add(double seconds, double position) => _samples.Add((seconds, position));

    public SmoothnessResult Compute()
    {
        var deviations = new List<double>();
        var frameMs = new List<double>();
        double sumSquares = 0;
        int windows = 0;
        double speedSum = 0;

        int start = 0;
        while (start < _samples.Count)
        {
            int end = start;
            while (end < _samples.Count && _samples[end].Seconds - _samples[start].Seconds < WindowSeconds)
                end++;
            int n = end - start;
            if (n >= 10)
            {
                // the least squares line
                double meanT = 0, meanX = 0;
                for (int i = start; i < end; i++)
                {
                    meanT += _samples[i].Seconds;
                    meanX += _samples[i].Position;
                }
                meanT /= n;
                meanX /= n;
                double stt = 0, stx = 0;
                for (int i = start; i < end; i++)
                {
                    double dt = _samples[i].Seconds - meanT;
                    stt += dt * dt;
                    stx += dt * (_samples[i].Position - meanX);
                }
                double slope = stt > 0 ? stx / stt : 0;
                // windows where the score does not move (stopped, waiting for the pianist) do not count
                if (Math.Abs(slope) > 1e-6)
                {
                    windows++;
                    speedSum += slope;
                    for (int i = start; i < end; i++)
                    {
                        double expected = meanX + slope * (_samples[i].Seconds - meanT);
                        double deviation = Math.Abs(_samples[i].Position - expected);
                        deviations.Add(deviation);
                        sumSquares += deviation * deviation;
                        if (i > start)
                            frameMs.Add((_samples[i].Seconds - _samples[i - 1].Seconds) * 1000.0);
                    }
                }
            }
            start = end > start ? end : start + 1;
        }

        if (deviations.Count == 0)
            return new SmoothnessResult(0, 0, 0, 0, 0, 0, 0, 0, 0);

        deviations.Sort();
        frameMs.Sort();
        double meanFrame = 0;
        foreach (double ms in frameMs)
            meanFrame += ms;
        meanFrame = frameMs.Count > 0 ? meanFrame / frameMs.Count : 0;

        return new SmoothnessResult(
            deviations.Count,
            windows,
            Math.Sqrt(sumSquares / deviations.Count),
            Percentile(deviations, 0.99),
            deviations[^1],
            meanFrame,
            Percentile(frameMs, 0.99),
            frameMs.Count > 0 ? frameMs[^1] : 0,
            speedSum / windows);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0;
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
