// The rich text of Qt (the HTML subset of QMessageBox and QTextBrowser) shown with Avalonia text
// blocks. Avalonia has no HTML control; this understands what PianoBooster uses: headings,
// paragraphs, quotes, bold, italic, line breaks and links. Part of the .NET port of PianoBooster. GPLv3.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PianoBooster.App;

internal static partial class HtmlText
{
    [GeneratedRegex(@"<(/?)([a-zA-Z0-9]+)([^>]*)>|([^<]+)", RegexOptions.Singleline)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"<(style|head|script)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    /// <summary>The blocks of the text (one text block for each heading, paragraph...), one under the other.</summary>
    public static StackPanel ToPanel(string html, double fontSize = double.NaN, IBrush? quoteBrush = null)
    {
        var panel = new StackPanel { Spacing = 6 };
        html = HiddenRegex().Replace(html, "");

        SelectableTextBlock? block = null;
        var styles = new Stack<string>(); // b, i, a
        string? link = null;
        bool center = false;

        SelectableTextBlock CurrentBlock()
        {
            if (block == null)
            {
                block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
                if (!double.IsNaN(fontSize))
                    block.FontSize = fontSize;
                if (center)
                    block.TextAlignment = TextAlignment.Center;
                panel.Children.Add(block);
            }
            return block;
        }

        void NewBlock(string tag)
        {
            block = null;
            SelectableTextBlock b = CurrentBlock();
            switch (tag)
            {
                case "h1":
                    b.FontSize = (double.IsNaN(fontSize) ? 14 : fontSize) * 1.8;
                    b.FontWeight = FontWeight.Bold;
                    break;
                case "h2":
                    b.FontSize = (double.IsNaN(fontSize) ? 14 : fontSize) * 1.5;
                    b.FontWeight = FontWeight.Bold;
                    break;
                case "h3":
                    b.FontSize = (double.IsNaN(fontSize) ? 14 : fontSize) * 1.2;
                    b.FontWeight = FontWeight.Bold;
                    break;
                case "blockquote":
                    b.Margin = new Avalonia.Thickness(24, 0, 0, 0);
                    if (quoteBrush != null)
                        b.Foreground = quoteBrush;
                    break;
            }
        }

        foreach (Match m in TokenRegex().Matches(html))
        {
            if (m.Groups[4].Success)
            {
                string text = SpacesRegex().Replace(WebUtility.HtmlDecode(m.Groups[4].Value), " ");
                if (text.Trim().Length == 0 && block == null)
                    continue;
                Inline run;
                if (link != null)
                {
                    string url = link;
                    var linkText = new TextBlock
                    {
                        Text = text,
                        Foreground = Brushes.RoyalBlue,
                        TextDecorations = TextDecorations.Underline,
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    if (!double.IsNaN(fontSize))
                        linkText.FontSize = fontSize;
                    if (styles.Contains("b"))
                        linkText.FontWeight = FontWeight.Bold;
                    linkText.PointerPressed += (_, _) => OpenUrl(url);
                    ToolTip.SetTip(linkText, url);
                    run = new InlineUIContainer(linkText) { BaselineAlignment = BaselineAlignment.TextBottom };
                }
                else
                {
                    var r = new Run(text);
                    if (styles.Contains("b"))
                        r.FontWeight = FontWeight.Bold;
                    if (styles.Contains("i"))
                        r.FontStyle = FontStyle.Italic;
                    run = r;
                }
                CurrentBlock().Inlines!.Add(run);
                continue;
            }

            bool closing = m.Groups[1].Value == "/";
            string tagName = m.Groups[2].Value.ToLowerInvariant();
            switch (tagName)
            {
                case "p" or "h1" or "h2" or "h3" or "blockquote" or "div" or "tr":
                    if (closing)
                        block = null;
                    else
                        NewBlock(tagName);
                    break;
                case "center":
                    center = !closing;
                    if (block != null && block.Inlines!.Count == 0)
                        block.TextAlignment = center ? TextAlignment.Center : TextAlignment.Left; // <h2><center>
                    else
                        block = null;
                    break;
                case "br":
                    CurrentBlock().Inlines!.Add(new LineBreak());
                    break;
                case "b" or "strong":
                    if (closing) Pop(styles, "b"); else styles.Push("b");
                    break;
                case "i" or "em":
                    if (closing) Pop(styles, "i"); else styles.Push("i");
                    break;
                case "a":
                    link = closing ? null : HrefRegex().Match(m.Groups[3].Value) is { Success: true } href ? href.Groups[1].Value : null;
                    break;
                case "td" or "th":
                    if (closing)
                        CurrentBlock().Inlines!.Add(new Run("    "));
                    break;
            }
        }
        return panel;
    }

    private static void Pop(Stack<string> styles, string style)
    {
        var kept = new List<string>();
        while (styles.Count > 0)
        {
            string s = styles.Pop();
            if (s == style)
                break;
            kept.Add(s);
        }
        for (int i = kept.Count - 1; i >= 0; i--)
            styles.Push(kept[i]);
    }

    /// <summary>QDesktopServices::openUrl(): the web browser of the user.</summary>
    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Core.Log.Error("Cannot open " + url + ": " + e.Message);
        }
    }

}
