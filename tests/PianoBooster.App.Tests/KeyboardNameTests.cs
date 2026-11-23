using Xunit;

namespace PianoBooster.App.Tests;

/// <summary>Each MIDI keyboard keeps its range by its name, without the index of the port list.</summary>
public class KeyboardNameTests
{
    [Theory]
    [InlineData("0 - Piano USB", "Piano USB")]
    [InlineData("12 - Digital Piano 1", "Digital Piano 1")]
    [InlineData("Piano USB", "Piano USB")]
    [InlineData("1 - MIDI - Port 2", "MIDI - Port 2")]
    public void TheIndexOfThePortIsNotPartOfTheName(string port, string name) =>
        Assert.Equal(name, Settings.KeyboardName(port));
}
