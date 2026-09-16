using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAIRR.AgentHost;

/// <summary>Flow 模板：命名步骤序列（数据文件 &lt;工具目录&gt;/flows/&lt;name&gt;.json，增删改模板无需重编译）。</summary>
public class FlowTemplate
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>模板显示名（UI 模式选择器下拉项文本）。</summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";

    /// <summary>模板用途说明（UI 模式选择器提示行显示）。</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>下拉前缀图标字符（emoji，与固定项 ⚡/🔬 对齐）；缺省空=📋 通用流程，只读定位类模板可设 🔍。</summary>
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";

    /// <summary>步骤序列（search|map|edit|build|commit|ask）。</summary>
    [JsonPropertyName("steps")] public List<FlowStep> Steps { get; set; } = new();
}

/// <summary>Flow 模板存取：工具级 &lt;exe目录&gt;/flows/*.json；目录为空时自动落两个默认模板。</summary>
public static class FlowTemplateStore
{
    /// <summary>模板根目录：&lt;工具目录&gt;/flows/</summary>
    public static string Dir() => Path.Combine(AppContext.BaseDirectory, "flows");

    /// <summary>加载全部模板（损坏文件跳过并记日志）；目录不存在/为空时自动写默认模板。</summary>
    public static List<FlowTemplate> LoadAll()
    {
        var dir = Dir();
        var list = new List<FlowTemplate>();
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var t = JsonSerializer.Deserialize<FlowTemplate>(File.ReadAllText(f));
                    if (t != null && !string.IsNullOrWhiteSpace(t.Name) && t.Steps.Count > 0)
                    {
                        if (string.IsNullOrWhiteSpace(t.DisplayName)) t.DisplayName = DefaultDisplay(t.Name);
                        list.Add(t);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Flow] 模板加载失败 {f}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Flow] 模板目录读取失败: {ex.Message}"); }
        if (list.Count == 0) SeedDefaults(dir);
        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>按名查找模板（忽略大小写）；名称为空/不存在返回 null。
    /// packageDir 非空时先查角色包目录 <paramref name="packageDir"/>/flows/&lt;name&gt;.json（角色包内私有流程，整体复制=整体分发），
    /// 未命中再回退工具级共享池 <exe目录>/flows/&lt;name&gt;.json。</summary>
    public static FlowTemplate? Find(string name, string? packageDir = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // 角色包优先：包内私有流程模板（新形态 roles/<角色>/flows/*.json）
        if (!string.IsNullOrWhiteSpace(packageDir))
        {
            var pkgPath = Path.Combine(packageDir, "flows", name + ".json");
            if (File.Exists(pkgPath))
            {
                var t = LoadFile(pkgPath);
                if (t != null) return t;
            }
        }
        // 回退共享池
        return LoadAll().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从单个 json 文件加载模板（含字段归一化）；损坏/缺关键字段返回 null。</summary>
    static FlowTemplate? LoadFile(string path)
    {
        try
        {
            var t = JsonSerializer.Deserialize<FlowTemplate>(File.ReadAllText(path));
            if (t != null && !string.IsNullOrWhiteSpace(t.Name) && t.Steps.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(t.DisplayName)) t.DisplayName = DefaultDisplay(t.Name);
                return t;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Flow] 模板加载失败 {path}: {ex.Message}"); }
        return null;
    }

    /// <summary>内置中文显示名（旧模板文件缺 displayName 字段时回退用）。</summary>
    static string DefaultDisplay(string name) => name switch
    {
        "dev-basic" => "流程模式",
        _ => name,
    };

    /// <summary>首次使用时落默认模板（dev-basic），已存在则跳过。</summary>
    static void SeedDefaults(string dir)
    {
        var defaults = new[]
        {
            new FlowTemplate
            {
                Name = "dev-basic",
                DisplayName = "流程模式",
                Description = "按定位→修改→编译验证→收尾汇报的完整流程执行，可修改文件",
                Steps = new List<FlowStep>
                {
                    new() { Step = "search", Message = "定位与任务相关的代码（类/方法/文件），给出要改的位置" },
                    new() { Step = "edit", Message = "按任务目标实现修改（改前 Read 确认现状）" },
                    new() { Step = "build", Query = "dotnet build -v q", Message = "编译验证整个解决方案" },
                    new() { Step = "ask", Message = "总结修改内容与遗留问题" },
                },
            },
        };
        foreach (var t in defaults)
        {
            var path = Path.Combine(dir, t.Name + ".json");
            if (File.Exists(path)) continue;
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));   // flow 模板中文直存
                System.Diagnostics.Debug.WriteLine($"[Flow] 默认模板已写入 {path}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Flow] 默认模板写入失败 {path}: {ex.Message}"); }
        }
    }
}
