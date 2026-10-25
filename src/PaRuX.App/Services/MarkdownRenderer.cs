using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PaRuX.Services
{
    /// <summary>
    /// A small, self-contained Markdown to XAML renderer. No third-party package:
    /// it covers exactly what assistant replies actually use — headings, bold,
    /// italic, inline code, fenced code blocks, lists and links.
    /// </summary>
    public static class MarkdownRenderer
    {
        private static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Courier New");
        private static readonly SolidColorBrush InlineCodeBrush =
            new SolidColorBrush(Color.FromArgb(255, 0x9B, 0x6D, 0xF3));

        private static readonly Regex OrderedItemRx = new Regex(@"^(\s*)(\d+)[\.\)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex BulletItemRx = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex HeadingRx = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);

        /// <summary>Replaces the contents of <paramref name="target"/> with rendered Markdown.</summary>
        public static void Render(string markdown, Panel target)
        {
            if (target == null) return;
            target.Children.Clear();
            if (string.IsNullOrEmpty(markdown)) return;

            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var paragraph = new List<string>();
            int i = 0;

            while (i < lines.Length)
            {
                string line = lines[i];

                // ---- fenced code block ----
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph(paragraph, target);

                    string fenceLine = line.TrimStart();
                    string language = fenceLine.Substring(3).Trim();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        code.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Length) i++;   // consume the closing fence (absent while streaming)

                    target.Children.Add(BuildCodeBlock(string.Join(Environment.NewLine, code), language));
                    continue;
                }

                // ---- heading ----
                Match h = HeadingRx.Match(line);
                if (h.Success)
                {
                    FlushParagraph(paragraph, target);
                    target.Children.Add(BuildHeading(h.Groups[2].Value, h.Groups[1].Value.Length));
                    i++;
                    continue;
                }

                // ---- list block ----
                if (BulletItemRx.IsMatch(line) || OrderedItemRx.IsMatch(line))
                {
                    FlushParagraph(paragraph, target);

                    while (i < lines.Length)
                    {
                        Match bullet = BulletItemRx.Match(lines[i]);
                        Match ordered = OrderedItemRx.Match(lines[i]);
                        if (bullet.Success)
                        {
                            target.Children.Add(BuildListItem("•", bullet.Groups[2].Value, bullet.Groups[1].Value.Length));
                        }
                        else if (ordered.Success)
                        {
                            target.Children.Add(BuildListItem(ordered.Groups[2].Value + ".", ordered.Groups[3].Value, ordered.Groups[1].Value.Length));
                        }
                        else
                        {
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                // ---- blank line ends a paragraph ----
                if (line.Trim().Length == 0)
                {
                    FlushParagraph(paragraph, target);
                    i++;
                    continue;
                }

                paragraph.Add(line);
                i++;
            }

            FlushParagraph(paragraph, target);
        }

        private static void FlushParagraph(List<string> buffer, Panel target)
        {
            if (buffer.Count == 0) return;
            string text = string.Join(" ", buffer).Trim();
            buffer.Clear();
            if (text.Length == 0) return;

            var rtb = new RichTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, 0, 0, 8),
                LineHeight = 21
            };
            var p = new Paragraph();
            AddInlines(p.Inlines, text);
            rtb.Blocks.Add(p);
            target.Children.Add(rtb);
        }

        private static UIElement BuildHeading(string text, int level)
        {
            var rtb = new RichTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 6)
            };
            var p = new Paragraph
            {
                FontSize = level == 1 ? 20 : (level == 2 ? 17 : 15),
                FontWeight = FontWeights.SemiBold
            };
            AddInlines(p.Inlines, text);
            rtb.Blocks.Add(p);
            return rtb;
        }

        private static UIElement BuildListItem(string marker, string text, int indent)
        {
            int level = Math.Min(indent / 2, 3);

            var grid = new Grid { Margin = new Thickness(level * 16, 1, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bullet = new TextBlock
            {
                Text = marker,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetColumn(bullet, 0);
            grid.Children.Add(bullet);

            var rtb = new RichTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                LineHeight = 21
            };
            var p = new Paragraph();
            AddInlines(p.Inlines, text);
            rtb.Blocks.Add(p);
            Grid.SetColumn(rtb, 1);
            grid.Children.Add(rtb);

            return grid;
        }

        private static UIElement BuildCodeBlock(string code, string language)
        {
            var stack = new StackPanel();

            if (!string.IsNullOrWhiteSpace(language))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = language,
                    FontSize = 11,
                    Opacity = 0.6,
                    Margin = new Thickness(2, 0, 0, 4)
                });
            }

            var text = new TextBlock
            {
                Text = code,
                FontFamily = Mono,
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true
            };

            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = text
            };
            stack.Children.Add(scroller);

            var border = new Border { Child = stack };

            object style;
            if (Application.Current.Resources.TryGetValue("PaRuXCodeBlockStyle", out style) && style is Style)
            {
                border.Style = (Style)style;
            }
            else
            {
                border.Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
                border.CornerRadius = new CornerRadius(8);
                border.Padding = new Thickness(14, 12, 14, 12);
                border.Margin = new Thickness(0, 8, 0, 10);
            }

            return border;
        }

        // ------------------------------------------------------------------
        // Inline scanning: **bold**, *italic*, `code`, [text](url)
        // ------------------------------------------------------------------

        private static void AddInlines(InlineCollection target, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var literal = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                // inline code
                if (c == '`')
                {
                    int close = text.IndexOf('`', i + 1);
                    if (close > i)
                    {
                        Flush(target, literal);
                        target.Add(new Run
                        {
                            Text = text.Substring(i + 1, close - i - 1),
                            FontFamily = Mono,
                            Foreground = InlineCodeBrush
                        });
                        i = close + 1;
                        continue;
                    }
                }

                // bold  ** ** or __ __
                if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c
                    && !IsIntraWordUnderscore(text, i, c))
                {
                    string marker = new string(c, 2);
                    int close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                    if (close > i + 1 && !(c == '_' && FollowedByWordChar(text, close + 2)))
                    {
                        Flush(target, literal);
                        var bold = new Bold();
                        AddInlines(bold.Inlines, text.Substring(i + 2, close - i - 2));
                        target.Add(bold);
                        i = close + 2;
                        continue;
                    }
                }

                // italic  * * or _ _
                if ((c == '*' || c == '_') && !IsIntraWordUnderscore(text, i, c))
                {
                    int close = text.IndexOf(c, i + 1);
                    if (close > i + 1 && !(c == '_' && FollowedByWordChar(text, close + 1)))
                    {
                        Flush(target, literal);
                        var italic = new Italic();
                        AddInlines(italic.Inlines, text.Substring(i + 1, close - i - 1));
                        target.Add(italic);
                        i = close + 1;
                        continue;
                    }
                }

                // link  [text](url)
                if (c == '[')
                {
                    int closeBracket = text.IndexOf(']', i + 1);
                    if (closeBracket > i
                        && closeBracket + 1 < text.Length
                        && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket + 2);
                        if (closeParen > closeBracket)
                        {
                            string label = text.Substring(i + 1, closeBracket - i - 1);
                            string url = text.Substring(closeBracket + 2, closeParen - closeBracket - 2).Trim();

                            Uri? uri = null;
                            Uri.TryCreate(url, UriKind.Absolute, out uri);
                            if (uri != null && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto"))
                            {
                                Flush(target, literal);
                                var link = new Hyperlink { NavigateUri = uri };
                                link.Inlines.Add(new Run { Text = label.Length > 0 ? label : url });
                                target.Add(link);
                                i = closeParen + 1;
                                continue;
                            }
                        }
                    }
                }

                literal.Append(c);
                i++;
            }

            Flush(target, literal);
        }

        /// <summary>
        /// CommonMark does not treat underscores inside a word as emphasis, so
        /// snake_case identifiers survive intact.
        /// </summary>
        private static bool IsIntraWordUnderscore(string text, int index, char marker)
        {
            if (marker != '_') return false;
            return index > 0 && char.IsLetterOrDigit(text[index - 1]);
        }

        private static bool FollowedByWordChar(string text, int index)
        {
            return index < text.Length && char.IsLetterOrDigit(text[index]);
        }

        private static void Flush(InlineCollection target, StringBuilder buffer)
        {
            if (buffer.Length == 0) return;
            target.Add(new Run { Text = buffer.ToString() });
            buffer.Clear();
        }
    }
}
