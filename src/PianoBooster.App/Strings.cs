// tr() of Qt. The texts stay in English until phase 6 (translations: .ts -> .resx).
// Part of the .NET port of PianoBooster. GPLv3.

namespace PianoBooster.App;

internal static class Strings
{
    /// <summary>The translation of a text of the interface (QObject::tr()).</summary>
    public static string Tr(string text) => text;

    /// <summary>tr(text).arg(a): "%1" is replaced.</summary>
    public static string Tr(string text, object a) => Tr(text).Replace("%1", a.ToString());

    /// <summary>tr(text).arg(a).arg(b).</summary>
    public static string Tr(string text, object a, object b) => Tr(text).Replace("%1", a.ToString()).Replace("%2", b.ToString());

    /// <summary>tr("%n bar(s)", nullptr, n): the plural in English.</summary>
    public static string TrPlural(string singular, string plural, int n) => Tr(n == 1 ? singular : plural).Replace("%n", n.ToString());
}
