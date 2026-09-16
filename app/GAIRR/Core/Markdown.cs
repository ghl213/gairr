using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace GAIRR.Core;

/// <summary>
/// 轻量 Markdown → FlowDocument 渲染器：标题/加粗/行内代码/列表/表格/代码块/引用/分隔线。
/// 对话区用 FlowDocumentScrollViewer 显示，天然支持选中复制。
/// </summary>
public static class Markdown
{
    static readonly Brush Fg = Hex("#EEF0F6");
    static readonly Brush Dim = Hex("#9AA0B4");
    static readonly Brush Accent = Hex("#9B9DF9");
    static readonly Brush CodeFg = Hex("#D6E2F0");
    static readonly Brush CodeBg = Hex("#40000000");
    static readonly Brush Line = Hex("#2E3040");
    static readonly Brush HeadBg = Hex("#226366F1");
    static readonly FontFamily Mono = new("Consolas, Microsoft YaHei, SimSun");

    static Brush Hex(string s) => (Brush)new BrushConverter().ConvertFromString(s)!;

    /// <summary>毒字符防线：清洗可能令 WPF 文本引擎崩溃的字符（C0 控制符(保留 \t/\n/\r)、C1 区、孤立代理、
    /// 非字符 U+FFFE/FFFF 与 U+FDD0-FDEF → U+FFFD）。PtsHost 排版遇此类字符会 fail-fast 直接杀进程；
    /// 历史曾因工具输出夹带二进制垃圾（session_history.json 千余处）致点击会话列表即崩，
    /// 全部 markdown→FlowDocument 渲染入口必须先经此清洗。无脏返回原串引用。</summary>
    public static string Sanitize(string? s)
    {
        if (s == null) return "";
        for (int i = 0; i < s.Length; i++)
            if (IsDirty(s, i))
            {
                var b = new StringBuilder(s.Length);
                for (int j = 0; j < s.Length; j++) b.Append(IsDirty(s, j) ? '\uFFFD' : s[j]);
                return b.ToString();
            }
        return s;
    }

    static bool IsDirty(string s, int i)
    {
        char c = s[i];
        bool hi = c >= 0xD800 && c <= 0xDBFF, lo = c >= 0xDC00 && c <= 0xDFFF;
        return hi && (i + 1 >= s.Length || !(s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF))
            || lo && (i == 0 || !(s[i - 1] >= 0xD800 && s[i - 1] <= 0xDBFF))
            || (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
            || (c >= 0x7F && c <= 0x9F)
            || c == 0xFFFE || c == 0xFFFF || (c >= 0xFDD0 && c <= 0xFDEF);
    }

    public static FlowDocument ToDoc(string md, string? fontFamily = null)
    {
        md = Sanitize(md);
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Foreground = Fg,
            FontFamily = string.IsNullOrWhiteSpace(fontFamily)
                ? new FontFamily("Microsoft YaHei, SimSun, Segoe UI, sans-serif")
                : new FontFamily(fontFamily)
        };
        if (string.IsNullOrWhiteSpace(md)) return doc;

        var lines = md.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var t = lines[i].Trim();

            // 代码块 ```...```
            if (t.StartsWith("```"))
            {
                var sb = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++)
                    sb.AppendLine(lines[i]);
                i++; // 跳过结束 ```
                AddCodeBlock(doc, sb.ToString().TrimEnd('\n', '\r'));
                continue;
            }

            // 分隔线 --- / ___ / ───
            if (Regex.IsMatch(t, @"^[-─_]{3,}$"))
            {
                doc.Blocks.Add(new Paragraph
                {
                    BorderBrush = Line,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 10, 0, 10),
                });
                i++;
                continue;
            }

            // 标题 #~####
            var hm = Regex.Match(t, @"^(#{1,4})\s+(.*)$");
            if (hm.Success)
            {
                AddHeading(doc, hm.Groups[1].Length, hm.Groups[2].Value);
                i++;
                continue;
            }

            // 表格：当前行以 | 开头且下一行是分隔行
            if (t.StartsWith("|") && i + 1 < lines.Length
                && lines[i + 1].Contains('-')
                && Regex.IsMatch(lines[i + 1].Trim(), @"^[\s|:-]+$"))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].Trim().StartsWith("|"))
                { rows.Add(lines[i].Trim()); i++; }
                AddTable(doc, rows);
                continue;
            }

            // 无序列表
            var ul = Regex.Match(t, @"^[-*•]\s+(.*)$");
            if (ul.Success) { AddBullet(doc, "•", ul.Groups[1].Value); i++; continue; }

            // 有序列表
            var ol = Regex.Match(t, @"^(\d+)[.)]\s+(.*)$");
            if (ol.Success) { AddBullet(doc, ol.Groups[1].Value + ".", ol.Groups[2].Value); i++; continue; }

            // 引用 >
            if (t.StartsWith(">"))
            {
                var p = new Paragraph
                {
                    Margin = new Thickness(10, 3, 0, 3),
                    BorderBrush = Accent,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0),
                };
                AddInline(p, t.TrimStart('>', ' '), fg: Dim);
                doc.Blocks.Add(p);
                i++;
                continue;
            }

            // 空行跳过
            if (t.Length == 0) { i++; continue; }

            // 普通段落
            var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
            AddInline(para, t);
            doc.Blocks.Add(para);
            i++;
        }
        return doc;
    }

    /// <summary>长 token 软断点：WPF 文档段落按可用宽折行，但 base64/URL/长日志等连续无空格拉丁串没有断点，
    /// 空间不足时整串横向溢出（超出气泡/可视区）。对这些超长串每隔 ZwspStep 字符注入零宽空格 U+200B
    /// 提供"可折行机会"：宽度足够时不折、不占显示位；仅当放不下才在断点折行（复制时可能带入零宽字符，属已知取舍）。
    /// 中文等每字符可断的语言不在此正则内，不受影响。</summary>
    static readonly Regex LongTokenRe = new(@"[A-Za-z0-9_\-./\\:@%+=?#~&,;()<>]{121,}");
    const int ZwspStep = 90;

    static string BreakTokens(string s)
    {
        if (s.Length <= 120) return s;
        return LongTokenRe.Replace(s, m =>
        {
            var v = m.Value;
            if (v.Length <= 120) return v;
            var sb = new StringBuilder(v.Length + v.Length / ZwspStep + 1);
            for (int i = 0; i < v.Length; i += ZwspStep)
            {
                if (i > 0) sb.Append('\u200B');
                sb.Append(v, i, Math.Min(ZwspStep, v.Length - i));
            }
            return sb.ToString();
        });
    }

    static void AddHeading(FlowDocument doc, int level, string text)
    {
        var size = level switch { 1 => 18, 2 => 16.5, 3 => 15, _ => 14 };
        var p = new Paragraph { Margin = new Thickness(0, level <= 2 ? 10 : 7, 0, 4) };
        AddInline(p, text, bold: true,
            fg: level <= 2 ? Accent : Fg, size: size);
        doc.Blocks.Add(p);
    }

    static void AddBullet(FlowDocument doc, string marker, string text)
    {
        var p = new Paragraph { Margin = new Thickness(14, 2, 0, 2) };
        p.Inlines.Add(new Run(marker + "  ") { Foreground = Accent });
        AddInline(p, text);
        doc.Blocks.Add(p);
    }

    static void AddCodeBlock(FlowDocument doc, string code)
    {
        var sec = new Section
        {
            Background = CodeBg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 6, 0, 6),
        };
        foreach (var ln in code.Split('\n'))
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run(BreakTokens(ln)) { FontFamily = Mono, FontSize = 12.5, Foreground = CodeFg });
            sec.Blocks.Add(p);
        }
        doc.Blocks.Add(sec);
    }

    static void AddTable(FlowDocument doc, List<string> rows)
    {
        if (rows.Count < 2) return;
        var head = SplitRow(rows[0]);
        var table = new Table { Margin = new Thickness(0, 6, 0, 6) };
        // 列宽用星号按可用宽均分（单列表格占满、多列不超气泡）：FlowDocument 默认按内容自然宽排布，
        // 多列表格内容总宽可远超可视区导致横向溢出，星号列让列收缩、单元格内文本折行。
        foreach (var _ in head) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        for (int r = 0; r < rows.Count; r++)
        {
            if (r == 1) continue; // 分隔行 |---|---|
            var cells = SplitRow(rows[r]);
            var row = new TableRow();
            for (int c = 0; c < head.Count; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInline(p, c < cells.Count ? cells[c] : "", bold: r == 0);
                row.Cells.Add(new TableCell(p)
                {
                    BorderBrush = Line,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = r == 0 ? HeadBg : Brushes.Transparent,
                });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        doc.Blocks.Add(table);
    }

    static List<string> SplitRow(string row)
    {
        var s = row.Trim();
        if (s.StartsWith("|")) s = s[1..];
        if (s.EndsWith("|")) s = s[..^1];
        return s.Split('|').Select(x => x.Trim()).ToList();
    }

    /// <summary>行内解析：**加粗** 与 `代码`，其余普通文本；文件路径转成可点击链接</summary>
    static void AddInline(Paragraph p, string s, bool bold = false, Brush? fg = null, double size = 0)
    {
        var baseFg = fg ?? Fg;
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            p.Inlines.Add(MakeRun(sb.ToString(), bold, baseFg, size));
            sb.Clear();
        }

        // 先按文件路径切分，再对每段做行内解析
        var pathRe = new Regex(@"([a-zA-Z]:\\[^\s<>""]+|(?:[\w\-]+/)+[\w\-]+\.[\w\-]+)");
        var parts = pathRe.Split(s);
        foreach (var part in parts)
        {
            if (pathRe.IsMatch(part))
            {
                Flush();
                var link = new Hyperlink(new Run(part)) { Foreground = Accent, Cursor = System.Windows.Input.Cursors.Hand };
                link.Click += (sender, e) => OpenFileFromMarkdown(part);
                p.Inlines.Add(link);
            }
            else
            {
                // 对非路径文本做原有的 **加粗 / `代码` 解析
                int i = 0;
                while (i < part.Length)
                {
                    if (part[i] == '`')
                    {
                        int j = part.IndexOf('`', i + 1);
                        if (j > i)
                        {
                            Flush();
                            p.Inlines.Add(new Run(BreakTokens(part.Substring(i + 1, j - i - 1)))
                            { FontFamily = Mono, Foreground = Hex("#F0A13E"), Background = CodeBg });
                            i = j + 1;
                            continue;
                        }
                    }
                    if (part[i] == '*' && i + 1 < part.Length && part[i + 1] == '*')
                    {
                        int j = part.IndexOf("**", i + 2, StringComparison.Ordinal);
                        if (j > i)
                        {
                            Flush();
                            AddInline(p, part.Substring(i + 2, j - i - 2), bold: true, fg: baseFg, size: size);
                            i = j + 2;
                            continue;
                        }
                    }
                    sb.Append(part[i++]);
                }
                Flush();
            }
        }
    }

    static void OpenFileFromMarkdown(string path)
    {
        // 尝试相对路径 → 绝对路径
        var root = AppConfig.ProjectRootStatic;
        var full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (System.IO.File.Exists(full))
        {
            // 通过 Application.Current.MainWindow 打开编辑窗
            if (Application.Current.MainWindow is MainWindow mw) mw.OpenEditor(full);
        }
    }

    static Run MakeRun(string text, bool bold, Brush fg, double size)
    {
        var run = new Run(BreakTokens(text)) { Foreground = fg };
        if (bold) run.FontWeight = FontWeights.Bold;
        if (size > 0) run.FontSize = size;
        return run;
    }
}

/// <summary>XAML 绑定转换器：string → FlowDocument</summary>
public class MarkdownConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Markdown.ToDoc(value as string ?? "", parameter as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
