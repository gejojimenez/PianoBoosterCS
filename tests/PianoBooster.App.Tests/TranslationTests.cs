using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Avalonia.Controls;
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

    // Phase 1.6: the names of the notes in the results (Draw.NoteNames and the key signature are
    // static too, so they are here and put back)
    [Fact]
    public void NoteNamesOfTheResults()
    {
        string[] names = Rendering.Draw.NoteNames;
        try
        {
            Assert.Equal(new[] { "C4", "C♯4", "E♭4", "B♭4", "B3", "A0", "C8" },
                new int?[] { 60, 61, 63, 70, 59, 21, 108 }.Select(n => NoteText.Name(n)));
            Assert.Equal(NoteText.None, NoteText.Name(null));
            Assert.Equal(NoteText.None, NoteText.Name(128));

            Core.StavePos.SetKeySignature(-6, 0); // G♭ major: flats (the white keys keep their names, as in the score)
            Assert.Equal(new[] { "G♭4", "D♭5", "B4" }, new int?[] { 66, 73, 71 }.Select(n => NoteText.Name(n)));
            Assert.Equal("F♯4", NoteText.Name(66, keyOfC: true)); // not the loaded song
            Core.StavePos.SetKeySignature(2, 0); // D major: sharps
            Assert.Equal("D♯4", NoteText.Name(63));

            Rendering.Draw.NoteNames = new[] { "C", "D", "E", "F", "G", "A", "B" }
                .Select(n => Text("es", () => Strings.Translate("CDraw", n))).ToArray();
            Assert.Equal("Do♯4", NoteText.Name(61));
        }
        finally
        {
            Rendering.Draw.NoteNames = names;
            Core.StavePos.SetKeySignature(0, 0);
        }
    }

    [Fact]
    public void TextsOfTheResults()
    {
        Assert.Equal("1 error", Text("es", () => Strings.TrPlural("%n error(s)", 1)));
        Assert.Equal("3 errores", Text("es", () => Strings.TrPlural("%n error(s)", 3)));
        Assert.Equal("¡Nueva mejor marca! 1 punto porcentual más.",
            Text("es", () => Strings.TrPlural("New best! %n percentage point(s) better.", 1)));
        Assert.Equal("Nota equivocada", Text("es", () => PracticeText.Kind(Core.PracticeErrorKind.Wrong)));
        Assert.Equal("Ambas manos", Text("es", () => PracticeText.Hand(Core.WhichPart.Both)));
        Assert.Equal("Sigue", Text("es", () => PracticeText.Mode(Core.PlayMode.FollowYou)));
        Assert.Equal("Prácticas de A (Sigue, Mano derecha)", Text("es", () => Strings.Tr("Practices of %1 (%2, %3)",
            "A", PracticeText.Mode(Core.PlayMode.FollowYou), PracticeText.Hand(Core.WhichPart.Right))));
        Assert.Equal("Wrong note", Text("de", () => PracticeText.Kind(Core.PracticeErrorKind.Wrong)));

        var practice = new Core.PracticeRecord
        {
            Score = 139, MaxScore = 170, Started = new DateTime(2026, 11, 27, 18, 0, 0),
            Ended = new DateTime(2026, 11, 27, 18, 2, 5),
        };
        Assert.Equal("139 / 170 (81 %)", Text("", () => PracticeText.Score(practice))); // 81.76: rounded down
        Assert.Equal("2:05", PracticeText.Duration(practice));
        Assert.Equal("Adelanto: 1   Retraso: 0   Nota equivocada: 2   Omitida: 0", Text("es", () => PracticeText.Counts(new[]
        {
            new Core.PracticeError(0, 0, 0, Core.PracticeErrorKind.Wrong, 60, 61, Core.WhichPart.Right),
            new Core.PracticeError(0, 0, 0, Core.PracticeErrorKind.Early, 60, 60, Core.WhichPart.Right),
            new Core.PracticeError(1, 0, 0, Core.PracticeErrorKind.Wrong, 62, 61, Core.WhichPart.Right),
        })));
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

    [Fact]
    public void AccessKeys()
    {
        Assert.Equal("E&xit", Strings.AvaloniaToQtAccessKey("E_xit"));
        Assert.Equal("MIDI input && output", Strings.AvaloniaToQtAccessKey("MIDI input & output"));
        Assert.Equal("a_b", Strings.AvaloniaToQtAccessKey("a__b"));
        Assert.Equal("_Salir", Strings.QtToAvaloniaAccessKey("&Salir"));
        Assert.Equal("Entrada & salida", Strings.QtToAvaloniaAccessKey("Entrada && salida"));
        Assert.Equal("a__b", Strings.QtToAvaloniaAccessKey("a_b"));
        // pianobooster_es.ts: "&File" -> "&Archivo"; a text of a menu of the XAML file is "_File"
        Assert.Equal("_Archivo", Text("es", () => Strings.TranslateXamlText("QtWindow", "_File", true)));
        Assert.Equal("_File", Text("", () => Strings.TranslateXamlText("QtWindow", "_File", true)));
    }

    [Fact]
    public void LanguageCodes()
    {
        Assert.Equal("es", Strings.CultureOfLanguage("es").Name);
        Assert.Equal("zh-HK", Strings.CultureOfLanguage("zh_HK").Name);
        Assert.Equal("sr-Latn", Strings.CultureOfLanguage("sr@latin").Name);
        Assert.Equal(CultureInfo.InvariantCulture, Strings.CultureOfLanguage("en"));
        Assert.Equal(CultureInfo.InvariantCulture, Strings.CultureOfLanguage(""));
        Assert.Equal(CultureInfo.InvariantCulture, Strings.CultureOfLanguage("xx_nonsense"));
        Assert.Equal("zh_HK", Strings.LanguageOfCulture("zh-HK"));
        Assert.Equal("sr@latin", Strings.LanguageOfCulture("sr-Latn"));

        // the satellite assemblies of the application are copied next to the tests
        List<string> installed = Strings.InstalledLanguages();
        Assert.Equal(18, installed.Count);
        Assert.Contains("es", installed);
        Assert.Equal(installed.OrderBy(l => l, StringComparer.OrdinalIgnoreCase), installed);
    }

    [Fact]
    public void TranslateTreeKeepsTheOriginals()
    {
        var label = new TextBlock { Text = "Show Note Names" };
        var button = new Button { Content = "_Help" };
        var changedByCode = new TextBlock { Text = "Song:" };
        var root = new StackPanel { Children = { label, button, changedByCode } };
        ToolTip.SetTip(button, "Piano Booster Help");

        Strings.Culture = CultureInfo.InvariantCulture;
        Strings.TranslateTree(root); // keeps the originals
        var added = new TextBlock { Text = "Show Note Names" };
        root.Children.Add(added); // e.g. a song added by the code: never translated
        changedByCode.Text = "Amazing Grace";

        Strings.Culture = CultureInfo.GetCultureInfo("es");
        Strings.TranslateTree(root);
        Assert.Equal("Mostar el Nombre de las Notas", label.Text);
        Assert.Equal("A_yuda", button.Content); // pianobooster_es.ts: "&Help" -> "A&yuda"
        Assert.Equal("Ayuda de Piano Booster", ToolTip.GetTip(button));
        Assert.Equal("Show Note Names", added.Text);
        Assert.Equal("Amazing Grace", changedByCode.Text);

        Strings.Culture = CultureInfo.GetCultureInfo("de");
        Strings.TranslateTree(root); // from the original, not from the Spanish text
        Assert.Equal("Notennamen anzeigen", label.Text);

        Strings.Culture = CultureInfo.InvariantCulture;
        Strings.TranslateTree(root);
        Assert.Equal("Show Note Names", label.Text);
        Assert.Equal("_Help", button.Content);
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
