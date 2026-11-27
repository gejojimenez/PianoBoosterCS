// tr() of Qt: the translations of translations/*.ts of the C++ code, converted to
// translations/Strings.<culture>.resx by tools/ts-to-resx.ps1 (satellite assemblies).
// Part of the .NET port of PianoBooster. GPLv3.

using System.Globalization;
using System.Resources;

namespace PianoBooster.App;

internal static partial class Strings
{
    private static readonly ResourceManager s_resources =
        new("PianoBooster.App.translations.Strings", typeof(Strings).Assembly);

    /// <summary>
    /// The language of the translations (the QTranslator installed by QtWindow::refreshTranslate()).
    /// The invariant culture (the default) is English: the texts of the source code.
    /// </summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

    private static string? Lookup(string key)
    {
        try
        {
            return s_resources.GetString(key, Culture);
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
    }

    /// <summary>The translation of a text of the interface (QObject::tr()); the text itself if there is none.</summary>
    public static string Tr(string text) => Lookup(text) ?? text;

    /// <summary>tr(text).arg(a): "%1" is replaced.</summary>
    public static string Tr(string text, object a) => Tr(text).Replace("%1", a.ToString());

    /// <summary>tr(text).arg(a).arg(b).</summary>
    public static string Tr(string text, object a, object b) => Tr(text).Replace("%1", a.ToString()).Replace("%2", b.ToString());

    /// <summary>tr(text).arg(a).arg(b).arg(c).</summary>
    public static string Tr(string text, object a, object b, object c) => Tr(text, a, b).Replace("%3", c.ToString());

    /// <summary>tr(text).arg(a).arg(b).arg(c).arg(d).</summary>
    public static string Tr(string text, object a, object b, object c, object d) => Tr(text, a, b, c).Replace("%4", d.ToString());

    /// <summary>
    /// QCoreApplication::translate(context, text): the translation in that context (a class of the C++
    /// code, e.g. "CDraw"), for the few texts translated differently in different contexts; otherwise Tr(text).
    /// </summary>
    public static string Translate(string context, string text) => Lookup(context + "|" + text) ?? Tr(text);

    /// <summary>translate(context, text).arg(a).</summary>
    public static string Translate(string context, string text, object a) => Translate(context, text).Replace("%1", a.ToString());

    /// <summary>tr(text, nullptr, n): the plural form of the language, "%n" replaced by n.</summary>
    public static string TrPlural(string text, int n) =>
        (Lookup(text + "#" + PluralForm(Culture, n)) ?? text).Replace("%n", n.ToString());

    /// <summary>
    /// The index of the plural form (the numerusform of the .ts) for n, with the rules of the languages
    /// that PianoBooster installs; the others as English (two forms, singular for 1).
    /// </summary>
    public static int PluralForm(CultureInfo culture, int n)
    {
        int n10 = n % 10, n100 = n % 100;
        switch (culture.TwoLetterISOLanguageName)
        {
            case "ja":
            case "zh":
            case "ko":
            case "vi":
            case "th":
            case "id":
            case "ms":
                return 0;
            case "fr":
                return n <= 1 ? 0 : 1;
            case "is":
                return n10 == 1 && n100 != 11 ? 0 : 1;
            case "cs":
            case "sk":
                return n == 1 ? 0 : n >= 2 && n <= 4 ? 1 : 2;
            case "pl":
                return n == 1 ? 0 : n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14) ? 1 : 2;
            case "ru":
            case "uk":
                return n10 == 1 && n100 != 11 ? 0 : n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14) ? 1 : 2;
            default:
                return n == 1 ? 0 : 1;
        }
    }
}
