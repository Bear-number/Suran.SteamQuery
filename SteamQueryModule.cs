using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
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
    [Description("单次请求超时时间。实测商店详情接口两三秒就回，好友列表那种大网页经代理要更久；填低于10秒容易偶发失败")]
    public int RequestTimeoutSeconds { get; set; } = 25;

    [DisplayName("基础·详细日志")]
    [Description("开启后输出请求与解析的调试日志")]
    public bool DetailLog { get; set; } = false;

    [DisplayName("网络·代理地址")]
    [Description("HTTP代理，如 http://127.0.0.1:7890。实测 store.steampowered.com 与 api.steampowered.com 国内直连可用，但 steamcommunity.com 直连12秒超时——所以市场价格、个人资料、好友列表必须填代理才能用，其余功能不填也行")]
    public string ProxyUrl { get; set; } = "";

    [DisplayName("网络·缓存时长(秒)")]
    [Description("同一个网址在这个时长内重复查直接用缓存，0=每次真查。查磨损档这种一次发六条的能省不少")]
    public int CacheTtlSeconds { get; set; } = 300;

    [DisplayName("凭据·Steam Web API Key")]
    [Description("在 https://steamcommunity.com/dev/apikey 免费申请。只有『已购游戏列表』非要它不可：实测不带key时 GetOwnedGames 回401 Unauthorized，库存网页 /games/?tab=all 被302到登录页，没有免登录路子。资料卡与好友列表不用key")]
    public string ApiKey { get; set; } = "";

    [DisplayName("凭据·默认SteamID64")]
    [Description("17位数字。填了以后 SteamProfile / SteamFriends / SteamOwnedGames 不传参数就用它")]
    public string SteamId { get; set; } = "";

    [DisplayName("市场·货币代码")]
    [Description("Steam市场接口的currency参数：23=人民币(回显如 ¥ 1,457.18)，1=美元($471.39)，3=欧元")]
    public int Currency { get; set; } = 23;
}

[Module("Steam游戏查询",
    "查询Steam：好评率与评测统计、游戏详情与价格、关键词搜索、DLC反查本体、社区市场物品现价与五档磨损比价、个人资料卡、好友列表、已购游戏库存、连通性自检。商店与市场接口全公开，资料卡和好友也免登录免key，只有已购游戏库存需要一把免费申请的Steam Web API Key",
    defaultCategory: "苏染的工具")]
public class SteamQueryModule(
    XmlFunctionCaller functionCaller,
    ILogger<SteamQueryModule> logger,
    Interactor<SteamQueryModule> interactor
) : ChatBehaviour, IConfigurable<SteamQueryConfig>
{
    public SteamQueryConfig Configuration { get; set; } = null!;

    const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    const string StoreBase = "https://store.steampowered.com";
    const string ApiBase = "https://api.steampowered.com";
    const string CommunityBase = "https://steamcommunity.com";
    const int ProbeTimeoutSeconds = 8;
    const int CacheCap = 128;

    static readonly Dictionary<string, string> AppIdAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csgo"] = "730", ["cs"] = "730", ["cs2"] = "730", ["counterstrike"] = "730",
        ["dota2"] = "570", ["dota"] = "570",
        ["tf2"] = "440", ["teamfortress"] = "440",
        ["pubg"] = "578080",
        ["rust"] = "252490",
        ["gta5"] = "271590", ["gta"] = "271590"
    };

    static readonly string[] WearLevels = ["Factory New", "Minimal Wear", "Field-Tested", "Well-Worn", "Battle-Scarred"];
    static readonly Dictionary<string, string> WearChinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Factory New"] = "崭新", ["Minimal Wear"] = "略磨损", ["Field-Tested"] = "久经沙场",
        ["Well-Worn"] = "破损", ["Battle-Scarred"] = "战痕累累"
    };

    readonly Dictionary<string, (DateTime ExpireAt, string Body)> responseCache = new();

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "Steam查询：好评率与评测、游戏详情与价格、关键词搜索、DLC反查本体、社区市场现价与磨损比价、个人资料卡、好友列表、已购游戏库存、连通性自检。",
            Explanation = "用户问『XX游戏Steam好评率』『Steam上XX多少钱』『查Steam游戏』『这个DLC属于哪个游戏』时用前四个函数。问市场挂单价格（皮肤、饰品、卡片）用 SteamMarketPrice，物品要给市场全名，磨损档不确定就 allWears=true 一次比完。问『我的Steam资料/好友/买了哪些游戏』用 SteamProfile / SteamFriends / SteamOwnedGames，参数留空就用配置里的默认SteamID；前两个免key，库存那个必须配 API Key。凡是走 steamcommunity.com 的（市场价、资料、好友）都要先在配置里填代理，报『没填代理』就用 SteamEnv 自检。只知道游戏名时先用 SteamSearch 拿 appid；用户给商店链接或Steam主页链接也能直接解析。"
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
        string resolvedId = ResolveAppId(appId);
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

            List<string> rep = new()
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
    [Description("查询Steam游戏详情：名称、发行日期、价格、类型、是否支持简体中文、DLC数量、简介、商店链接。appIds支持逗号分隔批量查询多个appid或链接")]
    public async Task SteamAppInfo(
        [Description("appid或商店链接，多个用英文逗号分隔")] string appIds)
    {
        List<string> ids = appIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ResolveAppId)
            .Where(id => id.Length > 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            interactor.Poke("❌ 未提供有效的 appid（支持纯数字、商店链接，或 csgo/dota2/tf2/pubg/rust/gta5 这类缩写）");
            return;
        }
        string url = StoreBase + "/api/appdetails?appids=" + string.Join(",", ids) + "&l=schinese&cc=CN";
        try
        {
            string body = await FetchSteamAsync(url);
            using JsonDocument parsed = JsonDocument.Parse(body);
            JsonElement root = parsed.RootElement.Clone();
            List<string> rep = new();
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
                rep.Add("发行: " + GetReleaseDate(data) + " | 价格: " + price
                    + " | 类型: " + JoinDescriptions(data, "genres", 4)
                    + " | 简体中文: " + (GetStringField(data, "supported_languages").Contains("简体中文") ? "支持" : "未见标注"));
                string studios = JoinArrayAsText(data, "developers") + " / " + JoinArrayAsText(data, "publishers");
                if (studios.Trim(' ', '/').Length > 0)
                {
                    rep.Add("开发/发行: " + Shorten(studios, 160) + " | DLC: " + CountArray(data, "dlc") + " 个");
                }
                string description = Regex.Replace(GetStringField(data, "short_description"), "<[^>]+>", "").Trim();
                rep.Add("简介: " + Shorten(description, 300));
                rep.Add("链接: " + StoreBase + "/app/" + id);
            }
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 详情查询失败：" + queryError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜索Steam商店游戏（拿appid），返回：appid | 名称 | 价格 | 链接。搜不到时可换中英文名分别试")]
    public async Task SteamSearch(
        [Description("游戏名称关键词")] string keyword,
        [Description("返回条数，默认10，最多20")] int count = 10)
    {
        string trimmed = (keyword ?? "").Trim();
        if (trimmed.Length == 0)
        {
            interactor.Poke("❌ 搜索关键词不能为空");
            return;
        }
        string abbreviation = LookupAbbreviation(trimmed);
        if (abbreviation.Length > 0)
        {
            interactor.Poke("缩写 " + trimmed + " 对应 appid " + abbreviation + "，直接用：\n"
                + StoreBase + "/app/" + abbreviation + "\n详情用 SteamAppInfo，好评率用 SteamReviews，市场价用 SteamMarketPrice（appId 传 " + abbreviation + "）");
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
            List<string> rep = new();
            foreach (JsonElement item in items.EnumerateArray().Take(Math.Clamp(count, 1, 20)))
            {
                string price = "免费/未知";
                if (item.TryGetProperty("price", out JsonElement priceElement) && priceElement.ValueKind == JsonValueKind.Object)
                {
                    price = GetStringField(priceElement, "final_formatted");
                }
                rep.Add(GetStringField(item, "id") + " | " + GetStringField(item, "name") + " | " + price
                    + " | " + StoreBase + "/app/" + GetStringField(item, "id"));
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
        string resolvedId = ResolveAppId(appId);
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
                            + StoreBase + "/app/" + baseMatch.Groups[1].Value
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

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查Steam社区市场物品现价：最低挂单价、中位价、近期成交量。appId可传纯数字、商店链接或缩写(csgo/dota2/tf2/pubg/rust/gta5)。要挂单全名，如 'AK-47 | Redline (Field-Tested)'；不确定磨损档就把 allWears=true，插件把五档挨个查一遍再按价格排")]
    public async Task SteamMarketPrice(
        [Description("物品市场全名；allWears=true 时传不含磨损的基础名，如 'M4A1-S | Printstream'")] string item,
        [Description("游戏 appid / 商店链接 / 缩写，如 730、csgo、dota2")] string appId,
        [Description("true=五个磨损档全查并汇总，默认false只按 item 原样查")] bool allWears = false)
    {
        string trimmed = (item ?? "").Trim();
        if (trimmed.Length == 0)
        {
            interactor.Poke("❌ 物品市场名不能为空（CS2/Dota2/TF2 皮肤形如『武器名 | 皮肤名 (磨损)』，不带磨损后缀时先 allWears=true 比一遍）");
            return;
        }
        string resolvedId = ResolveAppId(appId);
        if (resolvedId.Length == 0)
        {
            interactor.Poke("❌ 市场接口非要 appid 不可。传 730、570、440、578080、252490、271590 这些数字，或直接传 csgo/dota2/tf2/pubg/rust/gta5；不知道 appid 就先 SteamSearch");
            return;
        }
        try
        {
            if (allWears == false)
            {
                interactor.Poke("== " + trimmed + " (appid " + resolvedId + ") ==\n" + await DescribeMarketLine(trimmed, resolvedId));
                return;
            }
            List<string> lines = new();
            List<(double Price, string Name, string Text)> priced = new();
            foreach (string wear in WearLevels)
            {
                string fullName = trimmed + " (" + wear + ")";
                string text = await DescribeMarketLine(fullName, resolvedId);
                double price = ParseAmount(text);
                if (price > 0)
                {
                    priced.Add((price, wear, text));
                }
                else
                {
                    lines.Add(WearChinese[wear] + " (" + wear + ")：" + text);
                }
            }
            lines.InsertRange(0, priced.OrderBy(entry => entry.Price).Select(entry => WearChinese[entry.Name] + " (" + entry.Name + ")：" + entry.Text));
            interactor.Poke("== " + trimmed + " 五档磨损 (appid " + resolvedId + "，货币代码 " + Configuration.Currency + ") ==\n"
                + string.Join("\n", lines)
                + "\n说明：实测这个接口只会回『最低挂单/中位价/成交量』三项，而且某项没有就整个字段不出现（显示为?）；只回 {\"success\":true} 表示该档当前无挂单。");
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 市场价查询失败：" + queryError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查Steam个人资料：SteamID64、昵称、自定义URL、在线状态、注册时间、所在地、真名、VAC与交易封禁、受限账号、头像、个性签名。参数可传SteamID64/自定义名/主页链接，留空用配置里的默认SteamID。免登录免key（实测 ?xml=1 公开可取）")]
    public async Task SteamProfile(
        [Description("SteamID64 / 自定义URL名 / steamcommunity 主页链接，留空用配置默认")] string steamId = "")
    {
        try
        {
            string xml = await FetchProfileXmlAsync(steamId);
            string error = XmlValue(xml, "error");
            if (error.Length > 0)
            {
                interactor.Poke("❌ 资料页取不到（Steam 原话：" + Shorten(error, 120) + "）。自定义名拼错、这个号不存在、或对方把主页设成私密都会这样");
                return;
            }
            List<string> rep = new()
            {
                "== " + XmlValue(xml, "steamID") + " (" + XmlValue(xml, "steamID64") + ") ==",
                "状态: " + XmlValue(xml, "onlineState") + " / " + XmlValue(xml, "stateMessage")
                    + " | 可见性: " + XmlValue(xml, "privacyState") + "(visibilityState " + XmlValue(xml, "visibilityState") + ")",
                "自定义URL: " + XmlValue(xml, "customURL") + " | 注册: " + XmlValue(xml, "memberSince")
                    + " | 所在地: " + XmlValue(xml, "location"),
                "封禁: VAC=" + BanText(XmlValue(xml, "vacBanned")) + " 交易=" + XmlValue(xml, "tradeBanState")
                    + " 受限账号=" + BanText(XmlValue(xml, "isLimitedAccount")),
                "两周游玩: " + XmlValue(xml, "hoursPlayed2Wk") + " 小时 | 头像: " + XmlValue(xml, "avatarFull")
            };
            string headline = XmlValue(xml, "headline");
            if (headline.Length > 0)
            {
                rep.Add("标题: " + headline);
            }
            string realName = XmlValue(xml, "realname");
            if (realName.Length > 0)
            {
                rep.Add("真名: " + realName);
            }
            string summary = XmlValue(xml, "summary");
            if (summary.Length > 0)
            {
                rep.Add("签名: " + Shorten(summary, 200));
            }
            string id64 = XmlValue(xml, "steamID64");
            rep.Add("好友列表用 SteamFriends(\"" + id64 + "\")，已购库存用 SteamOwnedGames(\"" + id64 + "\")（后者要 API Key）");
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 资料查询失败：" + queryError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列Steam好友（昵称+SteamID64）。参数同 SteamProfile，可传ID64/自定义名/链接，留空用配置默认。免登录免key：实测好友主页HTML公开可抓；注意库存页就没这待遇，会被302到登录页")]
    public async Task SteamFriends(
        [Description("SteamID64 / 自定义URL名 / 主页链接，留空用配置默认")] string steamId = "",
        [Description("最多列几条，默认30，最多100")] int limit = 30)
    {
        try
        {
            string id64 = await ResolveSteamIdAsync(steamId);
            string html = await FetchSteamAsync(CommunityBase + "/profiles/" + id64 + "/friends/");
            List<(string Id, string Name)> friends = ParseFriendBlocks(html);
            if (friends.Count == 0)
            {
                interactor.Poke("❌ " + id64 + " 的好友页里一条都没抓到——多半是对方把『好友列表』设成了私密，或这个号确实没好友");
                return;
            }
            int wanted = Math.Clamp(limit, 1, 100);
            List<string> rep = new()
            {
                "== " + id64 + " 的好友：抓到 " + friends.Count + " 条，列前 " + Math.Min(wanted, friends.Count) + " 条 =="
            };
            foreach ((string friendId, string name) in friends.Take(wanted))
            {
                rep.Add(name + " | " + friendId);
            }
            if (friends.Count > wanted)
            {
                rep.Add("（还有 " + (friends.Count - wanted) + " 条没列，把 limit 调大）");
            }
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 好友列表查询失败：" + queryError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列指定用户已购游戏（含总游玩时长与最近游玩时间），按时长从多到少排。这项非要配置『凭据·Steam Web API Key』不可：实测不带key接口回401、库存网页被302到登录页，没有免登录办法。参数可传ID64/自定义名/链接，留空用配置默认")]
    public async Task SteamOwnedGames(
        [Description("SteamID64 / 自定义URL名 / 主页链接，留空用配置默认")] string steamId = "",
        [Description("只看这几款：appid逗号分隔，留空列全部")] string appIds = "",
        [Description("最多列几条，默认30，最多200")] int limit = 30)
    {
        string key = Configuration.ApiKey.Trim();
        if (key.Length == 0)
        {
            interactor.Poke("❌ 已购游戏列表这项要 Steam Web API Key：在 https://steamcommunity.com/dev/apikey 免费申请一把填进配置「凭据·Steam Web API Key」。实测没有绕开的路——接口不带key回401 Unauthorized，库存网页 /games/?tab=all 直接被302到登录页。资料卡和好友列表不用key，要查那些直接用 SteamProfile / SteamFriends");
            return;
        }
        try
        {
            string id64 = await ResolveSteamIdAsync(steamId);
            string filter = "";
            List<string> wantedApps = appIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ResolveAppId)
                .Where(id => id.Length > 0)
                .ToList();
            if (wantedApps.Count > 0)
            {
                filter = "&appid_filter=" + string.Join(",", wantedApps);
            }
            string url = ApiBase + "/IPlayerService/GetOwnedGames/v1/?key=" + Uri.EscapeDataString(key)
                + "&steamid=" + id64 + "&include_played_free_games=1&include_appinfo=1&format=json" + filter;
            string body = await FetchSteamAsync(url);
            using JsonDocument parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.TryGetProperty("response", out JsonElement response) == false
                || response.ValueKind != JsonValueKind.Object
                || response.TryGetProperty("games", out JsonElement games) == false
                || games.ValueKind != JsonValueKind.Array)
            {
                interactor.Poke("❌ 接口没回 response.games。key 无效、或对方把『游戏详情』设成私密都会这样（实测私密时 games 直接为空）");
                return;
            }
            List<(string Name, long Id, double Hours, long LastPlayed)> list = games.EnumerateArray()
                .Select(game => (
                    Name: GetStringField(game, "name").Length > 0 ? GetStringField(game, "name") : "appid " + GetStringField(game, "appid"),
                    Id: GetLongField(game, "appid"),
                    Hours: GetDoubleField(game, "playtime_forever"),
                    LastPlayed: GetLongField(game, "rtime_last_played")))
                .OrderByDescending(entry => entry.Hours)
                .ToList();
            if (list.Count == 0)
            {
                interactor.Poke(wantedApps.Count > 0
                    ? "🎮 " + id64 + " 没在你指定的这几个 appid 里有游戏"
                    : "🎮 " + id64 + " 的已购列表是空的——把『游戏详情』设为私密的人，接口只会回空列表，这不是查询失败");
                return;
            }
            int wanted2 = Math.Clamp(limit, 1, 200);
            List<string> rep = new()
            {
                "== " + id64 + " 已购 " + GetLongField(response, "game_count") + " 款（按总时长排，列前 " + Math.Min(wanted2, list.Count) + " 款）=="
            };
            foreach ((string name, long id, double hours, long lastPlayed) in list.Take(wanted2))
            {
                rep.Add(name + " | appid " + id + " | " + hours.ToString("F1", CultureInfo.InvariantCulture) + " 小时"
                    + (lastPlayed > 0 ? " | 最近 " + DateTimeOffset.FromUnixTimeSeconds(lastPlayed).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""));
            }
            if (list.Count > wanted2)
            {
                rep.Add("（还有 " + (list.Count - wanted2) + " 款没列，把 limit 调大）");
            }
            interactor.Poke(string.Join("\n", rep));
        }
        catch (Exception queryError)
        {
            interactor.Poke("❌ 已购游戏查询失败：" + queryError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("自检Steam环境：store/api/steamcommunity 三个域名直连与经代理分别通不通、代理/默认SteamID/API Key 配得对不对、key 实调一次能不能用、缓存里攒了几条。哪样查不动时先跑这个")]
    public async Task SteamEnv()
    {
        string proxy = Configuration.ProxyUrl.Trim();
        List<string> rep = new() { "== Steam 自检 ==" };
        rep.Add("代理地址: " + (proxy.Length > 0 ? proxy : "没填（商店类接口实测能直连，市场价/资料/好友这几项实测直连超时，必须填）"));
        rep.Add("市场货币代码: " + Configuration.Currency + " | 缓存时长: " + Configuration.CacheTtlSeconds + " 秒（当前攒了 " + responseCache.Count + " 条）");
        rep.Add("store 商店接口 直连: " + await ProbeAsync(StoreBase + "/api/appdetails?appids=730&cc=CN", ""));
        if (proxy.Length > 0)
        {
            rep.Add("store 商店接口 经代理: " + await ProbeAsync(StoreBase + "/api/appdetails?appids=730&cc=CN", proxy));
        }
        rep.Add("api 开放接口 直连: " + await ProbeAsync(ApiBase + "/ISteamUser/GetPlayerBans/v1/?steamids=76561197960435530", "")
            + "（不带key回400=域名是通的）");
        if (proxy.Length > 0)
        {
            rep.Add("community 资料接口 直连: " + await ProbeAsync(CommunityBase + "/profiles/76561197960435530/?xml=1", ""));
            rep.Add("community 资料接口 经代理: " + await ProbeAsync(CommunityBase + "/profiles/76561197960435530/?xml=1", proxy));
            rep.Add("market 市场接口 经代理: " + await ProbeAsync(CommunityBase + "/market/priceoverview?appid=730&currency=23&market_hash_name=x", proxy));
        }
        string defaultId = Configuration.SteamId.Trim();
        if (defaultId.Length == 0)
        {
            rep.Add("默认SteamID: 没填（SteamProfile/SteamFriends/SteamOwnedGames 不传参数时会让你给ID）");
        }
        else
        {
            rep.Add("默认SteamID: " + defaultId + " → 解析结果: " + await SafeResolveAsync(defaultId));
        }
        string key = Configuration.ApiKey.Trim();
        if (key.Length == 0)
        {
            rep.Add("API Key: 没填（只有已购库存 SteamOwnedGames 要它，资料卡与好友列表不要）");
        }
        else
        {
            string checkUrl = ApiBase + "/ISteamUser/GetPlayerBans/v1?key=" + Uri.EscapeDataString(key) + "&steamids=76561197960435530";
            rep.Add("API Key: 已填 " + key[..Math.Min(4, key.Length)] + "…，实调: " + await ProbeAsync(checkUrl, ""));
        }
        interactor.Poke(string.Join("\n", rep));
    }

    // ============================================================
    // 辅助
    // ============================================================

    async Task<string> DescribeMarketLine(string marketName, string appId)
    {
        string url = CommunityBase + "/market/priceoverview?appid=" + appId
            + "&currency=" + Math.Clamp(Configuration.Currency, 1, 35)
            + "&market_hash_name=" + Uri.EscapeDataString(marketName);
        string body = await FetchSteamAsync(url);
        using JsonDocument parsed = JsonDocument.Parse(body);
        JsonElement root = parsed.RootElement.Clone();
        if (GetBoolField(root, "success") == false)
        {
            return "接口回 success=false：appid 与市场名对不上，或这个 app 根本没有社区市场";
        }
        string lowest = GetStringField(root, "lowest_price");
        if (lowest.Length == 0)
        {
            return "当前无挂单（实测 appid 对但物品名拼错、或该物品没人挂，都只回 {\"success\":true}）";
        }
        string median = GetStringField(root, "median_price");
        string volume = GetStringField(root, "volume");
        return "最低挂单 " + lowest + " | 中位价 " + (median.Length > 0 ? median : "?")
            + " | 成交量 " + (volume.Length > 0 ? volume : "?");
    }

    // 实测同一把枪五档磨损要发六条请求，靠缓存与串行请求，不做并发（社区市场429限流）
    static double ParseAmount(string text)
    {
        Match match = Regex.Match(text, @"[\d.,]+");
        if (match.Success == false)
        {
            return 0;
        }
        string digits = match.Value.Replace(",", "");
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
    }

    static List<(string Id, string Name)> ParseFriendBlocks(string html)
    {
        List<(string Id, string Name)> friends = new();
        HashSet<string> seen = new();
        foreach (string block in html.Split("friend_block_v2").Skip(1))
        {
            Match idMatch = Regex.Match(block, "data-steamid=\"(\\d+)\"");
            if (idMatch.Success == false || seen.Add(idMatch.Groups[1].Value) == false)
            {
                continue;
            }
            Match nameMatch = Regex.Match(block, "friend_block_content\"[^>]*>\\s*(?:<[^>]+>\\s*)*([^<\r\n]+)");
            string name = WebUtility.HtmlDecode(nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "");
            friends.Add((idMatch.Groups[1].Value, name.Length > 0 ? name : "（无昵称）"));
        }
        return friends;
    }

    async Task<string> ResolveSteamIdAsync(string input)
    {
        string target = (input ?? "").Trim();
        if (target.Length == 0)
        {
            target = Configuration.SteamId.Trim();
        }
        if (target.Length == 0)
        {
            throw new Exception("没给 SteamID：把17位ID64、自定义URL名或主页链接传进参数，或在配置「凭据·默认SteamID64」里填一个");
        }
        string xml = await FetchProfileXmlAsync(target);
        string id64 = XmlValue(xml, "steamID64");
        if (id64.Length == 0)
        {
            throw new Exception("解析不出 SteamID64（Steam 原话：" + Shorten(XmlValue(xml, "error"), 120)
                + "）——自定义名拼错、号不存在、主页设成私密都会这样");
        }
        return id64;
    }

    // /id/{自定义名} 与 /profiles/{ID64} 两种写法都认；?xml=1 实测免登录免key可取
    async Task<string> FetchProfileXmlAsync(string input)
    {
        string text = (input ?? "").Trim();
        Match linkMatch = Regex.Match(text, @"steamcommunity\.com/(profiles|id)/([A-Za-z0-9_\-]+)");
        string kind = "id";
        string value = text;
        if (linkMatch.Success)
        {
            kind = linkMatch.Groups[1].Value;
            value = linkMatch.Groups[2].Value;
        }
        else if (Regex.IsMatch(text, @"^\d{6,}$"))
        {
            kind = "profiles";
        }
        if (value.Length == 0)
        {
            throw new Exception("SteamID 参数是空的");
        }
        return await FetchSteamAsync(CommunityBase + "/" + kind + "/" + Uri.EscapeDataString(value) + "/?xml=1");
    }

    static string XmlValue(string xml, string tag)
    {
        Match match = Regex.Match(xml, "<" + tag + @">(?:<!\[CDATA\[)?([\s\S]*?)(?:\]\]>)?</" + tag + ">",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success == false)
        {
            return "";
        }
        return WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
    }

    static string BanText(string zeroOne) => zeroOne == "1" ? "是" : "否";

    // 从输入提取纯数字appid：兼容纯数字、商店链接、csgo/dota2 这类缩写、含杂质的字符串
    static string ResolveAppId(string input)
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
        string abbreviation = LookupAbbreviation(trimmed);
        if (abbreviation.Length > 0)
        {
            return abbreviation;
        }
        Match anyDigits = Regex.Match(trimmed, "[0-9]{3,}");
        return anyDigits.Success ? anyDigits.Value : "";
    }

    static string LookupAbbreviation(string text) =>
        AppIdAbbreviations.TryGetValue(text.Trim(), out string? appId) ? appId : "";

    async Task<string> FetchSteamAsync(string url)
    {
        int ttl = Math.Clamp(Configuration.CacheTtlSeconds, 0, 3600);
        if (ttl > 0 && responseCache.TryGetValue(url, out (DateTime ExpireAt, string Body) cached)
            && cached.ExpireAt > DateTime.Now)
        {
            LogDetail("命中缓存: " + url);
            return cached.Body;
        }
        bool needsCommunity = url.StartsWith(CommunityBase, StringComparison.Ordinal);
        string proxy = Configuration.ProxyUrl.Trim();
        if (needsCommunity && proxy.Length == 0)
        {
            throw new Exception("这条请求走 steamcommunity.com，实测国内直连12秒超时，得在配置「网络·代理地址」里填HTTP代理（如 http://127.0.0.1:7890）；商店好评率/详情/搜索这类是直连可用的，不用代理");
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Clamp(Configuration.RequestTimeoutSeconds, 5, 120)));
                using HttpClient client = NewHttpClient(proxy, Math.Clamp(Configuration.RequestTimeoutSeconds, 5, 120));
                using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (body.Trim().Length > 0)
                {
                    if (ttl > 0)
                    {
                        TrimCache(ttl);
                        responseCache[url] = (DateTime.Now.AddSeconds(ttl), body);
                    }
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

    static HttpClient NewHttpClient(string proxy, int timeoutSeconds)
    {
        HttpClientHandler handler = new();
        if (proxy.Trim().Length > 0)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy.Trim());
        }
        HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    void TrimCache(int ttl)
    {
        if (responseCache.Count < CacheCap)
        {
            return;
        }
        foreach (string stale in responseCache.Where(entry => entry.Value.ExpireAt <= DateTime.Now).Select(entry => entry.Key).ToList())
        {
            responseCache.Remove(stale);
        }
        if (responseCache.Count >= CacheCap)
        {
            responseCache.Clear();
        }
    }

    async Task<string> ProbeAsync(string url, string proxy)
    {
        try
        {
            using HttpClient client = NewHttpClient(proxy, ProbeTimeoutSeconds);
            using HttpResponseMessage response = await client.GetAsync(url, DestroyCancellationToken);
            return "✅ HTTP " + (int)response.StatusCode;
        }
        catch (Exception probeError)
        {
            return "❌ 不通（" + Shorten(probeError.Message, 90) + "）";
        }
    }

    async Task<string> SafeResolveAsync(string input)
    {
        try
        {
            return await ResolveSteamIdAsync(input) + "（昵称 " + XmlValue(await FetchProfileXmlAsync(input), "steamID") + "）";
        }
        catch (Exception resolveError)
        {
            return "❌ " + Shorten(resolveError.Message, 120);
        }
    }

    static string JoinDescriptions(JsonElement data, string arrayField, int max)
    {
        if (data.TryGetProperty(arrayField, out JsonElement array) == false || array.ValueKind != JsonValueKind.Array)
        {
            return "未知";
        }
        List<string> texts = array.EnumerateArray()
            .Select(item => GetStringField(item, "description"))
            .Where(text => text.Length > 0)
            .Take(max)
            .ToList();
        return texts.Count > 0 ? string.Join("、", texts) : "未知";
    }

    static string JoinArrayAsText(JsonElement data, string arrayField)
    {
        if (data.TryGetProperty(arrayField, out JsonElement array) == false || array.ValueKind != JsonValueKind.Array)
        {
            return "";
        }
        return string.Join("、", array.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "")
            .Where(text => text.Length > 0));
    }

    static int CountArray(JsonElement data, string arrayField)
    {
        if (data.TryGetProperty(arrayField, out JsonElement array) == false || array.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        return array.GetArrayLength();
    }

    static string GetReleaseDate(JsonElement data)
    {
        if (data.TryGetProperty("release_date", out JsonElement releaseDate) && releaseDate.ValueKind == JsonValueKind.Object)
        {
            return GetStringField(releaseDate, "date").Length > 0 ? GetStringField(releaseDate, "date") : "?";
        }
        return "?";
    }

    static string Shorten(string text, int max)
    {
        string single = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        return single.Length > max ? single[..max] + "…" : single;
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
            && (field.ValueKind == JsonValueKind.True
                || (field.ValueKind == JsonValueKind.Number && field.GetInt64() == 1));
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

    static double GetDoubleField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return 0;
        }
        return field.ValueKind switch
        {
            JsonValueKind.Number => field.GetDouble(),
            JsonValueKind.String => double.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0,
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
