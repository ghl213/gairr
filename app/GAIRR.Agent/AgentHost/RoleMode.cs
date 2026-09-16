using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAIRR.AgentHost;

/// <summary>角色档案：用户可自定义的"身份壳"（谁在用），下挂一组工作模式（怎么干活）。
/// 两种数据形态（免编译热加载）：
///   扁平：&lt;工具目录&gt;/roles/&lt;name&gt;.json —— 提示词/流程引用共享池 prompts/、flows/；
///   角色包：&lt;工具目录&gt;/roles/&lt;name&gt;/role.json（目录名=角色 name，决策 B）+ 可选 prompts/、flows/ 私有文件
///          —— 私有提示词/流程跟角色走，整体复制目录=整体分发，仍可回退共享池。
/// 一个角色含其全部工作模式，增删改免编译热加载。</summary>
public class RoleProfile
{
    /// <summary>角色 id（同文件内唯一；全局唯一性由 UI 层 tag = role|mode 承载，不强制）。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>角色显示名（UI 两级选择器左侧列文本）。</summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";

    /// <summary>角色图标字符（emoji）。</summary>
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";

    /// <summary>角色说明（UI 悬停提示）。</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>默认模式名：选中该角色时自动启用的模式（缺省取 modes 首项）。</summary>
    [JsonPropertyName("defaultMode")] public string DefaultMode { get; set; } = "";

    /// <summary>展示排序号（越小越靠前，默认 100 排在未显式设置角色的后面；同级按 DisplayName 排序）。
    /// 作用在角色选择器左侧角色列的先后顺序，便于把常用角色置顶（如开发视角软件工程师排第一）。</summary>
    [JsonPropertyName("order")] public int Order { get; set; } = 100;

    /// <summary>该角色下的工作模式清单（JSON 顺序即 UI 顺序）。</summary>
    [JsonPropertyName("modes")] public List<ModeProfile> Modes { get; set; } = new();

    /// <summary>运行时标记：该角色所属角色包目录（目录型角色包 = roles/&lt;name&gt; 的绝对路径；扁平角色=null）。不参与序列化。</summary>
    [JsonIgnore] public string? PackageDir { get; set; }
}

/// <summary>工作模式 = 一个完整的命名执行方案：提示词文件（可选）+ 线性流程模板（可选）。
/// 缺省 displayName 回退 name、icon 回退 📋。加模式=往角色的 modes 数组追加一项 JSON 即可。</summary>
public class ModeProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";

    [JsonPropertyName("icon")] public string Icon { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>提示词文件：&lt;角色包目录&gt;/prompts/、&lt;工具目录&gt;/prompts/ 或 项目 .gairr/prompts/ 下的 md 文件名；
    /// 留空=沿用引擎当前默认（按 SystemCfg.AgentMode 选 agent-agile.md / agent-deep.md）。
    /// 解析优先级见 RoleModeStore.ResolvePromptFile：角色包内 → 工具级共享池 → 项目级。</summary>
    [JsonPropertyName("promptFile")] public string? PromptFile { get; set; }

    /// <summary>流程模板名：引用 &lt;角色包目录&gt;/flows/ 或 &lt;工具目录&gt;/flows/ 下的 &lt;name&gt;.json
    /// （沿用现有 Flow 免编译机制）；留空=纯提示词自主执行（Agent 自主决策，无固定步骤）。</summary>
    [JsonPropertyName("flowName")] public string? FlowName { get; set; }

    /// <summary>所属角色包目录（目录型角色包=roles/&lt;name&gt;/ 目录；扁平角色=null，提示词/流程直接走共享池）。
    /// 仅内存回填，不参与 JSON 序列化：用于 prompt/flow 先查"角色包内"再回退共享池。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PackageDir { get; set; }
}

/// <summary>角色/模式仓库存取：工具级 &lt;exe目录&gt;/roles/；
/// 目录为空时自动落默认"软件工程师"示例角色包（roles/software-engineer/role.json，方案 C：默认角色也整理为包目录形态）。
/// 旧扁平角色（默认 + 用户自建 roles/*.json）首次加载时自动全量迁移为包目录（读旧写新、保留用户修改、成功后删除旧文件）。
/// 两级自定义都直接编辑 JSON（复制目录=复制角色，追加 modes 项=加模式）。</summary>
public static class RoleModeStore
{
    /// <summary>仓库根目录：&lt;工具目录&gt;/roles/</summary>
    public static string Dir() => Path.Combine(AppContext.BaseDirectory, "roles");

    /// <summary>加载全部角色（损坏文件跳过并记日志；目录不存在/为空时先落默认示例角色再重试一次）。
    /// 两种形态并扫：扁平 roles/*.json（兼容旧文件，PackageDir=null）+ 目录型角色包 roles/&lt;name&gt;/role.json
    /// （决策 B：目录名必须等于角色 name；目录内 prompts/、flows/ 为该角色私有资产）。</summary>
    public static List<RoleProfile> LoadAll()
    {
        var dir = Dir();
        var list = new List<RoleProfile>();
        try
        {
            Directory.CreateDirectory(dir);
            TryMigrateFlatRoles(dir);   // 旧扁平角色全量迁移为包目录（默认 + 用户自建，决策 B 泛化），见方法注释
            // ① 扁平形态：roles/*.json（PackageDir=null，提示词/流程走共享池；迁移失败的旧文件仍由此兜底加载）
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                LoadOne(list, f, null);
            // ② 目录型角色包：roles/<name>/role.json（决策 B：目录名=name，不符则跳过）
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var rf = Path.Combine(sub, "role.json");
                if (File.Exists(rf)) LoadOne(list, rf, sub);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Role] 角色目录读取失败: {ex.Message}"); }
        if (list.Count == 0 && SeedDefaults(dir)) return LoadAll();   // 无任何角色时先落默认示例再重读
        // 排序：order 升序（默认 100，角色包可设 order 置顶/靠前），同级再按 DisplayName 稳定排序
        return list.OrderBy(r => r.Order).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>旧扁平角色全量迁移为包目录形态（决策 B 泛化）：遍历 roles/*.json（默认 software-engineer + 用户自建扁平角色），
    /// 按文件内 name 转成 roles/&lt;name&gt;/role.json（目录名=角色 name），成功后删除旧扁平文件；
    /// 同名包目录已存在则视为已迁移，仅删除旧扁平（避免双形态并扫时同一角色重复出现）。
    /// 迁移失败（含名字含非法路径字符/JSON 损坏）保留原扁平文件，由下方扁平扫描继续兼容加载，保证不丢用户数据。</summary>
    static void TryMigrateFlatRoles(string dir)
    {
        string[] flats;
        try { flats = Directory.EnumerateFiles(dir, "*.json").ToArray(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Role] 扁平角色目录扫描失败: {ex.Message}"); return; }
        foreach (var flat in flats)
        {
            try
            {
                var r = JsonSerializer.Deserialize<RoleProfile>(File.ReadAllText(flat));
                if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;                       // 损坏/无名：保留，扁平扫描兜底
                if (r.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;               // 名字无法作目录名：保留扁平兼容
                var pkgFile = Path.Combine(dir, r.Name, "role.json");
                Directory.CreateDirectory(Path.GetDirectoryName(pkgFile)!);
                if (!File.Exists(pkgFile))
                {
                    File.Copy(flat, pkgFile);
                    System.Diagnostics.Debug.WriteLine($"[Role] 扁平角色 {r.Name} 已迁移为包目录 {pkgFile}");
                }
                File.Delete(flat);
                System.Diagnostics.Debug.WriteLine($"[Role] 扁平 {flat} 已移除（由包目录 {r.Name}/role.json 取代）");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Role] 扁平角色迁移失败（保留原文件）{flat}: {ex.Message}"); }
        }
    }

    /// <summary>加载单个角色文件并入列：目录型（pkgDir != null）校验目录名=角色 name（决策 B）后回填 PackageDir；
    /// 扁平型（pkgDir=null）仅做默认字段补齐。损坏/非法文件跳过并记日志。</summary>
    static void LoadOne(List<RoleProfile> list, string file, string? pkgDir)
    {
        try
        {
            var r = JsonSerializer.Deserialize<RoleProfile>(File.ReadAllText(file));
            if (r == null || string.IsNullOrWhiteSpace(r.Name)) return;
            if (pkgDir != null)
            {
                // 决策 B：目录名即角色 name，不一致视为放错位置的包，跳过防止 UI 出现游离角色
                var dirName = Path.GetFileName(pkgDir);
                if (!string.Equals(dirName, r.Name, StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"[Role] 角色包目录名({dirName})与 role.json 的 name({r.Name})不一致，已跳过：{file}");
                    return;
                }
            }
            if (string.IsNullOrWhiteSpace(r.DisplayName)) r.DisplayName = r.Name;
            foreach (var m in r.Modes)
            {
                if (string.IsNullOrWhiteSpace(m.Name)) continue;
                if (string.IsNullOrWhiteSpace(m.DisplayName)) m.DisplayName = m.Name;
                m.PackageDir = pkgDir;
            }
            if (r.Modes.Count > 0 && string.IsNullOrWhiteSpace(r.DefaultMode)) r.DefaultMode = r.Modes[0].Name;
            r.PackageDir = pkgDir;
            list.Add(r);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Role] 角色加载失败 {file}: {ex.Message}"); }
    }

    /// <summary>解析模式提示词文件路径：① 角色包内 prompts/&lt;file&gt;（存在时返回绝对路径，引擎 PromptPath 已支持绝对路径直用）
    /// → ② 原名交回引擎按既有规则（工具级共享池 prompts/ → 项目级 .gairr/prompts/）。</summary>
    public static string ResolvePromptFile(ModeProfile mode)
    {
        var f = mode?.PromptFile;
        if (string.IsNullOrWhiteSpace(f)) return f ?? "";
        if (mode?.PackageDir != null)
        {
            var p = Path.Combine(mode.PackageDir, "prompts", f);
            if (File.Exists(p)) return p;
        }
        return f;
    }

    /// <summary>取模式引用的流程模板：① 角色包内 flows/&lt;flowName&gt;.json → ② 工具级共享池（FlowTemplateStore.Find 原逻辑）。
    /// 返回 null 表示纯提示词自主模式或模板缺失。</summary>
    public static FlowTemplate? ResolveFlow(ModeProfile mode)
    {
        var n = mode?.FlowName;
        if (string.IsNullOrWhiteSpace(n)) return null;
        if (mode?.PackageDir != null)
        {
            var p = Path.Combine(mode.PackageDir, "flows", n + ".json");
            if (File.Exists(p))
            {
                try
                {
                    var t = JsonSerializer.Deserialize<FlowTemplate>(File.ReadAllText(p));
                    if (t != null && !string.IsNullOrWhiteSpace(t.Name) && t.Steps.Count > 0)
                        return t;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Role] 角色包流程加载失败 {p}: {ex.Message}"); }
            }
        }
        return FlowTemplateStore.Find(n);
    }

    /// <summary>按角色 id 查角色；不存在返回 null。</summary>
    public static RoleProfile? FindRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return null;
        return LoadAll().FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按 角色id|模式名 查模式；找不到返回 null。</summary>
    public static ModeProfile? FindMode(string roleName, string modeName)
    {
        if (string.IsNullOrWhiteSpace(roleName) || string.IsNullOrWhiteSpace(modeName)) return null;
        return LoadAll().FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase))
            ?.Modes.FirstOrDefault(m => string.Equals(m.Name, modeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>首次使用时落默认示例角色为包目录形态（决策 C：默认角色也整理成角色包 roles/software-engineer/，
    /// 目录名=name；modes 的提示词/流程仍复用共享池 prompts//flows/，不复制私有文件，便于全局热改）。
    /// 旧扁平角色（含默认 roles/software-engineer.json）由 LoadAll 开头的 TryMigrateFlatRoles 一次性迁移为包目录；
    /// 本种子仅在 roles/ 无任何角色时触发。</summary>
    static bool SeedDefaults(string dir)
    {
        var role = new RoleProfile
        {
            Name = "software-engineer",
            DisplayName = "软件工程师",
            Icon = "👤",
            Description = "默认示例角色：负责需求到代码的落地执行。本角色为包目录形态（roles/software-engineer/，目录名=name），复制整个目录即完整分发角色；提示词/流程默认复用共享池，若需私有可在此目录下建 prompts/、flows/ 覆盖同名文件。",
            DefaultMode = "agile",
            Modes = new List<ModeProfile>
            {
                new ModeProfile
                {
                    Name = "agile", DisplayName = "敏捷模式", Icon = "⚡",
                    PromptFile = "agent-agile.md",
                    Description = "敏捷模式：语义直达、够用即停、免清单；提示词文件 prompts/agent-agile.md（共享池）",
                },
                new ModeProfile
                {
                    Name = "deep", DisplayName = "严谨模式", Icon = "🔬",
                    PromptFile = "agent-deep.md",
                    Description = "严谨模式：四层检索、完整验证、防偏移回读；提示词文件 prompts/agent-deep.md（共享池）",
                },
                new ModeProfile
                {
                    Name = "dev-basic", DisplayName = "流程模式", Icon = "📋",
                    PromptFile = "agent-deep.md", FlowName = "dev-basic",
                    Description = "按定位→修改→编译验证→收尾汇报的线性流程执行（步骤定义在 flows/dev-basic.json，共享池）",
                },
            },
        };
        var path = Path.Combine(dir, role.Name, "role.json");   // 决策 C：默认角色落为包目录 roles/<name>/role.json
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) return true;   // 已存在其它文件但该默认角色包也在：视为已初始化
            File.WriteAllText(path, JsonSerializer.Serialize(role, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文直存，便于用户直接改
            }));
            System.Diagnostics.Debug.WriteLine($"[Role] 默认角色包已写入 {path}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Role] 默认角色包写入失败 {path}: {ex.Message}");
            return false;
        }
    }
}
