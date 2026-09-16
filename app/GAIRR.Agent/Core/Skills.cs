using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;

namespace GAIRR.Core;

/// <summary>一个技能 = 一个 SKILL.md（兼容开放规范：frontmatter name/description + 正文指令）</summary>
public class SkillDef : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";

    bool enabled = true;
    public bool Enabled { get => enabled; set { enabled = value; Notify(nameof(Enabled)); Notify(nameof(EnabledText)); } }

    public string EnabledText => enabled ? "启用" : "禁用";

    /// <summary>是否来自市场安装（本地列表行尾显示"市场"标记）</summary>
    public bool MarketInstalled { get; set; }

    /// <summary>列表显示名：市场安装的条目名后附〔市场〕标记</summary>
    public string NameDisplay => MarketInstalled ? Name + " 〔市场〕" : Name;

    /// <summary>悬停提示的使用前提说明（启用状态/市场来源）</summary>
    public string Preconditions =>
        (Enabled ? "当前已启用，任务匹配描述时可自动触发；用户也可在输入框输入 /技能名 强制触发"
                 : "⚠ 当前已禁用：任务不会自动触发该技能，请在列表中单击重新启用")
        + (MarketInstalled ? "；来自市场安装，卸载请进「市场」视图" : "");

    public SkillDef() { }
    public SkillDef(string name, string description, string path)
    {
        Name = name; Description = description; Path = path;
    }
}

/// <summary>声明式插件数据模型（支持加载状态、启用/禁用切换）</summary>
public class PluginDef : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";

    bool loaded = true;
    /// <summary>是否加载成功（失败时显示为错误状态，不可切换启用）</summary>
    public bool Loaded { get => loaded; set { loaded = value; Notify(nameof(Loaded)); Notify(nameof(StatusText)); Notify(nameof(StatusBrush)); } }

    bool enabled = true;
    public bool Enabled { get => enabled; set { enabled = value; Notify(nameof(Enabled)); Notify(nameof(StatusText)); Notify(nameof(StatusBrush)); } }

    /// <summary>是否来自市场安装（本地列表行尾显示"市场"标记）</summary>
    public bool MarketInstalled { get; set; }

    /// <summary>列表显示名：市场安装的条目名后附〔市场〕标记</summary>
    public string NameDisplay => MarketInstalled ? Name + " 〔市场〕" : Name;

    /// <summary>悬停提示的使用前提说明（加载/启用状态/市场来源）</summary>
    public string Preconditions
    {
        get
        {
            if (!Loaded) return "⚠ 加载失败：插件不可用，请检查 plugins 目录内 plugin.ini 配置与脚本；详情见状态栏/日志";
            var s = Enabled ? "当前已启用，匹配 Description 描述的任务可直接调用其工具"
                            : "⚠ 当前已禁用：Agent 看不到该插件的工具，请在列表中单击重新启用";
            if (MarketInstalled) s += "；来自市场安装，卸载请进「市场」视图";
            return s;
        }
    }

    /// <summary>状态文字：加载失败显示"加载失败"，空状态显示提示文案，否则显示"启用"/"禁用"</summary>
    public string StatusText
    {
        get
        {
            if (!Loaded)
            {
                // 空状态（Name="暂无插件"）显示描述而非"加载失败"
                if (Name == "暂无插件") return Description;
                return "加载失败";
            }
            return Enabled ? "启用" : "禁用";
        }
    }

    /// <summary>状态颜色：加载失败红色，空状态灰色，否则跟随启用状态（绿/灰）</summary>
    public System.Windows.Media.Brush StatusBrush
    {
        get
        {
            if (!Loaded)
            {
                if (Name == "暂无插件")
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44));
            }
            return Enabled
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
        }
    }

    public PluginDef() { }
    public PluginDef(string name, string description, string path, bool loaded = true)
    {
        Name = name; Description = description; Path = path; Loaded = loaded;
    }
}

/// <summary>内置工具数据模型（支持启用/禁用切换，禁用后 Agent 不可见）</summary>
public class ToolItemDef : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    bool enabled = true;
    public bool Enabled { get => enabled; set { enabled = value; Notify(nameof(Enabled)); Notify(nameof(StatusText)); Notify(nameof(StatusBrush)); } }

    public string StatusText => enabled ? "启用" : "禁用";
    public System.Windows.Media.Brush StatusBrush => enabled
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E))
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));

    public ToolItemDef() { }
    public ToolItemDef(string name, string description)
    {
        Name = name; Description = description;
    }
}

/// <summary>技能加载器：启动扫描目录形成清单（只把描述注入 system prompt），触发时才懒加载全文。
/// 热加载：Watch 监视目录内 *.md 变化，2 秒防抖重扫（保留启用状态），下个任务清单自动可见。</summary>
public class SkillLoader
{
    readonly string dir;
    readonly HashSet<string>? enabled;   // null = 全部启用
    readonly object scanGate = new();    // 重扫与构造期扫描互斥（watcher 回调来自后台线程）

    /// <summary>热加载完成事件：技能目录 *.md 变化触发重扫后回调最新列表（UI 订阅刷新技能面板）</summary>
    public event Action<List<SkillDef>>? Reloaded;

    FileSystemWatcher? watcher;          // 持强引用：防 watcher 被 GC 导致监视失效
    Timer? debounceTimer;

    public List<SkillDef> Skills { get; private set; } = new();

    public SkillLoader(AppConfig cfg)
    {
        var raw = cfg.Get("Skills", "Dir", "skills");
        dir = Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
        // 技能开关：优先 [Skills] Disabled 黑名单（缺省全开，配置只写例外）；
        // 兼容旧 [Skills] Enabled 白名单（未配 Disabled 时回退，行为同旧版）
        var disabled = SwitchStore.SkillsDisabled();
        if (disabled.Count > 0)
        {
            enabled = null;   // 黑名单模式下白名单集合置空，过滤改走 disabled
            this.disabledSet = disabled;
        }
        else
        {
            var list = cfg.Get("Skills", "Enabled", "");
            enabled = (list.Length == 0 || list == "1" || list.Equals("true", StringComparison.OrdinalIgnoreCase))
                ? null
                : new HashSet<string>(
                    list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
        }
        Scan();
    }

    HashSet<string>? disabledSet;   // 黑名单（Disabled= 配置）；null=未启用黑名单模式

    void Scan()
    {
        var fresh = new List<SkillDef>();
        Console.WriteLine($"[SkillLoader] Scanning dir: {dir}");
        if (Directory.Exists(dir))
        {
            // 标准结构：skills/<名称>/SKILL.md
            foreach (var sub in Directory.GetDirectories(dir))
            {
                Console.WriteLine($"[SkillLoader] Found subdir: {sub}");
                TryAdd(fresh, Path.Combine(sub, "SKILL.md"), Path.GetFileName(sub));
            }
            // 宽松结构：skills/<名称>.md 直接平铺
            foreach (var md in Directory.GetFiles(dir, "*.md"))
                TryAdd(fresh, md, Path.GetFileNameWithoutExtension(md));
        }
        else Console.WriteLine("[SkillLoader] Dir not found");
        Console.WriteLine($"[SkillLoader] Total skills: {fresh.Count}");
        lock (scanGate) Skills = fresh;   // 整引用替换：读方（Manifest/面板）拿一致快照，无需加锁
    }

    void TryAdd(List<SkillDef> list, string path, string fallbackName)
    {
        Console.WriteLine($"[SkillLoader] TryAdd: path={path}, fallback={fallbackName}");
        if (!File.Exists(path)) { Console.WriteLine("[SkillLoader] File not found"); return; }
        var (name, desc) = ParseFrontmatter(path);
        Console.WriteLine($"[SkillLoader] Parsed: name={name}, desc={desc}");
        if (name.Length == 0) name = fallbackName;
        if (enabled != null && !enabled.Contains(name)) { Console.WriteLine($"[SkillLoader] Filtered: {name} not in enabled list"); return; }
        if (list.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Console.WriteLine($"[SkillLoader] Duplicate: {name}"); return; }
        var skill = new SkillDef(name, desc, path);
        // 黑名单只是禁用，仍要显示在技能面板；除非文件被删除，否则不丢失
        if (disabledSet != null && disabledSet.Contains(name))
        {
            skill.Enabled = false;
            Console.WriteLine($"[SkillLoader] Added disabled: {name}");
        }
        else
        {
            Console.WriteLine($"[SkillLoader] Added: {name}");
        }
        list.Add(skill);
    }

    /// <summary>启动热加载监视（仅监视 *.md：技能文件变化才需重扫）。目录不存在则不监视。
    /// 变化经 2 秒防抖后全量重扫，保留用户启用状态，最后广播 Reloaded。</summary>
    public void Watch()
    {
        if (watcher != null) return;   // 已监视
        if (!Directory.Exists(dir)) return;
        try
        {
            watcher = new FileSystemWatcher(dir, "*.md") { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
            watcher.Changed += OnMdChanged; watcher.Created += OnMdChanged;
            watcher.Deleted += OnMdChanged; watcher.Renamed += OnMdChanged;
            watcher.EnableRaisingEvents = true;
        }
        catch { watcher = null; /* 监视失败不影响启动加载：退回重启生效 */ }
    }

    /// <summary>md 变化回调：2 秒防抖（Write 工具多事件/连续保存合并），到期后台重扫</summary>
    void OnMdChanged(object sender, FileSystemEventArgs e)
    {
        lock (scanGate)
        {
            debounceTimer?.Dispose();
            debounceTimer = new Timer(_ => Task.Run(Rescan), null, 2000, Timeout.Infinite);
        }
    }

    /// <summary>防抖到期执行：保留启用状态重扫，整引用替换后广播最新列表</summary>
    void Rescan()
    {
        // 记录旧启用状态（按名称），重扫后还原，避免用户在面板的禁用被重置
        var prevEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Skills) prevEnabled[s.Name] = s.Enabled;

        Scan();   // 构建新列表并整引用替换

        // 还原启用状态：新列表按名称匹配旧状态（默认保持 true）
        foreach (var s in Skills)
            if (prevEnabled.TryGetValue(s.Name, out var en)) s.Enabled = en;

        try { Reloaded?.Invoke(new List<SkillDef>(Skills)); } catch { /* 订阅方异常不影响扫描结果 */ }
    }

    /// <summary>手写解析 frontmatter（--- 之间的 key: value 行），不引 YAML 库</summary>
    static (string Name, string Desc) ParseFrontmatter(string path)
    {
        var name = ""; var desc = "";
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "---") return (name, desc);
            for (var i = 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t == "---") break;
                var ci = t.IndexOf(':');
                if (ci <= 0) continue;
                var k = t[..ci].Trim();
                var v = t[(ci + 1)..].Trim();
                if (k.Equals("name", StringComparison.OrdinalIgnoreCase)) name = v;
                else if (k.Equals("description", StringComparison.OrdinalIgnoreCase)) desc = v;
            }
        }
        catch { /* 读坏按空技能处理 */ }
        return (name, desc);
    }

    /// <summary>注入 system prompt 的技能清单（每个一行描述，不注全文）</summary>
    public string Manifest()
    {
        if (Skills.Count == 0) return "";
        var active = Skills.Where(s => s.Enabled).ToList();   // 禁用技能不进清单：模型看不到、不会触发
        if (active.Count == 0) return "";
        var sb = new StringBuilder(
            "\n可用技能（任务与某技能描述匹配时，先调 LoadSkill 取完整指令再执行；用户用 /技能名 可强制触发）：\n");
        foreach (var s in active) sb.Append("- ").Append(s.Name).Append(": ").Append(s.Description).Append('\n');
        return sb.ToString();
    }

    public SkillDef? Find(string name) =>
        Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>懒加载全文（LoadSkill 工具与 /斜杠 触发共用）</summary>
    public string LoadFull(string name)
    {
        var s = Find(name);
        if (s == null) return "错误：技能不存在 " + name;
        try { return File.ReadAllText(s.Path); }
        catch (Exception ex) { return "错误：技能文件读取失败 " + ex.Message; }
    }
}

/// <summary>市场条目数据模型（绑定市场列表行；状态机：install→installing→installed / failed→重试）</summary>
public class MarketEntryDef : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public CatalogEntry Entry { get; }              // 底层目录册条目（含 url/sha256，安装时用）
    public string Name => Entry.Name;
    public string Description => Entry.Desc;
    public string KindText => Entry.Kind == "plugin" ? "插件" : "技能";

    string cnDesc = "";
    /// <summary>中文摘要（后台大模型翻译结果；空=未翻译，界面回退英文原文）</summary>
    public string CnDesc { get => cnDesc; set { cnDesc = value; Notify(nameof(CnDesc)); Notify(nameof(DisplayDescription)); } }

    /// <summary>卡片摘要行显示文本：优先中文翻译，回退英文原文/占位提示</summary>
    public string DisplayDescription => CnDesc.Length > 0 ? CnDesc : (Entry.Desc.Length > 0 ? Entry.Desc : "（无描述）");

    string state = "install";
    /// <summary>行状态：install=未安装 / installing=安装中 / installed=已安装 / failed=失败可重试</summary>
    public string State { get => state; set { state = value; Notify(nameof(State)); Notify(nameof(ActionText)); Notify(nameof(ActionEnabled)); Notify(nameof(StatusBrush)); Notify(nameof(MetaText)); Notify(nameof(Preconditions)); } }

    /// <summary>悬停提示的使用前提说明（按安装状态）</summary>
    public string Preconditions => state switch
    {
        "installing" => "安装中：请稍候，完成后自动出现在「已安装」栏与本地列表",
        "installed" => "已安装：Agent 已可直接使用；如需移除请点「卸载」",
        "failed" => "⚠ 安装失败：" + (error.Length > 0 ? error : "未知原因") + "；请点「重试」重新安装",
        _ => "未安装：点「安装」下载落盘后自动注册，装完当轮即可调用"
    };

    string error = "";
    /// <summary>失败原因（悬停描述列可见）</summary>
    public string Error { get => error; set { error = value; Notify(nameof(Error)); } }

    string progress = "";
    /// <summary>安装进度文本（installing 状态时描述列显示；GitHub 行按阶段上报）</summary>
    public string ProgressText { get => progress; set { progress = value; Notify(nameof(ProgressText)); } }

    string installedName = "";
    /// <summary>GitHub 行实际安装的技能名（skills/ 目录名；卸载与本地列表标记用，目录册行为空）</summary>
    public string InstalledName { get => installedName; set { installedName = value; Notify(nameof(InstalledName)); } }

    public string ActionText => state switch { "installing" => "安装中…", "installed" => "卸载", "failed" => "重试", _ => "安装" };
    public bool ActionEnabled => state != "installing";

    /// <summary>行状态点颜色：已装绿 / 安装中黄 / 失败红 / 未装灰</summary>
    public System.Windows.Media.Brush StatusBrush => state switch
    {
        "installed" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)),
        "installing" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xA2, 0x3C)),
        "failed" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66)),
    };

    /// <summary>是否 GitHub 搜索结果行（source 以 github: 开头）</summary>
    public bool IsGitHub => Entry.Source.StartsWith("github:", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否 ClawHub 市场行（source 以 clawhub: 开头；Url 为官方下载端点，直接走 Market 安装链路）</summary>
    public bool IsClawHub => Entry.Source.StartsWith("clawhub:", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否 Gitee 市场行（source 以 gitee: 开头；安装走 git clone + 令牌）</summary>
    public bool IsGitee => Entry.Source.StartsWith("gitee:", StringComparison.OrdinalIgnoreCase);

    /// <summary>列表行唯一键（源+名称；同名条目可来自不同源，按钮 Tag 与状态复用都用它）</summary>
    public string EntryKey => Entry.Source + "|" + Entry.Name;

    /// <summary>版本 + 类型标记（数量已移至名称下方 StatText 行，此处只留版本/类型/来源）</summary>
    public string MetaText =>
        IsClawHub ? (Entry.Version.Length > 0 ? "v" + Entry.Version + " · " : "") + "技能 · ClawHub" :
        IsGitee ? KindText + " · 来自 Gitee" :
        IsGitHub ? KindText + " · 来自 GitHub" :
        (Entry.Version.Length > 0 ? "v" + Entry.Version + " · " : "") + KindText + " · 来自市场";

    /// <summary>数量显示行（名称下方）：GitHub/Gitee 显示星标 ★n（Version 字段即星标数）；
    /// ClawHub 显示 ★星标 + ⬇下载量；无数据返回空（卡片隐藏该行）</summary>
    public string StatText
    {
        get
        {
            if (IsGitHub || IsGitee)
                return long.TryParse(Entry.Version, out var s) && s > 0 ? "★" + s : "";
            if (IsClawHub)
            {
                var (dl, st) = ParseExtra(Entry.Extra);
                var a = st > 0 ? "★" + st : "";
                var b = dl > 0 ? FmtDownloads(dl) : "";
                return a.Length > 0 && b.Length > 0 ? a + " · " + b : a.Length > 0 ? a : b;
            }
            return "";
        }
    }

    /// <summary>数量行是否显示（空数据时折叠，避免卡片留白）</summary>
    public bool HasStat => StatText.Length > 0;

    /// <summary>解析 ClawHub Extra（新格式 下载量|星标数；兼容旧格式纯下载量数字串，星标按 0）</summary>
    static (long dl, int st) ParseExtra(string s)
    {
        var p = s.Split('|');
        var dl = long.TryParse(p[0], out var d) ? d : 0;
        var st = p.Length > 1 && int.TryParse(p[1], out var x) ? x : 0;
        return (dl, st);
    }

    /// <summary>下载量格式化：≥1万 显示 ⬇x.x万，否则 ⬇n</summary>
    static string FmtDownloads(long n) => n >= 10000 ? $"⬇{n / 10000.0:0.#}万" : $"⬇{n}";

    /// <summary>远程仓库行的仓库标识（github:/gitee: 去掉前缀；安装时传给对应安装链路）</summary>
    public string RepoPath
    {
        get
        {
            var i = Entry.Source.IndexOf(':');
            return (IsGitHub || IsGitee) && i > 0 ? Entry.Source[(i + 1)..] : "";
        }
    }

    public MarketEntryDef(CatalogEntry entry) { Entry = entry; }
}
