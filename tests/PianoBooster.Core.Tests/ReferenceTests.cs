using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// The core keeps its state in statics, like the C++ code: the tests must not run at the same time
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PianoBooster.Core.Tests;

/// <summary>
/// Runs each scenario of TestData/scenarios.txt with the C# port and checks that it writes
/// exactly what the original C++ code wrote (TestData/reference, see tools/RefDump).
/// </summary>
public class ReferenceTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "TestData");

    public static IEnumerable<object[]> Scenarios()
    {
        foreach (string raw in File.ReadAllLines(Path.Combine(DataDir, "scenarios.txt")))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            yield return new object[] { line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0] };
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void SameAsTheCppCode(string name) => Check(name, null);

    public static IEnumerable<object[]> PlayScenarios() =>
        Scenarios().Where(s => ModeOf((string)s[0]) == "play");

    /// <summary>The performance observer only observes: with it the conductor does the same.</summary>
    [Theory]
    [MemberData(nameof(PlayScenarios))]
    public void SameWithAPerformanceObserver(string name)
    {
        var recorder = new EventRecorder();
        Check(name, recorder);
        Assert.NotEmpty(recorder.Events);
        Assert.IsType<PlayingStarted>(recorder.Events[0]);
    }

    private static string ModeOf(string name) => ScenarioFields(name)[1];

    private static string[] ScenarioFields(string name) =>
        File.ReadAllLines(Path.Combine(DataDir, "scenarios.txt"))
            .Select(l => l.Trim())
            .First(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == name)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static void Check(string name, IPerformanceObserver? observer)
    {
        string[] fields = ScenarioFields(name);
        string mode = fields[1];
        string midi = Path.Combine(DataDir, "midi", fields[2]).Replace('\\', '/');
        var parameters = new Dictionary<string, string>();
        foreach (string field in fields.Skip(3))
        {
            int eq = field.IndexOf('=');
            parameters[field[..eq]] = field[(eq + 1)..];
        }

        string referenceFile = Path.Combine(DataDir, "reference", name + ".txt");
        Assert.True(File.Exists(referenceFile), $"No reference for {name}: run tools/RefDump/generate-references.ps1");
        string[] expected = File.ReadAllText(referenceFile).Split('\n');
        string actualText = RefDriver.Run(mode, midi, parameters, observer);
        string[] actual = actualText.Split('\n');

        int count = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < count; i++)
        {
            if (expected[i] != actual[i])
                Fail(name, actualText, $"line {i + 1}:\n  C++: {expected[i]}\n  C#:  {actual[i]}");
        }
        if (expected.Length != actual.Length)
            Fail(name, actualText, $"C++ wrote {expected.Length} lines, C# {actual.Length}");
    }

    private static void Fail(string name, string actualText, string message)
    {
        string dir = Path.Combine(Path.GetTempPath(), "PianoBooster.Core.Tests");
        Directory.CreateDirectory(dir);
        string actualFile = Path.Combine(dir, name + ".actual.txt");
        File.WriteAllText(actualFile, actualText);
        Assert.Fail($"{name} differs from the C++ code at {message}\n(the C# output is in {actualFile})");
    }
}
