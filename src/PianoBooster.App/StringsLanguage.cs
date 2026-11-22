// The language of the interface: what QtWindow::refreshTranslate() does with the QTranslator
// (choosing the language) and with listWidgetsRetranslateUi / retranslateUi() (translating again the
// texts of the windows, which in the port come from the XAML files). Also the list of installed
// languages for GuiPreferencesDialog::initLanguageCombo().
// Part of the .NET port of PianoBooster. GPLv3.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace PianoBooster.App;

internal static partial class Strings
{
    /// <summary>
    /// The culture of a language code of Qt (the "General/lang" setting, the names of the .ts files):
    /// "es", "zh_HK" -> "zh-HK", "sr@latin" -> "sr-Latn". The invariant culture (English, the texts of
    /// the source code) for "en", "" or a code that .NET does not know.
    /// </summary>
    public static CultureInfo CultureOfLanguage(string code)
    {
        string name = code.Replace("@latin", "-Latn").Replace('_', '-');
        if (name.Length == 0 || name.Equals("en", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.InvariantCulture;
        try
        {
            return CultureInfo.GetCultureInfo(name, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>The language code of Qt of a culture name: "zh-HK" -> "zh_HK", "sr-Latn" -> "sr@latin".</summary>
    public static string LanguageOfCulture(string cultureName) =>
        cultureName.Replace("-Latn", "@latin").Replace('-', '_');

    /// <summary>
    /// The installed translations (the pianobooster_&lt;code&gt;.qm files of the C++ code): the folders
    /// next to the program with a satellite assembly, as language codes of Qt, sorted by name.
    /// </summary>
    public static List<string> InstalledLanguages()
    {
        var languages = new List<string>();
        string assemblyName = typeof(Strings).Assembly.GetName().Name + ".resources.dll";
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(AppContext.BaseDirectory))
            {
                if (!File.Exists(Path.Combine(dir, assemblyName)))
                    continue;
                string culture = Path.GetFileName(dir);
                try
                {
                    CultureInfo.GetCultureInfo(culture, predefinedOnly: true);
                    languages.Add(LanguageOfCulture(culture));
                }
                catch (CultureNotFoundException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        languages.Sort(StringComparer.OrdinalIgnoreCase);
        return languages;
    }

    // ---- translating again the texts of the XAML files (listWidgetsRetranslateUi + retranslateUi) ----

    /// <summary>The context of tr() (the class of the C++ code) of the texts of each window or panel.</summary>
    private static readonly Dictionary<Type, string> s_contexts = new()
    {
        [typeof(MainWindow)] = "QtWindow",
        [typeof(SidePanel)] = "GuiSidePanel",
        [typeof(TopBar)] = "GuiTopBar",
        [typeof(LoopingPopup)] = "GuiLoopingPopup",
        [typeof(PreferencesDialog)] = "GuiPreferencesDialog",
        [typeof(MidiSetupDialog)] = "GuiMidiSetupDialog",
        [typeof(KeyboardSetupDialog)] = "GuiKeyboardSetupDialog",
        [typeof(SongDetailsDialog)] = "GuiSongDetailsDialog",
        [typeof(LibraryDialog)] = "GuiLibraryDialog",
        [typeof(SectionEditor)] = "GuiSectionEditor",
    };

    /// <summary>A translated text: its original (English, from the XAML file) and the text last put.</summary>
    private sealed class XamlText
    {
        public required AvaloniaProperty Property;
        public required string Original;
        public required string Context;
        public required bool AccessKey; // "_" marks the access key, as "&" in Qt
        public required string LastSet;
    }

    // the texts of each element, kept the first time (as listWidgetsRetranslateUi); an empty list for an
    // element without texts, so that the elements added later (songs, parts...) are never translated
    private static readonly ConditionalWeakTable<AvaloniaObject, List<XamlText>> s_xamlTexts = new();

    /// <summary>
    /// Translates the texts that the XAML file gave to root and to the elements inside it (Text, Content,
    /// Header, Title, ToolTip.Tip, Watermark). The first call keeps the originals: it must be done just after
    /// InitializeComponent(), before the code adds anything. The later calls translate them again into the
    /// current language, except those that the code has changed since (it translates them itself).
    /// </summary>
    public static void TranslateTree(Control root)
    {
        bool record = !s_xamlTexts.TryGetValue(root, out _);
        Translate(root, ContextOf(root, root.GetType()), record);
    }

    private static string ContextOf(Control root, Type type)
    {
        if (s_contexts.TryGetValue(type, out string? context))
            return context;
        foreach (ILogical ancestor in root.GetLogicalAncestors())
            if (s_contexts.TryGetValue(ancestor.GetType(), out context))
                return context;
        return "QObject";
    }

    private static void Translate(ILogical element, string context, bool record)
    {
        if (s_contexts.TryGetValue(element.GetType(), out string? own))
            context = own;

        if (element is AvaloniaObject obj)
        {
            if (!s_xamlTexts.TryGetValue(obj, out List<XamlText>? texts))
            {
                if (record)
                {
                    texts = Record(obj, context);
                    s_xamlTexts.Add(obj, texts);
                }
            }
            if (texts != null)
            {
                foreach (XamlText text in texts)
                {
                    if (!Equals(obj.GetValue(text.Property), text.LastSet))
                        continue; // changed by the code
                    string translated = TranslateXamlText(text.Context, text.Original, text.AccessKey);
                    obj.SetValue(text.Property, translated);
                    text.LastSet = translated;
                }
            }
        }

        foreach (ILogical child in element.LogicalChildren)
            Translate(child, context, record);
    }

    private static List<XamlText> Record(AvaloniaObject obj, string context)
    {
        var texts = new List<XamlText>();
        void Add(AvaloniaProperty property, bool accessKey)
        {
            if (obj.GetValue(property) is string { Length: > 0 } original)
                texts.Add(new XamlText { Property = property, Original = original, Context = context, AccessKey = accessKey, LastSet = original });
        }

        switch (obj)
        {
            case Window:
                Add(Window.TitleProperty, false);
                break;
            case MenuItem:
                Add(HeaderedSelectingItemsControl.HeaderProperty, true);
                break;
            case HeaderedContentControl:
                Add(HeaderedContentControl.HeaderProperty, true);
                break;
            case ContentControl:
                Add(ContentControl.ContentProperty, true);
                break;
            case TextBlock:
                Add(TextBlock.TextProperty, false);
                break;
            case TextBox:
                Add(TextBox.PlaceholderTextProperty, false);
                break;
        }
        Add(ToolTip.TipProperty, false);
        return texts;
    }

    /// <summary>tr() of a text of a XAML file; with an access key, "_" of Avalonia is "&amp;" of Qt.</summary>
    public static string TranslateXamlText(string context, string original, bool accessKey)
    {
        if (!accessKey)
            return Translate(context, original);
        string qt = AvaloniaToQtAccessKey(original);
        return QtToAvaloniaAccessKey(Translate(context, qt));
    }

    /// <summary>"E_xit" -> "E&amp;xit"; "__" (a "_") -> "_"; "&amp;" -> "&amp;&amp;".</summary>
    public static string AvaloniaToQtAccessKey(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '_')
            {
                if (i + 1 < text.Length && text[i + 1] == '_')
                {
                    sb.Append('_');
                    i++;
                }
                else
                    sb.Append('&');
            }
            else if (c == '&')
                sb.Append("&&");
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"Sa&amp;lir" -> "Sa_lir"; "&amp;&amp;" (a "&amp;") -> "&amp;"; "_" -> "__".</summary>
    public static string QtToAvaloniaAccessKey(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&')
                {
                    sb.Append('&');
                    i++;
                }
                else if (i + 1 < text.Length)
                    sb.Append('_');
            }
            else if (c == '_')
                sb.Append("__");
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
