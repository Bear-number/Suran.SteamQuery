using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.SteamQuery;

public class SteamQueryConfig
{
    [DisplayName("基础·评测语言")]
    [Description("评测列表语言：schinese=仅简体中文评测，留空=全部语言")]
    public string ReviewLanguage { get; set; } = "schinese";

    [DisplayName("基础·请求超时(秒)")]
    [Description("单次请求超时时间")]
    public int RequestTimeoutSeconds { get; set; } = 25;

    [DisplayName("基础·详细日志")]
    [Description("开启后输出请求与解析的调试日志")]
    public bool DetailLog { get; set; } = false;
}

[Module("Steam游戏查询",
    "查询Steam游戏信息：好评率与评测统计、最新好评差评内容、游戏详情与价格、关键词搜索、DLC反查本体。全部走Steam商店官方公开接口，无需登录和Key",
    defaultCategory: "苏染的工具")]
public class SteamQueryModule(
    XmlFunctionCaller functionCaller,
    ILogger<SteamQueryModule> logger,
    Interactor<SteamQueryModule> interactor
) : ChatBehaviour, IConfigurable<SteamQueryConfig>
{
    public SteamQueryConfig Configuration { get; set; } = null!;

    static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    const string StoreBase = "https://store.steampowered.com";

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "Steam游戏查询：好评率与评测统计、最新评测内容、游戏详情与价格、关键词搜索、DLC反查本体游戏。",
            Explanation = "用户问『XX游戏Steam好评率多少』『Steam上XX评价怎么样』『查一下Steam游戏XX』『这个Steam游戏多少钱』『搜索Steam游戏』『这个DLC属于哪个游戏』时使用。只知道游戏名时先用 SteamSearch 拿 appid；用户给的是商店链接也能直接解析出 appid。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询Steam游戏的评测统计（好评等级/好评率）与最新评测列表。appId可传纯数字appid或商店链接")]
    public async Task SteamReviews(
        [Description("appid或商店链接")] string appId,
        [Description("评测条数，默认10")] int count = 10,
        [Description("评测语言过滤，留空用插件配置（默认仅简体中文），all=全部语言")] string? language = null)
    {
        string resolvedId = ExtractAppId(appId);
        int reviewCount = Math.Clamp(count, 1, 50);
        string reviewLanguage = (language ?? Configuration.ReviewLanguage).Trim();
        if (reviewLanguage == "all")
        {
            reviewLanguage = "";
        }
        string languagePart = reviewLanguage.Length > 0 ? "language=" + reviewLanguage + "&" : "";
        string url = StoreBase + "/appreviews/" + resolvedId + "?json=1&" + languagePart
            + "filter=recent&num_per_page=" + reviewCount + "&purchase_type=all&query_summary=1&client_id=" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            string body = await FetchSteamAsync(url);
            using JsonDocument parsed = JsonDocument.Parse(body);
            JsonElement root = parsed.RootElement.Clone();
            JsonElement summary = root.TryGetProperty("query_summary", out JsonElement summaryElement)
                ? summaryElement.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
            long total = GetLongField(summary, "total_reviews");
            long positive = GetLongField(summary, "total_positive");
            long negative = GetLongField(summary, "total_negative");
            double rate = total > 0 ? positive * 100.0 / total : 0;

            var rep = new List<string>
            {
                "== " + resolvedId + " 评测统计 ==",
                "等级: " + (GetStringField(summary, "review_score_desc").Length > 0 ? GetStringField(summary, "review_score_desc") : "?")
                    + " | 好评 " + positive + " / 差评 " + negative + " / 共 " + total
                    + " | 好评率 " + rate.ToString("F1", CultureInfo.InvariantCulture) + "%",
                "== 最新评测（新→旧） =="
            };
            if (root.TryGetProperty("reviews", out JsonElement reviews) && reviews.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement review in reviews.EnumerateArray())
                {
                    long timestamp = GetLongField(review, "timestamp_created");
                    string date = timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : "?";
                    string tag = GetBoolField(review, "voted_up") ? "👍好评" : "👎差评";
                    string author = "";
                    if (review.TryGetProperty("author", out JsonElement authorElement) && authorElement.ValueKind == JsonValueKind.Object)
                    {
                        author = GetStringField(authorElement, "personaname");
                    }
                    string content = GetStringField(review, "review").Trim();
                    rep.Add("[" + tag + " " + date + "] " + author + ": " + (content.Length > 600 ? content[..600] : content));
                }
            }
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 评测查询失败：" + queryError.Message + "（appid " + resolvedId + " 是否正确？可先用 SteamSearch 搜索确认）");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询Steam游戏详情：名称、发行日期、价格、简介、商店链接。appIds支持逗号分隔批量查询多个appid或链接")]
    public async Task SteamAppInfo(
        [Description("appid或商店链接，多个用英文逗号分隔")] string appIds)
    {
        List<string> ids = appIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ExtractAppId)
            .Where(id => id.Length > 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            interactor.Poke("❌ 未提供有效的 appid（支持纯数字或商店链接）");
            return;
        }
        string url = StoreBase + "/api/appdetails?appids=" + string.Join(",", ids) + "&l=schinese&cc=CN";
        try
        {
            string body = await FetchSteamAsync(url);
            using JsonDocument parsed = JsonDocument.Parse(body);
            JsonElement root = parsed.RootElement.Clone();
            var rep = new List<string>();
            foreach (string id in ids)
            {
                if (root.TryGetProperty(id, out JsonElement entry) == false
                    || entry.ValueKind != JsonValueKind.Object
                    || GetBoolField(entry, "success") == false
                    || entry.TryGetProperty("data", out JsonElement data) == false
                    || data.ValueKind != JsonValueKind.Object)
                {
                    rep.Add(id + ": 未找到");
                    continue;
                }
                string price = "免费/未知";
                if (data.TryGetProperty("price_overview", out JsonElement priceElement) && priceElement.ValueKind == JsonValueKind.Object)
                {
                    price = GetStringField(priceElement, "final_formatted");
                }
                rep.Add("== " + GetStringField(data, "name") + " (" + id + ") ==");
                rep.Add("发行: " + GetReleaseDate(data) + " | 价格: " + price);
                string description = Regex.Replace(GetStringField(data, "short_description"), "<[^>]+>", "").Trim();
                rep.Add("简介: " + (description.Length > 300 ? description[..300] : description));
                rep.Add("链接: https://store.steampowered.com/app/" + id);
            }
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 详情查询失败：" + queryError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜索Steam游戏（拿appid），返回前10条：appid | 名称 | 价格 | 链接。搜索无结果时可尝试中英文名分别搜")]
    public async Task SteamSearch(
        [Description("游戏名称关键词")] string keyword)
    {
        string trimmed = (keyword ?? "").Trim();
        if (trimmed.Length == 0)
        {
            interactor.Poke("❌ 搜索关键词不能为空");
            return;
        }
        string url = StoreBase + "/api/storesearch/?term=" + Uri.EscapeDataString(trimmed) + "&l=schinese&cc=CN";
        try
        {
            string body = await FetchSteamAsync(url);
            using JsonDocument parsed = JsonDocument.Parse(body);
            JsonElement root = parsed.RootElement.Clone();
            if (root.TryGetProperty("items", out JsonElement items) == false || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                interactor.Poke("🔍 无结果。可尝试用中英文名分别搜，或让用户直接给商店链接/appid");
                return;
            }
            var rep = new List<string>();
            foreach (JsonElement item in items.EnumerateArray().Take(10))
            {
                string price = "免费/未知";
                if (item.TryGetProperty("price", out JsonElement priceElement) && priceElement.ValueKind == JsonValueKind.Object)
                {
                    price = GetStringField(priceElement, "final_formatted");
                }
                rep.Add(GetStringField(item, "id") + " | " + GetStringField(item, "name") + " | " + price
                    + " | https://store.steampowered.com/app/" + GetStringField(item, "id"));
            }
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 搜索失败：" + queryError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("判断appid是DLC还是本体：是DLC则反查所属本体游戏（需访问商店网页解析）。DLC页面显示『需要基础游戏』时好评率应查本体")]
    public async Task SteamBaseGame(
        [Description("appid或商店链接")] string appId)
    {
        string resolvedId = ExtractAppId(appId);
        string url = StoreBase + "/app/" + resolvedId + "/";
        try
        {
            string html = await FetchSteamAsync(url);
            (string Mark, string Multi)[] markers = new[]
            {
                ("requires the base game", "requires one of the following products"),
                ("需要在 Steam 上拥有基础游戏", "需要以下产品之一")
            };
            foreach ((string singleMarker, string multiMarker) in markers)
            {
                int markerIndex = html.IndexOf(singleMarker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    string window = html.Substring(markerIndex, Math.Min(600, html.Length - markerIndex));
                    Match baseMatch = Regex.Match(window, @"https://store\.steampowered\.com/app/(\d+)/");
                    if (baseMatch.Success)
                    {
                        interactor.Poke("DLC " + resolvedId + " 属于本体: " + baseMatch.Value + " (appid " + baseMatch.Groups[1].Value + ")\n"
                            + "https://store.steampowered.com/app/" + baseMatch.Groups[1].Value
                            + "\n提示：好评率请用 SteamReviews 查本体 appid " + baseMatch.Groups[1].Value);
                        return;
                    }
                    interactor.Poke(resolvedId + " 是DLC，但页面未提取到本体appid，请人工确认");
                    return;
                }
                if (html.IndexOf(multiMarker, StringComparison.Ordinal) >= 0)
                {
                    interactor.Poke(resolvedId + " 是DLC且依赖多本体，请人工从商店页确认");
                    return;
                }
            }
            interactor.Poke(resolvedId + " 看起来是本体游戏（商店页无 base game 依赖），好评率直接用 SteamReviews 查询");
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 本体反查失败：" + queryError.Message);
        }
    }

    // ============================================================
    // 辅助
    // ============================================================

    // 从输入提取纯数字appid：兼容纯数字、商店链接、含杂质的字符串
    static string ExtractAppId(string input)
    {
        string trimmed = (input ?? "").Trim();
        if (Regex.IsMatch(trimmed, "^[0-9]+$"))
        {
            return trimmed;
        }
        Match match = Regex.Match(trimmed, @"store\.steampowered\.com/app/([0-9]+)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        Match anyDigits = Regex.Match(trimmed, "[0-9]{3,}");
        return anyDigits.Success ? anyDigits.Value : "";
    }

    // 带一次重试的Steam请求：接口偶发空返回或超时按原技能要求重试一次
    async Task<string> FetchSteamAsync(string url)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Clamp(Configuration.RequestTimeoutSeconds, 5, 120)));
                using HttpResponseMessage response = await httpClient.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (body.Trim().Length > 0)
                {
                    return body;
                }
                LogDetail("Steam返回空内容(尝试 " + (attempt + 1) + "/2)");
            }
            catch (Exception fetchError)
            {
                LogDetail("Steam请求失败(尝试 " + (attempt + 1) + "/2)：" + fetchError.Message);
                if (attempt == 1)
                {
                    throw new Exception("Steam接口不可达（已重试一次）：" + fetchError.Message);
                }
            }
        }
        throw new Exception("Steam接口返回空内容（已重试一次）");
    }

    static string GetReleaseDate(JsonElement data)
    {
        if (data.TryGetProperty("release_date", out JsonElement releaseDate) && releaseDate.ValueKind == JsonValueKind.Object)
        {
            return GetStringField(releaseDate, "date").Length > 0 ? GetStringField(releaseDate, "date") : "?";
        }
        return "?";
    }

    void LogDetail(string message)
    {
        if (Configuration.DetailLog)
        {
            logger.LogInformation("[Steam] 🔍 {Message}", message);
        }
    }

    static bool GetBoolField(JsonElement element, string fieldName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(fieldName, out JsonElement field)
            && field.ValueKind == JsonValueKind.True;
    }

    static long GetLongField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return 0;
        }
        return field.ValueKind switch
        {
            JsonValueKind.Number => field.GetInt64(),
            JsonValueKind.String => long.TryParse(field.GetString(), out long parsed) ? parsed : 0,
            _ => 0
        };
    }

    static string GetStringField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return "";
        }
        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString() ?? "",
            JsonValueKind.Number => field.GetRawText(),
            _ => ""
        };
    }
}
