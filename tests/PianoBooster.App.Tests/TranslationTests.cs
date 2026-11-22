using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PianoBooster.App.Tests;

/// <summary>
/// The translations (phase 6.1): tools/ts-to-resx.ps1 (the .ts files of the C++ code to .resx) and
/// Strings (tr() of Qt) on the .resx files generated from the C++ code. Strings.Culture is static, so
/// every test is in this class (the tests of a class do not run in parallel) and puts it back.
/// </summary>
public sealed class TranslationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PianoBooster.App.Tests", Guid.NewGuid().ToString("N"));

    public TranslationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Strings.Culture = CultureInfo.InvariantCulture;
        Directory.Delete(_folder, true);
    }

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PianoBooster.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("PianoBooster.sln not found");
    }

    private static string Text(string culture, Func<string> tr)
    {
        Strings.Culture = culture.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture);
        return tr();
    }

    [Fact]
    public void EnglishIsTheTextItself()
    {
        Assert.Equal("Show Note Names", Text("", () => Strings.Tr("Show Note Names")));
        Assert.Equal("Cannot open \"x.mid\"", Text("", () => Strings.Tr("Cannot open \"%1\"", "x.mid")));
        Assert.Equal("B", Text("", () => Strings.Translate("CDraw", "B")));
        // as the C++ code without a translation: tr("%n bar(s)", nullptr, n)
        Assert.Equal("1 bar(s)", Text("", () => Strings.TrPlural("%n bar(s)", 1)));
        Assert.Equal("en", Text("en", () => "en"));
        Assert.Equal("Show Note Names", Text("en", () => Strings.Tr("Show Note Names")));
    }

    [Fact]
    public void SpanishAndGerman()
    {
        // pianobooster_es.ts, as it is (with its typo)
        Assert.Equal("Mostar el Nombre de las Notas", Text("es", () => Strings.Tr("Show Note Names")));
        Assert.Equal("Mostar el Nombre de las Notas", Text("es-ES", () => Strings.Tr("Show Note Names"))); // falls back to es
        Assert.Equal("Notennamen anzeigen", Text("de", () => Strings.Tr("Show Note Names")));
        // a text that is not in the .ts files stays in English
        Assert.Equal("Not a text of PianoBooster", Text("es", () => Strings.Tr("Not a text of PianoBooster")));
    }

    [Fact]
    public void TranslatedByContext()
    {
        // pianobooster_de.ts: "B" is "H" in CDraw (the note names) and "B" in GuiTopBar (the keys)
        Assert.Equal("H", Text("de", () => Strings.Translate("CDraw", "B")));
        Assert.Equal("B", Text("de", () => Strings.Translate("GuiTopBar", "B")));
        Assert.Equal("H", Text("de", () => Strings.Tr("B"))); // without a context: the first one
        // a context without a translation of its own: the text without a context
        Assert.Equal("Do", Text("es", () => Strings.Translate("CDraw", "C")));
    }

    [Fact]
    public void Plurals()
    {
        Assert.Equal("1 compás", Text("es", () => Strings.TrPlural("%n bar(s)", 1)));
        Assert.Equal("5 compases", Text("es", () => Strings.TrPlural("%n bar(s)", 5)));
        Assert.Equal("0 compases", Text("es", () => Strings.TrPlural("%n bar(s)", 0)));
        // German has no translation of it: English
        Assert.Equal("5 bar(s)", Text("de", () => Strings.TrPlural("%n bar(s)", 5)));

        Assert.Equal(0, Strings.PluralForm(CultureInfo.GetCultureInfo("ja"), 5));
        Assert.Equal(0, Strings.PluralForm(CultureInfo.GetCultureInfo("fr"), 0));
        Assert.Equal(1, Strings.PluralForm(CultureInfo.GetCultureInfo("es"), 0));
        Assert.Equal(new[] { 2, 0, 1, 2, 0, 2 }, new[] { 0, 1, 3, 11, 21, 25 }.Select(n => Strings.PluralForm(CultureInfo.GetCultureInfo("ru"), n)));
        Assert.Equal(new[] { 2, 0, 1, 2, 2, 1 }, new[] { 0, 1, 3, 12, 21, 22 }.Select(n => Strings.PluralForm(CultureInfo.GetCultureInfo("pl"), n)));
        Assert.Equal(new[] { 2, 0, 1, 2 }, new[] { 0, 1, 4, 5 }.Select(n => Strings.PluralForm(CultureInfo.GetCultureInfo("cs"), n)));
    }

    // A small .ts file with each case of the script
    private const string Ts = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE TS>
        <TS version="2.1" language="de">
        <context>
            <name>CDraw</name>
            <message><source>B</source><translation>H</translation></message>
            <message><source>C#</source><translation>Cis</translation></message>
            <message><source>Empty</source><translation type="unfinished"></translation></message>
            <message><source>Unfinished</source><translation type="unfinished">Unfertig</translation></message>
            <message><source>Old</source><translation type="obsolete">Alt</translation></message>
            <message><source>Gone</source><translation type="vanished">Weg</translation></message>
        </context>
        <context>
            <name>GuiTopBar</name>
            <message><source>B</source><translation>B</translation></message>
            <message><source>Same &amp; "quoted"</source><translation>Gleich &amp; „zitiert“</translation></message>
            <message numerus="yes"><source>%n bar(s)</source><translation><numerusform>%n Takt</numerusform><numerusform>%n Takte</numerusform></translation></message>
        </context>
        <context>
            <name>QtWindow</name>
            <message><source>Same &amp; "quoted"</source><translation>Gleich &amp; „zitiert“</translation></message>
        </context>
        </TS>
        """;

    [Fact]
    public void ScriptConvertsTsToResx()
    {
        if (!OperatingSystem.IsWindows())
            return; // the script is for Windows PowerShell

        string tsDir = Path.Combine(_folder, "ts"), outDir = Path.Combine(_folder, "out");
        Directory.CreateDirectory(tsDir);
        File.WriteAllText(Path.Combine(tsDir, "pianobooster_de.ts"), Ts);
        File.WriteAllText(Path.Combine(tsDir, "pianobooster_blank.ts"), Ts.Replace("language=\"de\"", ""));
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "Strings.fr.resx"), "<root />"); // no longer produced: removed

        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(RepositoryRoot(), "tools", "ts-to-resx.ps1"), "-TsDir", tsDir, "-OutDir", outDir, "-All" })
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + errors);
        Assert.Contains("by context: 'B'", output);
        Assert.DoesNotContain("Same", output); // the same translation in two contexts is not a conflict

        Assert.Equal(new[] { "Strings.de.resx", "Strings.resx" },
            Directory.GetFiles(outDir).Select(Path.GetFileName).Order().ToArray());
        Assert.Empty(XDocument.Load(Path.Combine(outDir, "Strings.resx")).Root!.Elements("data"));

        var data = XDocument.Load(Path.Combine(outDir, "Strings.de.resx")).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
        Assert.Equal(new()
        {
            ["B"] = "H",
            ["CDraw|B"] = "H",
            ["GuiTopBar|B"] = "B",
            ["C#"] = "Cis",
            ["Unfinished"] = "Unfertig", // as lrelease without -nounfinished
            ["Same & \"quoted\""] = "Gleich & „zitiert“",
            ["%n bar(s)#0"] = "%n Takt",
            ["%n bar(s)#1"] = "%n Takte",
        }, data);
    }
}
