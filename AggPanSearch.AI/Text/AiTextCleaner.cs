using System.Text;
using System.Text.RegularExpressions;

namespace AggPanSearch.AI;

/// <summary>
/// AI 文案清洗：去掉模型常见的包裹符号与「介绍：」类前缀，
/// 折叠多余空行，并按字数上限截到最近一句的句末。
/// </summary>
public static class AiTextCleaner
{
    private static readonly Regex LabelPrefix = new Regex(
        @"^\s*(资源介绍|内容简介|简\s*介|介\s*绍|描\s*述|说\s*明)\s*[:：]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex BlankLines = new Regex(@"[ \t]*\r?\n(?:[ \t]*\r?\n){2,}", RegexOptions.Multiline);

    private static readonly char[] SentenceEnds = { '。', '！', '？', '；', '…', '.', '!', '?', ';' };

    /// <summary>
    /// 清洗并截断文案。
    /// </summary>
    /// <param name="raw">模型原始输出。</param>
    /// <param name="maxChars">字数上限，小于等于 0 表示不截断。</param>
    public static string Clean(string raw, int maxChars = 0)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        // 去掉 ``` 代码块包裹
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstBreak = text.IndexOf('\n');
            if (firstBreak > 0)
            {
                text = text.Substring(firstBreak + 1);
            }
            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 3);
            }
            text = text.Trim();
        }

        text = StripWrapper(text);
        text = LabelPrefix.Replace(text, string.Empty).Trim();
        text = StripWrapper(text);

        // 逐行去尾部空白，折叠 3 行以上空行
        string[] lines = text.Split('\n');
        StringBuilder builder = new StringBuilder(text.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }
            builder.Append(lines[i].TrimEnd());
        }

        text = BlankLines.Replace(builder.ToString(), "\n\n").Trim();

        return Truncate(text, maxChars);
    }

    /// <summary>按字数上限截断到最近的句末标点，避免半句结尾。</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        string slice = text.Substring(0, maxChars);
        int cut = slice.LastIndexOfAny(SentenceEnds);
        if (cut >= maxChars / 2)
        {
            return slice.Substring(0, cut + 1).TrimEnd();
        }

        return slice.TrimEnd() + "…";
    }

    /// <summary>去掉模型习惯加的成对包裹符号（引号、书名号）。</summary>
    private static string StripWrapper(string text)
    {
        string value = text.Trim();

        (char Open, char Close)[] pairs =
        {
            ('"', '"'),
            ('\'', '\''),
            ('“', '”'),
            ('‘', '’'),
            ('《', '》'),
            ('「', '」'),
            ('{', '}'),
            ('[', ']')
        };

        bool changed = true;
        while (changed && value.Length >= 2)
        {
            changed = false;
            foreach ((char open, char close) in pairs)
            {
                if (value[0] == open && value[value.Length - 1] == close)
                {
                    value = value.Substring(1, value.Length - 2).Trim();
                    changed = true;
                    break;
                }
            }
        }

        return value;
    }
}
