
# Suran.SteamQuery

Steam 查询：好评率与评测、游戏详情与价格、关键词搜索、DLC 反查本体、**社区市场现价与五档磨损比价**、**个人资料卡**、**好友列表**、**已购游戏库存**、连通性自检。移植自工作区的 `steam-game-query` 技能，并按 `kira-ai-plugin-steam-main`（KiraAI Steam 插件）把它有而这里没有的功能补齐。

## 版本
4.1.1（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- 外部：走 `steamcommunity.com` 的功能（市场价、资料卡、好友）需要一个 HTTP 代理；商店系（好评率、详情、搜索、本体反查）**不用代理也能用**

## AI 函数（9 个）
| 函数 | 说明 |
|---|---|
| SteamReviews(appId, count=10, language?) | 评测统计（等级/好评/差评/总数/好评率）+ 最新评测列表（👍👎、作者、日期、内容截断600字）。URL 里不带随机参数，所以同一 appid 在缓存时长内只发一次 |
| SteamAppInfo(appIds) | 详情：名称/发行日期/价格（`final_formatted`，有折扣带原价与百分比）/**类型/是否支持简体中文/开发商发行商/DLC 数**/简介/链接，逗号分隔批量 |
| SteamSearch(keyword, count=10) | 商店关键词搜索，返回 appid/名称/价格/链接；输入是 `csgo`、`dota2` 这类缩写时直接回对应 appid，不再发请求。这个接口只回分单位的 `final/initial`，插件按 `currency` 换算成 `¥298.00` 并标原价 |
| SteamBaseGame(appId) | DLC 反查本体（中英文页面关键词兼容） |
| SteamMarketPrice(item, appId, allWears=false) | 市场物品现价：最低挂单/中位价/成交量 + 可直接点的挂单页链接。`appId` 认数字、商店链接、缩写；`allWears=true` 把崭新/略磨损/久经沙场/破损/战痕累累五档挨个查完再按价格从低到高排，末尾给最便宜那档的挂单页 |
| SteamProfile(steamId?) | 资料卡：ID64、昵称、自定义URL、在线状态、可见性、注册日期、所在地、真名、VAC/交易封禁、受限账号、头像、两周时长、个性签名。**免登录免 key** |
| SteamFriends(steamId?, limit=30) | 好友昵称 + SteamID64 + **在线/离线/游戏中**（在玩的还带游戏名），游戏中与在线的排前面、抬头给三种状态各几条。**免登录免 key**（抓好友主页 HTML） |
| SteamOwnedGames(steamId?, appIds?, limit=30) | 已购游戏，按总游玩时长排，带最近游玩日期；`appIds` 只看指定几款。**这一项非要 API Key 不可** |
| SteamEnv() | 自检：store/api/steamcommunity 三域名直连与经代理通不通、代理/默认ID/key 配得对不对、key 实调一次、缓存攒了几条 |

`steamId` 三种写法都认：17位 ID64、自定义URL名（`robinwalker`）、主页链接（`/profiles/{id}` 或 `/id/{name}`），留空就用配置里的默认 ID；`appId` 同样兼容纯数字、商店链接、`csgo/cs/cs2/dota/dota2/tf2/teamfortress/pubg/rust/gta/gta5` 缩写和含杂质的字符串。

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| ReviewLanguage | schinese | 评测列表语言：schinese=仅简中，留空=全部语言（SteamReviews 的 language 参数可临时覆盖，all=全部） |
| RequestTimeoutSeconds | 25 | 单次请求超时。实测商店接口两三秒回，好友那种大网页经代理更久，低于10秒容易偶发失败 |
| DetailLog | 关 | 请求调试日志 |
| ProxyUrl | 空 | HTTP 代理，如 `http://127.0.0.1:7890`。**只有市场价/资料卡/好友这三项非要**（实测 steamcommunity.com 直连12秒超时）；填了以后商店系也一起走代理 |
| CacheTtlSeconds | 300 | 同一网址在这个时长内重复查直接用缓存，0=每次真查，最长 86400（一天）。查五档磨损一次发六条，靠它省掉重复 |
| ApiKey | 空 | Steam Web API Key，`https://steamcommunity.com/dev/apikey` 免费申请。**只有已购库存要它** |
| SteamId | 空 | 默认 17 位 ID64，填了以后资料/好友/库存不传参数就用它 |
| Currency | 23 | 市场接口 currency：23=人民币（`¥ 1,457.18`），1=美元（`$471.39`），3=欧元 |

## 参考插件（KiraAI Steam）功能对照
| 参考插件有 | 这里的做法 |
|---|---|
| `steam_search_games` 商店搜索 | 已有 SteamSearch，另加缩写直达（`csgo`→730）与条数参数。4.1.1 修了价格：参考插件按 `price.final/100` 换算（自己硬写 ¥），这里改成按 `currency` 选符号并带原价；参考插件那行 `tiny_description` 实测**接口根本不回**（`storesearch` 只有 type/name/id/price/tiny_image/metascore/platforms/streamingvideo/controller_support），所以没抄 |
| `steam_market_price` 市场现价 | **移植**：SteamMarketPrice，走同一个 `priceoverview` 接口；4.1.1 另补了参考插件也打的 `market/listings/{appid}/{全名}` 挂单页链接 |
| `steam_market_history` 历史成交 | **兼容实现失败，改文档说明**：实测 `market_history` 页 ajaxcontent 回的是整页 HTML、里面 0 条 `market_history_row`，`pricehistory`/`priceorderhistory` 直接 429——免登录取不到历史成交，要这个得登录态；插件只给现价/中位价/成交量 + 挂单页链接，链接点进去就是历史成交页 |
| `steam_inventory` 已购游戏 | **移植**（SteamOwnedGames），但接口本身要 key：实测不带 key 回 401、`/games/?tab=all` 被 302 到登录页，没有免登录路子，所以按实测把「要 key」写进配置说明和函数说明 |
| `steam_friends` 好友列表 | **兼容实现**：参考插件用 `GetFriendList`+`GetPlayerSummaries`（两个都要 key），状态来自那步的 `personastate`/`gameextrainfo`；这里改抓好友主页 HTML，**免 key 免登录**，实测一页 294 条，状态取每条 `class="… friend_block_v2 persona online  "` 里那个词（同一页 60 在线 / 229 离线 / 5 游戏中），正在玩的游戏名就在同一条的 `friend_game_link` 里，所以 `gameextrainfo` 那项也一并拿到了。剩下的差距只有一条：分不出「忙碌/离开/休眠/想玩」这几种细分态，那几个值免登录页面上确实没有 |
| `steam_player_summary` 用户资料 | **兼容实现**：参考插件用 `GetPlayerSummaries`（要 key）；这里走 `?xml=1`，免 key 免登录，字段反而更多（注册日期、所在地、真名、VAC/交易封禁、受限账号、个性签名）。另外「自定义URL名 → ID64」是本插件自己从 XML 的 `steamID64` 标签取的，**参考插件里并没有 `ResolveVanityURL` 这一步**（那份 main.py 全文 grep 零命中） |
| 配置 `proxy_enabled/proxy_url` | **移植**成一个 `ProxyUrl`：填了就走，没填只在需要它的功能上如实报错 |
| 配置 `cache_ttl` + 定时清理任务 | **移植**成 `CacheTtlSeconds`（4.1.1 把上限从 3600 秒放宽到 86400，跟参考那套一致），不做后台任务——写缓存时顺手清过期项，超上限就整张倒掉 |
| `_search_wear_variants` 五档磨损 | **移植**成 `allWears` 开关，串行发六条（实测并发容易被 429），按价格排 |
| 不带括号的磨损兜底 | **按实测不抄**：参考插件查不到 `(Field-Tested)` 时会再发一条 `名字 + 空格 + 磨损档`；实测这种写法只回裸 `{"success":true}`，等于每档 miss 都白多花一条请求（五档全空 = 十条）。改成五档全空时在文案里直接点明「磨损后缀必须带括号」 |
| `status_map` 0..6 在线状态 | **大部分补上了**：好友列表的 在线/离线/游戏中 与在玩的游戏名，4.1.1 起从 HTML 的 `persona` 类名和 `friend_game_link` 取到；剩下的 2 忙碌/3 离开/4 休眠/5-6 想玩 只有带 key 的 `GetPlayerSummaries` 给。资料卡那侧一直用的是 XML 的 `onlineState`/`stateMessage` 文字，不需要数字表 |
| `data["price"]` 字段 | 参考插件读的这个字段实测**根本不存在**，`priceoverview` 只有 `lowest_price`/`median_price`/`volume`，且某项没数据时整个字段不出现（这里显示 `?`） |
| `/steam` 斜杠命令 | 不做：那是 QQ 侧命令，本插件按 Alife 函数面板暴露 |

## 实测记录（2026-10-15 本机 curl + 反射探针，177 项 0 失败）
- **直连**：`store.steampowered.com/api/appdetails` 200/3.1秒；`api.steampowered.com` 200/1.8秒（不带 key 回 400 说 `key` 必填，域名本身通）；`steamcommunity.com` 两种路径 **12秒超时**（资料 XML、市场接口各测一次都是）→ 这就是 `ProxyUrl` 只服务于 community 系的依据
- 市场现价：`GET /market/priceoverview?appid=730&currency=23&market_hash_name=…`
  - 实回 `{"success":true,"lowest_price":"¥ 3,172.28","volume":"1","median_price":"¥ 3,150.88"}` 形态；`currency=1` 回 `$471.39`
  - **字段会缺**：久经沙场那档实测只有 `lowest_price`，中位价与成交量整个不出现
  - **拼错物品名/磨损档只回裸 `{"success":true}`**，`appid=1` 这种没市场的游戏也回这个——所以文案统一成「当前无挂单（appid 对但物品名拼错、或没人挂，都只回 success:true）」
  - 同一把枪五档实测：崭新 ¥3,172.28 / 略磨损 ¥1,884.30 / 久经 ¥1,457.18 / 破损 ¥1,468.10 / 战痕 ¥1,122.31（崭新贵过战痕这条排得开）
- 资料卡：`GET /profiles/{ID64}/?xml=1` 与 `/id/{自定义名}/?xml=1` 都 **200、免登录免 key**，标签里昵称/真名/所在地/签名走 CDATA，部分带 HTML 实体（解析时 `HtmlDecode`）；查不到时回 `<response><error><![CDATA[The specified profile could not be found.]]></error></response>`，烂 ID 回 `Failed loading profile data`，两种都是 200 而不是 404
- 好友：`GET /profiles/{ID64}/friends/` **200、免登录免 key**，实测 265KB、`data-steamid` 去重后 294 条，昵称在 `friend_block_content` 里；`/friends/?ajax=1` 反而 302（要登录），所以只走 HTML 那一条
- 库存：`/games/?tab=all` 实测 302 到 `/login/`，跟随后是中文登录页（`<title>登录</title>`），页面里 `rgGames`/`data-appid` 一个都没有——**没有免登录路子**；`IPlayerService/GetOwnedGames` 不带 key 实测 **401 Unauthorized**，乱填 key 同样被拒
- 商店详情 `l=schinese`：`supported_languages` 是带 `<strong>` 实体的中文长串，里面出现「简体中文」才判支持；`genres[].description` 给「动作、免费开玩」；`dlc` 是 appid 数字数组（730 实测 1 个）
- 探针里还核过：缓存 TTL=0 时一次都不攒、开了只发一次、过期条目被真结果覆盖、挤满 128 条先清过期再整张倒掉；九个函数的说明都带实测结论（401/302/超时/免key）

### 4.1.1 另加的实测（2026-10-21 本机 curl + 探针复跑 222 项 0 失败）
- `storesearch` 的 `price` 实测**只有三个键**：`{"currency":"CNY","initial":29800,"final":29800}`，单位是分，**没有 `final_formatted`**——`appdetails` 的 `price_overview` 才有这个字段（实回 `¥ 298.00`，`discount_percent` 0，`initial_formatted` 空串）。4.1.0 那版两处都读 `final_formatted`，所以搜索结果里付费游戏的价格全显示成空白
- 免费游戏（730 这类）在 `storesearch` 里**整个 `price` 键不出现**，所以这里的文案是「无价格字段（实测免费游戏就是这样）」，不硬说成免费
- `appreviews` 去掉随机 `client_id` 后照旧 200，`query_summary`/`voted_up` 齐全；那个参数唯一的做用是把 URL 每次都变、把缓存打穿
- 好友页 HTML 的每条类名就是状态来源：`class="selectable friend_block_v2 persona online  "`。2026-10-15 那页抓到的样本只有 online(42)/offline(252) 两种；2026-10-21 复测同一账号是 online 60 / offline 229 / **in-game 5**，带横杠不是 `inGame`，正则得按 `[A-Za-z-]+` 取。在玩的把游戏名写在同一条的 `<span class="friend_game_link">AION 2</span>` 里，免登录也拿得到；页面里 `onlineState` 零命中，`忙碌/离开/休眠/想玩` 那几种细分态确实没有
- `/profiles/{ID64}/friends/` 实测 302 到 `/id/{自定义名}/friends`（少个尾巴斜杠），跟随后 200、266KB——`HttpClient` 默认跟跳转所以插件侧一直没事，用 curl 复现时得加 `-L`
- 挂单页 `market/listings/730/AK-47%20%7C%20Redline%20%28Field-Tested%29` 实测 302 跳到 `…/730/G1807209A023004`、跟随后 200；不带括号那写法（`…Redline%20Field-Tested`）的 `priceoverview` 只回裸 `{"success":true}`
- 商店系直连本轮复测仍通（`appdetails` 200/11.5 秒、`storesearch` 200/3.1 秒、`appreviews` 200/1.3 秒）；`appdetails` 那次是掐着 12 秒关口回来的，`RequestTimeoutSeconds` 默认 25 秒的意义就在这

## 注意事项
- **市场价、资料卡、好友列表不填代理一定失败**，报的是「这条请求走 steamcommunity.com…得在配置「网络·代理地址」里填HTTP代理」；先用 SteamEnv 看一眼直连/经代理两行
- 已购库存 `SteamOwnedGames` 是唯一硬需要 key 的功能；不配 key 时它会直接把原因和申请地址讲清楚，不会假装查成功
- 好友与库存都受对方**隐私设置**影响：主页查不到时资料 XML 实测回 error；好友列表抓不到条目时插件明说「多半是对方把好友列表设成私密」（本机没有私密账号样本，这一档是按页面结构推断，没实测）；游戏详情设私密时 GetOwnedGames 只回空列表——这几种情况插件都当正常结果说，不伪装成查询失败
- 市场接口只给现价，**没有历史成交**（免登录实测取不到，见对照表）；物品要填市场全名，形如 `AK-47 | Redline (Field-Tested)`，磨损档不确定就 `allWears=true`
- `CacheTtlSeconds` 默认 300 秒意味着挂单价最长滞后 5 分钟；盯价格变动时把它设 0
- 好友那列的在线/离线/游戏中也是查那一刻的快照，同样受缓存 TTL 影响；挂单页是插件拼出来的链接，插件自己不发那条请求，点不点得开看你浏览器到不到得了 steamcommunity
- 五档磨损汇总一次发六条请求，实测串行做（并发容易被限流），慢是预期的
