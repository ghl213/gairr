using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GAIRR.Core;

/// <summary>敏感信息守卫：发往大模型前对密钥/密码/敏感URL做可逆加密脱敏（ENC 令牌），模型返回后再解密交工具执行/UI 展示。
/// 开关与类别见 system.ini [Sensitive] 节（Enabled/Keys/Passwords/Urls）；加密密钥自动生成保存于 exe 同目录 sensitive.key
/// （32 字节随机，丢失则历史 ENC 令牌不可解，但本地历史明文不受影响）。AES-256-GCM 随机 IV：同一密文每次密文不同，模型跨轮
/// 看到同一明文的不同令牌属正常现象，解密只依赖令牌自身。</summary>
public static class SensitiveGuard
{
    /* ---------- 开关（SystemCfg.Init 从 system.ini [Sensitive] 节加载） ---------- */
    public static bool Enabled;                 // 总开关：1=发送边界掩码 + 返回边界解密
    public static bool MaskKeys = true;         // 密钥类：sk-/AKIA/ghp_/AIza/PRIVATE KEY 等已知令牌形态
    public static bool MaskPasswords = true;    // 密码类：password/api_key/token 等赋值语句的值
    public static bool MaskUrls = true;         // URL 类：带 userinfo(user:pass@) 或 key/token/secret 等敏感参数的连接串

    const string TokenMark = "ENC(";
    static readonly Regex TokenRe = new(@"ENC\(([A-Za-z0-9_-]{24,})\)", RegexOptions.Compiled);

    /* ---------- 识别正则（NonBacktracking：杜绝灾难性回溯；命中区间按优先级去重，先 URL 后密钥再密码赋值） ---------- */

    // URL 类：完整 URL 且含 userinfo 认证段，或查询串带 key/token/secret/signature/password 等敏感参数
    static readonly Regex UrlRe = new(
        @"https?://[^\s""'<>]*?(?:[^\s/@""'<>]+:[^\s/@""'<>]+@|(?:[?&])(?:key|token|access_token|api_key|apikey|secret|signature|passwd|password|sig|sign|credential|auth)=)[^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    // 密钥类：常见云/平台令牌前缀 + PEM 私钥头（私钥整段掩码由 PEM 头命中起始后交给密码类规则？否——单独处理见 MaskText）
    static readonly Regex KeyRe = new(
        @"sk-[A-Za-z0-9]{16,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    // 密码/凭据赋值类：password/pwd/api_key/token/secret 等键后的赋值值（分组3=值本身，保留键名与分隔符原文；
    // 可选引号必须写字符类 ['""]?——逐字字符串里 ""?'? 会折叠成 "'? 使引号成必选，无引号赋值将全部失配）
    static readonly Regex PwdRe = new(
        @"\b(password|passwd|pwd|passphrase|api[_-]?key|secret|token|access[_-]?key|secret[_-]?key|client[_-]?secret|app[_-]?secret|private[_-]?key|auth[_-]?token|refresh[_-]?token)\b(\s*['""]?\s*[:=]\s*['""]?)([^\s""';,}\]]{4,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    // PEM 私钥整块（从 BEGIN 头到 END 尾，跨行）
    static readonly Regex PemRe = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /* ---------- 密钥管理 ---------- */

    static readonly object keyGate = new();
    static byte[]? key;

    /// <summary>加密密钥：优先读 exe 同目录 sensitive.key；缺失则生成 32 字节随机密钥落盘（并发安全）</summary>
    static byte[] Key
    {
        get
        {
            if (key != null) return key;
            lock (keyGate)
            {
                if (key != null) return key;
                var path = Path.Combine(AppContext.BaseDirectory, "sensitive.key");
                try
                {
                    if (File.Exists(path))
                    {
                        var b = Convert.FromBase64String(File.ReadAllText(path).Trim());
                        if (b.Length == 32) return key = b;
                    }
                }
                catch { /* 损坏/非法则重建 */ }
                var k = new byte[32];
                RandomNumberGenerator.Fill(k);
                try { File.WriteAllText(path, Convert.ToBase64String(k)); } catch { /* 只读目录：进程内密钥仍可用，跨进程不可解 */ }
                return key = k;
            }
        }
    }

    /// <summary>AES-256-GCM 加密并包装为 ENC(base64url(iv|tag|cipher)) 令牌；失败时原样返回明文（不阻断发送）</summary>
    public static string Encrypt(string plain)
    {
        try
        {
            using var aes = new AesGcm(Key, 16);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);
            var pt = Encoding.UTF8.GetBytes(plain);
            var ct = new byte[pt.Length];
            var tag = new byte[16];
            aes.Encrypt(nonce, pt, ct, tag);
            var all = new byte[12 + 16 + ct.Length];
            Buffer.BlockCopy(nonce, 0, all, 0, 12);
            Buffer.BlockCopy(tag, 0, all, 12, 16);
            Buffer.BlockCopy(ct, 0, all, 28, ct.Length);
            return TokenMark + Base64Url(all) + ")";
        }
        catch { return plain; }
    }

    /// <summary>解密单个 ENC 令牌负载；失败（密钥丢失/被篡改/非本机密文）返回 null，调用方保留原令牌</summary>
    public static string? DecryptToken(string b64)
    {
        try
        {
            var all = UnBase64Url(b64);
            if (all.Length < 28 + 4) return null;
            using var aes = new AesGcm(Key, 16);
            var nonce = all[..12];
            var tag = all[12..28];
            var ct = all[28..];
            var pt = new byte[ct.Length];
            aes.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return null; }
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] UnBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    /* ---------- 文本掩码 / 还原 ---------- */

    /// <summary>对文本中的敏感片段（按类别开关）逐一替换为 ENC 令牌；无命中或开关关闭时原样返回</summary>
    public static string MaskText(string text)
    {
        if (!Enabled || string.IsNullOrEmpty(text)) return text;
        var hits = new List<(int Start, int End)>();

        void Collect(Regex re, int grp)
        {
            foreach (Match m in re.Matches(text))
            {
                var g = m.Groups[grp];
                if (!g.Success || g.Length < 4) continue;
                var s = g.Index;
                var e = s + g.Length;
                // 已被更高优先级类别覆盖的区间跳过（URL > PEM 私钥 > 密钥令牌 > 密码赋值）
                if (hits.Any(h => s < h.End && e > h.Start)) continue;
                hits.Add((s, e));
            }
        }

        if (MaskUrls) Collect(UrlRe, 0);
        if (MaskKeys) { Collect(PemRe, 0); Collect(KeyRe, 0); }
        if (MaskPasswords) Collect(PwdRe, 3);
        if (hits.Count == 0) return text;

        hits.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sb = new StringBuilder(text.Length + hits.Count * 24);
        var pos = 0;
        foreach (var (s, e) in hits)
        {
            if (s < pos) continue;   // 重叠保底（排序后理论上不会发生）
            sb.Append(text, pos, s - pos);
            sb.Append(Encrypt(text[s..e]));
            pos = e;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>还原文本中的 ENC 令牌为明文；解密失败保留原令牌不动</summary>
    public static string UnmaskText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(TokenMark, StringComparison.Ordinal) < 0) return text;
        return TokenRe.Replace(text, m => DecryptToken(m.Groups[1].Value) ?? m.Value);
    }

    /* ---------- 消息数组掩码（LLMClient 发送边界调用；入参为 NormalizeMessages 产物，可原地修改） ---------- */

    /// <summary>就地掩码消息数组中所有字符串字段：content / reasoning_content / tool_calls[].function.arguments（tool 结果正文随 content 一并掩码）</summary>
    public static JsonArray MaskJsonMessages(JsonArray messages)
    {
        if (!Enabled) return messages;
        foreach (var node in messages)
        {
            if (node is not JsonObject m) continue;
            MaskField(m, "content");
            MaskField(m, "reasoning_content");
            if (m["tool_calls"] is JsonArray tcs)
                foreach (var t in tcs)
                    if (t is JsonObject tc && tc["function"] is JsonObject fn)
                        MaskField(fn, "arguments");
        }
        return messages;
    }

    static void MaskField(JsonObject o, string key)
    {
        if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            o[key] = MaskText((string)v!);
    }

    /// <summary>返回边界解密：正文/思维链/各工具调用参数中的 ENC 令牌还原为明文后再交 AgentLoop 执行工具、UI 展示与历史落盘</summary>
    public static LlmResponse UnmaskResponse(LlmResponse r)
    {
        if (!Enabled) return r;
        List<LlmToolCall> calls;
        if (r.ToolCalls.Count == 0) calls = r.ToolCalls;
        else
        {
            calls = new List<LlmToolCall>(r.ToolCalls.Count);
            foreach (var tc in r.ToolCalls)
                calls.Add(new LlmToolCall(tc.Id, tc.Name, UnmaskText(tc.Arguments)));
        }
        return new LlmResponse(
            r.Content == null ? null : UnmaskText(r.Content),
            r.ReasoningContent == null ? null : UnmaskText(r.ReasoningContent),
            calls, r.Usage, r.FinishReason);
    }
}
