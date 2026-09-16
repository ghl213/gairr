// MiniJson.Parser.cs — GAIRR.StatServer 迷你 JSON 库：递归下降解析器
// 覆盖 object/array/string/number/bool/null；字符串含全转义与 \uXXXX（含代理对合并）。
// 非法输入统一抛 JsonParseException（带位置），供 HTTP 层捕获返回 400。

using System.Globalization;
using System.Text;

namespace GAIRR.StatServer;

public static partial class MiniJson
{
    /// <summary>递归下降解析器。单遍扫描，Pos 为当前字符索引（0 起）。</summary>
    private sealed class Parser
    {
        private readonly string _s;
        public int Pos;
        public Parser(string s) => _s = s;
        public bool AtEnd => Pos >= _s.Length;

        /// <summary>跳过空白（空格/制表/CR/LF）。</summary>
        public void SkipWs()
        {
            while (Pos < _s.Length && (_s[Pos] == ' ' || _s[Pos] == '\t' || _s[Pos] == '\r' || _s[Pos] == '\n'))
                Pos++;
        }

        /// <summary>在当前位置构造解析异常。</summary>
        public JsonParseException Error(string msg) => new(msg, Pos);

        /// <summary>按首字符分发解析一个 JSON 值。</summary>
        public object? ParseValue()
        {
            if (AtEnd) throw Error("JSON 意外结束");
            return _s[Pos] switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => ParseString(),
                't' => ParseLiteral("true", true),
                'f' => ParseLiteral("false", false),
                'n' => ParseLiteral("null", null),
                _ => ParseNumber(),
            };
        }

        /// <summary>校验字面量（true/false/null）并返回对应值。</summary>
        private object? ParseLiteral(string literal, object? value)
        {
            if (Pos + literal.Length > _s.Length || string.CompareOrdinal(_s, Pos, literal, 0, literal.Length) != 0)
                throw Error($"无效字面量，期望 {literal}");
            Pos += literal.Length;
            return value;
        }

        /// <summary>解析 object：{ "k": v, ... }，结果用 Ordinal 序字典（大小写敏感，键不合并）。</summary>
        private Dictionary<string, object?> ParseObject()
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            Pos++; // '{'
            SkipWs();
            if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return obj; }
            while (true)
            {
                SkipWs();
                if (AtEnd || _s[Pos] != '"') throw Error("object 键必须是字符串");
                var key = ParseString();
                SkipWs();
                if (AtEnd || _s[Pos] != ':') throw Error("object 键后缺少 :");
                Pos++;
                SkipWs();
                obj[key] = ParseValue();
                SkipWs();
                if (AtEnd) throw Error("object 意外结束");
                if (_s[Pos] == ',') { Pos++; continue; }
                if (_s[Pos] == '}') { Pos++; return obj; }
                throw Error("object 中缺少 , 或 }");
            }
        }

        /// <summary>解析 array：[ v, ... ]。</summary>
        private List<object?> ParseArray()
        {
            var arr = new List<object?>();
            Pos++; // '['
            SkipWs();
            if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return arr; }
            while (true)
            {
                SkipWs();
                arr.Add(ParseValue());
                SkipWs();
                if (AtEnd) throw Error("array 意外结束");
                if (_s[Pos] == ',') { Pos++; continue; }
                if (_s[Pos] == ']') { Pos++; return arr; }
                throw Error("array 中缺少 , 或 ]");
            }
        }

        /// <summary>解析字符串：处理全转义，控制字符必须转义，UTF-16 原样保留。</summary>
        private string ParseString()
        {
            Pos++; // '"'
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw Error("字符串意外结束");
                var c = _s[Pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (AtEnd) throw Error("转义序列意外结束");
                    var e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u': sb.Append(ParseUnicodeEscape()); break;
                        default: throw Error($"无效转义序列 \\{e}");
                    }
                }
                else if (c < 0x20)
                    throw Error("字符串中存在未转义控制字符");
                else
                    sb.Append(c);
            }
        }

        /// <summary>解析 \uXXXX 为字符串（增补码位合并后返回 2 字符 surrogate pair）。</summary>
        private string ParseUnicodeEscape()
        {
            var unit = ReadHex4();
            if (unit >= 0xD800 && unit <= 0xDBFF && Pos + 1 < _s.Length && _s[Pos] == '\\' && _s[Pos + 1] == 'u')
            {
                Pos += 2;
                var low = ReadHex4();
                if (low < 0xDC00 || low > 0xDFFF) throw Error("Unicode 代理对低半区无效");
                return char.ConvertFromUtf32(char.ConvertToUtf32((char)unit, (char)low));
            }
            return ((char)unit).ToString();
        }

        /// <summary>读 4 位十六进制（\u 转义用）。</summary>
        private int ReadHex4()
        {
            if (Pos + 4 > _s.Length) throw Error("\\u 后不足 4 位十六进制");
            var v = 0;
            for (var i = 0; i < 4; i++)
            {
                var c = _s[Pos++];
                var d = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => throw Error("\\u 含非十六进制字符"),
                };
                v = v * 16 + d;
            }
            return v;
        }

        /// <summary>解析 number：-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?，InvariantCulture 转 double。</summary>
        private double ParseNumber()
        {
            var start = Pos;
            if (Pos < _s.Length && _s[Pos] == '-') Pos++;
            if (AtEnd || !char.IsDigit(_s[Pos])) throw Error("无效 JSON 值");
            if (_s[Pos] == '0') Pos++; // 前导零不允许（01 非法）
            else while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++;
            if (Pos < _s.Length && _s[Pos] == '.')
            {
                Pos++;
                if (AtEnd || !char.IsDigit(_s[Pos])) throw Error("小数点后缺少数字");
                while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++;
            }
            if (Pos < _s.Length && (_s[Pos] == 'e' || _s[Pos] == 'E'))
            {
                Pos++;
                if (Pos < _s.Length && (_s[Pos] == '+' || _s[Pos] == '-')) Pos++;
                if (AtEnd || !char.IsDigit(_s[Pos])) throw Error("指数缺少数字");
                while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++;
            }
            var text = _s[start..Pos];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw Error($"数字超出范围：{text}");
            return d;
        }
    }
}
