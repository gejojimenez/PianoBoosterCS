// Sets up a song and its score like the application (see RefDriver of the core tests) and
// draws its frames into a bitmap.

using System;
using System.Collections.Generic;
using System.IO;
using PianoBooster.Core;
using SkiaSharp;
using Xunit;

namespace PianoBooster.Rendering.Tests;

/// <summary>A MIDI device that plays nothing and gives the notes of the pianist.</summary>
internal sealed class FakeMidiDevice : IMidiDevice
{
    public readonly Queue<MidiEvent> Input = new();

    public void PlayMidiEvent(in MidiEvent midiEvent) { }
    public int CheckMidiInput() => Input.Count;
    public MidiEvent ReadMidiInput() => Input.Dequeue();
    public bool ValidMidiOutput() => true;
    public bool OpenMidiPort(MidiType type, string portName) => true;
}

internal sealed class ScoreHarness : IDisposable
{
    public readonly MemorySettings Settings;
    public readonly Song Song;
    public readonly Score Score;
    public readonly FakeMidiDevice Device = new();
    public readonly ViewRenderer Renderer = new();
    public readonly int Width;
    public readonly int Height;

    public static string MidiPath(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "midi", name);

    public static string OutputDir
    {
        get
        {
            string dir = Path.Combine(Path.GetTempPath(), "PianoBooster.Rendering.Tests");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public ScoreHarness(string midiName, PlayMode mode = PlayMode.Listen, int width = 1024, int height = 700,
                        Action<MemorySettings>? configure = null)
    {
        Width = width;
        Height = height;
        CoreStatics.Reset();
        Cfg.SetDefaults();
        Cfg.LogLevel = 0;

        Settings = new MemorySettings();
        configure?.Invoke(Settings);

        // CGLView's constructor and QtWindow's constructor
        Song = new Song { MidiDevice = Device };
        Score = new Score(Settings);
        Song.Init2(Score, Settings);
        Song.TrackList.Init(Song, Settings);
        // CGLView::initializeGL()
        Cfg.SetStaveEndX(400);
        Song.SetActiveHand(WhichPart.Both);
        Score.Init();
        Song.RegenerateChordQueue();
        // CGLView::resizeGL()
        Renderer.Resize(width, height);

        // CSettings::setCurrentSongName()
        Song.LoadSong(MidiPath(midiName).Replace('\\', '/'));
        Assert.Equal(MidiError.NoError, Song.MidiError);
        Note.SetChannelHands(-2, -2);
        Song.TrackList.Refresh();
        Song.TrackList.CurrentRowChanged(Song.TrackList.GetActiveItemIndex());

        Song.SetPlayMode(mode);
        Song.PlayFromStartBar();
    }

    /// <summary>Moves the song on by some ms, in steps of 4 ms, and takes a frame every 16 ms
    /// (as the drawing would). Returns the last frame.</summary>
    public FrameSnapshot Run(int ms, Action<int>? eachStep = null)
    {
        FrameSnapshot? frame = null;
        for (int t = 4; t <= ms; t += 4)
        {
            Song.Task(4);
            eachStep?.Invoke(t);
            if (t % 16 == 0)
                frame = Prepare(frame);
        }
        return frame ?? Prepare();
    }

    public FrameSnapshot Prepare(FrameSnapshot? reuse = null) =>
        Renderer.PrepareFrame(Song, Score, Settings, updateMidiTask: null, reuse);

    public SKBitmap Render(FrameSnapshot frame, float scale = 1.0f)
    {
        var bitmap = new SKBitmap((int)(Width * scale), (int)(Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(scale);
        Renderer.Render(canvas, frame, scale);
        canvas.Flush();
        return bitmap;
    }

    public static string Save(SKBitmap bitmap, string name)
    {
        string path = Path.Combine(OutputDir, name + ".png");
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    /// <summary>The skia y of a y of the C++ code (from the bottom of the viewport).</summary>
    public float ScreenY(float glY) => Renderer.ScoreViewport.Bottom - glY;

    public void Dispose() => Renderer.Dispose();
}
