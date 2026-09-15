using System.IO;
using System.Text.Json;
using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 文件分类规则引擎。
///
/// 设计借鉴 DropIt 的「协议 = 筛选条件 + 动作」模型，但砍掉了「动作」这一半 ——
/// 本工具的动作永远是「记一笔」，绝不移动 / 删除 / 重命名用户的原文件，
/// 所以规则只需要回答「这个文件属于哪一类」。
///
/// 匹配语义：规则内配置的多个条件维度是「与」关系（都满足才算命中），
/// 规则之间按 Priority 升序「或」匹配，第一个命中的即为结果。
/// 这样「名字含周报的文档」这类精确规则可以排在「所有表格文件」这类兜底规则前面。
/// </summary>
public sealed class ClassifyService
{
    public const string FallbackCategory = "未分类";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // rules.json 里全是中文关键词，必须原样写出来 —— 用户要能直接编辑这个文件
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private List<ClassifyRule> _rules = new();

    public IReadOnlyList<ClassifyRule> Rules => _rules;

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(AppPaths.RulesFile))
            {
                var r = JsonSerializer.Deserialize<List<ClassifyRule>>(
                    File.ReadAllText(AppPaths.RulesFile), JsonOpts);
                if (r is { Count: > 0 })
                {
                    _rules = r;
                    return;
                }
            }
        }
        catch
        {
            // 规则文件坏了就用内置默认规则，并重新写一份
        }

        _rules = CreateDefaultRules();
        Save();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.RulesFile,
                JsonSerializer.Serialize(_rules, JsonOpts));
        }
        catch
        {
            // 存不下也不影响本次使用
        }
    }

    public void ResetToDefaults()
    {
        _rules = CreateDefaultRules();
        Save();
    }

    public ClassifyHit Classify(string fileName, string fullPath)
    {
        var name = fileName.ToLowerInvariant();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var dir = "";
        try { dir = Path.GetDirectoryName(fullPath)?.ToLowerInvariant() ?? ""; }
        catch { /* 路径异常时只按文件名判断 */ }

        foreach (var rule in _rules.Where(r => r.Enabled).OrderBy(r => r.Priority))
        {
            if (!rule.HasAnyFilter) continue;

            if (rule.HasExtensionFilter &&
                !rule.Extensions.Any(e => string.Equals(NormalizeExt(e), ext, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (rule.HasNameFilter &&
                !rule.NameKeywords.Any(k => name.Contains(k.ToLowerInvariant(), StringComparison.Ordinal)))
                continue;

            if (rule.HasPathFilter &&
                !rule.PathKeywords.Any(k => dir.Contains(k.ToLowerInvariant(), StringComparison.Ordinal)))
                continue;

            return new ClassifyHit(rule.Category, rule.Name);
        }

        return new ClassifyHit(FallbackCategory, "");
    }

    private static string NormalizeExt(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return "";
        ext = ext.Trim().ToLowerInvariant();
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    /// <summary>内置默认规则：先按业务语义的精确规则，再按文件类型的兜底规则。</summary>
    public static List<ClassifyRule> CreateDefaultRules() => new()
    {
        new ClassifyRule
        {
            Name = "汇报与总结",
            Category = "汇报材料",
            Priority = 10,
            Extensions = new() { ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls", ".pdf", ".md", ".txt" },
            NameKeywords = new() { "周报", "日报", "月报", "季报", "汇报", "总结", "复盘", "小结", "述职", "报告" },
        },
        new ClassifyRule
        {
            Name = "合同与协议",
            Category = "合同协议",
            Priority = 15,
            NameKeywords = new() { "合同", "协议", "报价", "订单", "发票", "验收", "条款" },
        },
        new ClassifyRule
        {
            Name = "会议材料",
            Category = "会议材料",
            Priority = 20,
            NameKeywords = new() { "会议", "纪要", "议程", "meeting", "评审", "讨论稿" },
        },
        new ClassifyRule
        {
            Name = "设计稿",
            Category = "设计稿",
            Priority = 25,
            Extensions = new() { ".psd", ".ai", ".fig", ".xd", ".sketch", ".axure", ".rp", ".rp9" },
        },
        new ClassifyRule
        {
            Name = "需求与方案",
            Category = "需求方案",
            Priority = 30,
            NameKeywords = new() { "需求", "方案", "设计文档", "prd", "技术方案", "计划", "排期" },
        },
        new ClassifyRule
        {
            Name = "表格数据",
            Category = "表格数据",
            Priority = 40,
            Extensions = new() { ".xlsx", ".xls", ".csv", ".tsv", ".xlsm" },
        },
        new ClassifyRule
        {
            Name = "演示文稿",
            Category = "演示文稿",
            Priority = 45,
            Extensions = new() { ".pptx", ".ppt", ".pps", ".ppsx" },
        },
        new ClassifyRule
        {
            Name = "文档",
            Category = "文档",
            Priority = 50,
            Extensions = new() { ".docx", ".doc", ".pdf", ".md", ".txt", ".rtf", ".wps", ".odt", ".epub" },
        },
        new ClassifyRule
        {
            Name = "源代码",
            Category = "代码",
            Priority = 60,
            Extensions = new()
            {
                ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".go", ".rs",
                ".c", ".cpp", ".h", ".hpp", ".m", ".swift", ".php", ".rb", ".lua", ".sql",
                ".html", ".css", ".scss", ".vue", ".sh", ".ps1", ".bat", ".cmd",
                ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".config",
            },
        },
        new ClassifyRule
        {
            Name = "图片",
            Category = "图片",
            Priority = 70,
            Extensions = new()
            {
                ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg",
                ".tif", ".tiff", ".heic", ".ico", ".raw", ".cr2", ".nef",
            },
        },
        new ClassifyRule
        {
            Name = "压缩包",
            Category = "压缩包",
            Priority = 80,
            Extensions = new() { ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso" },
        },
        new ClassifyRule
        {
            Name = "音视频",
            Category = "音视频",
            Priority = 85,
            Extensions = new()
            {
                ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a",
                ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
            },
        },
        new ClassifyRule
        {
            Name = "安装包",
            Category = "安装包",
            Priority = 90,
            Extensions = new() { ".exe", ".msi", ".msix", ".appx", ".dmg", ".pkg", ".deb", ".apk" },
        },
        new ClassifyRule
        {
            Name = "数据库与日志",
            Category = "数据与日志",
            Priority = 95,
            Extensions = new() { ".log", ".db", ".sqlite", ".sqlite3", ".bak", ".dump", ".trace" },
        },
    };
}

public readonly record struct ClassifyHit(string Category, string MatchedRule);
