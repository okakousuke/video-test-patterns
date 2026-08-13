using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace RawInspector;

/// <summary>
/// 使い方ドキュメント（Markdown）を画面へ出すための変換です。
///
/// **完全なMarkdown実装ではありません。** `docs/` で実際に使っている記法だけを見ます
/// （見出し・箇条書き・番号付き・表・コードブロック・強調・インラインコード・リンク・区切り線）。
/// 足りなくなったときは、記法を増やす前に文書のほうを直すほうが早いはずです。
///
/// 外部ライブラリを使わないのは、このアプリが依存を1つも持っていないためです。
/// ヘルプを出すためだけに依存を増やすと、配布物の作り方まで変わります。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly FontFamily BodyFont = new("Meiryo, Yu Gothic UI, Segoe UI");
    private static readonly FontFamily MonoFont = new("Consolas, MS Gothic");

    private static readonly Brush BodyText = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x30));
    private static readonly Brush HeadingText = new SolidColorBrush(Color.FromRgb(0x00, 0x5C, 0xB4));
    private static readonly Brush PanelBorder = new SolidColorBrush(Color.FromRgb(0xD0, 0xD5, 0xDC));
    private static readonly Brush CodeFill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7));
    private static readonly Brush BlockFill = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x18));
    private static readonly Brush BlockText = new SolidColorBrush(Color.FromRgb(0xD6, 0xDA, 0xC8));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(0xEB, 0xEF, 0xF5));

    private static readonly Regex OrderedItem = new(@"^\d+\.\s+", RegexOptions.Compiled);
    private static readonly Regex Separator = new(@"^:?-{3,}:?$", RegexOptions.Compiled);

    /// <param name="onLink">リンクを押したときの行き先です。文書内リンクと外部URLの両方が来ます。</param>
    public static FlowDocument Render(string markdown, Action<string> onLink)
    {
        var document = new FlowDocument
        {
            FontFamily = BodyFont,
            FontSize = 13,
            LineHeight = 21,
            Foreground = BodyText,
            Background = Brushes.White,
            PagePadding = new Thickness(28, 20, 28, 36),
            IsOptimalParagraphEnabled = true,
            IsHyphenationEnabled = false,
        };

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];

            if (line.Trim().Length == 0) { index++; continue; }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                document.Blocks.Add(ReadCodeBlock(lines, ref index));
                continue;
            }

            if (line.StartsWith('|'))
            {
                document.Blocks.Add(ReadTable(lines, ref index, onLink));
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                document.Blocks.Add(ReadHeading(line, onLink));
                index++;
                continue;
            }

            if (Separator.IsMatch(line.Trim()))
            {
                document.Blocks.Add(new BlockUIContainer(new System.Windows.Shapes.Rectangle
                {
                    Height = 1,
                    Fill = PanelBorder,
                    Margin = new Thickness(0, 6, 0, 6),
                }));
                index++;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || OrderedItem.IsMatch(line))
            {
                document.Blocks.Add(ReadList(lines, ref index, onLink));
                continue;
            }

            document.Blocks.Add(ReadParagraph(lines, ref index, onLink));
        }

        return document;
    }

    /// <summary>
    /// 見出しは、リンクの飛び先として名前を持たせます（`launcher.md#生成器の状態` のような指定用）。
    /// </summary>
    private static Paragraph ReadHeading(string line, Action<string> onLink)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        var text = line[level..].Trim();

        var paragraph = new Paragraph
        {
            Tag = Anchor(text),
            FontWeight = FontWeights.Bold,
            Foreground = level <= 2 ? HeadingText : BodyText,
            FontSize = level switch { 1 => 21, 2 => 16, _ => 13.5 },
            Margin = new Thickness(0, level == 1 ? 0 : 20, 0, level == 1 ? 14 : 6),
        };
        AppendInlines(paragraph.Inlines, text, onLink);

        if (level == 1)
        {
            paragraph.BorderBrush = PanelBorder;
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 8);
        }
        return paragraph;
    }

    /// <summary>見出しの飛び先の名前です。空白と記号の揺れで外さないよう、詰めて比べます。</summary>
    public static string Anchor(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c) && c != '`' && c != '*').ToArray());

    private static Paragraph ReadParagraph(string[] lines, ref int index, Action<string> onLink)
    {
        var text = new StringBuilder();
        while (index < lines.Length)
        {
            var line = lines[index];
            if (line.Trim().Length == 0) break;
            if (line.StartsWith('#') || line.StartsWith('|') || line.StartsWith("```", StringComparison.Ordinal)) break;
            if (line.StartsWith("- ", StringComparison.Ordinal) || OrderedItem.IsMatch(line)) break;

            // 折り返しは書き手の都合なので、表示では1つの段落へ繋げます。
            // 日本語は語の間に空白を入れないため、繋ぎ目に空白を足しません。
            if (text.Length > 0) text.Append(NeedsSpace(text[^1], line[0]) ? " " : "");
            text.Append(line.Trim());
            index++;
        }

        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        AppendInlines(paragraph.Inlines, text.ToString(), onLink);
        return paragraph;
    }

    /// <summary>行を繋ぐときに空白が要るかどうか。英数字どうしのときだけ足します。</summary>
    private static bool NeedsSpace(char previous, char next) =>
        previous < 0x80 && next < 0x80 && !char.IsWhiteSpace(previous) && !char.IsWhiteSpace(next);

    private static List ReadList(string[] lines, ref int index, Action<string> onLink)
    {
        var ordered = OrderedItem.IsMatch(lines[index]);
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(22, 0, 0, 0),
        };

        while (index < lines.Length)
        {
            var line = lines[index];
            string content;
            if (line.StartsWith("- ", StringComparison.Ordinal)) content = line[2..];
            else if (OrderedItem.IsMatch(line)) content = OrderedItem.Replace(line, "");
            else break;
            index++;

            // 項目の続き（次の行が字下げされているだけのもの）は同じ項目へ繋げます。
            while (index < lines.Length && lines[index].StartsWith("  ", StringComparison.Ordinal)
                   && lines[index].Trim().Length > 0
                   && !lines[index].TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                content += (NeedsSpace(content[^1], lines[index].TrimStart()[0]) ? " " : "") + lines[index].Trim();
                index++;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
            AppendInlines(paragraph.Inlines, content, onLink);
            list.ListItems.Add(new ListItem(paragraph));
        }
        return list;
    }

    private static Section ReadCodeBlock(string[] lines, ref int index)
    {
        index++; // ```
        var text = new StringBuilder();
        while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal))
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(lines[index]);
            index++;
        }
        if (index < lines.Length) index++; // 閉じの ```

        var paragraph = new Paragraph(new Run(text.ToString()))
        {
            FontFamily = MonoFont,
            FontSize = 12,
            LineHeight = 18,
            Foreground = BlockText,
            Margin = new Thickness(0),
        };
        // 折り返さずに横スクロールさせたいところですが、FlowDocument では枠の中で折り返します。
        // コマンド1行が長くなりすぎないよう、文書側で改行しておく前提です。
        return new Section(paragraph)
        {
            Background = BlockFill,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
        };
    }

    private static Table ReadTable(string[] lines, ref int index, Action<string> onLink)
    {
        var rows = new List<string[]>();
        while (index < lines.Length && lines[index].TrimStart().StartsWith('|'))
        {
            rows.Add(SplitRow(lines[index]));
            index++;
        }

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 14) };
        var columns = rows.Count > 0 ? rows[0].Length : 1;
        for (var i = 0; i < columns; i++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        var isHeader = true;
        foreach (var cells in rows)
        {
            // 区切り行（| --- | --- |）は表の一部ではないので出しません。
            if (cells.All(cell => Separator.IsMatch(cell.Trim()))) { isHeader = false; continue; }

            var row = new TableRow();
            if (isHeader) row.Background = HeaderFill;

            for (var i = 0; i < columns; i++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
                if (i < cells.Length) AppendInlines(paragraph.Inlines, cells[i], onLink);
                row.Cells.Add(new TableCell(paragraph)
                {
                    BorderBrush = PanelBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 5, 8, 5),
                    FontWeight = isHeader ? FontWeights.Bold : FontWeights.Normal,
                });
            }
            group.Rows.Add(row);
            isHeader = false;
        }
        return table;
    }

    private static string[] SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    /// <summary>
    /// 行の中の記法です。`コード`、**強調**、[表示](行き先) の3つだけ見ます。
    /// 閉じが無いものは記法として扱わず、そのままの文字として出します。
    /// </summary>
    private static void AppendInlines(InlineCollection target, string text, Action<string> onLink)
    {
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            target.Add(new Run(buffer.ToString()));
            buffer.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    target.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = MonoFont,
                        FontSize = 12,
                        Background = CodeFill,
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush();
                    var bold = new Bold();
                    AppendInlines(bold.Inlines, text[(i + 2)..end], onLink);
                    target.Add(bold);
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        Flush();
                        var destination = text[(close + 2)..end];
                        var link = new Hyperlink { Foreground = HeadingText, Cursor = Cursors.Hand };
                        AppendInlines(link.Inlines, text[(i + 1)..close], onLink);
                        link.Click += (_, _) => onLink(destination);
                        link.ToolTip = destination;
                        target.Add(link);
                        i = end + 1;
                        continue;
                    }
                }
            }

            buffer.Append(text[i]);
            i++;
        }
        Flush();
    }
}
