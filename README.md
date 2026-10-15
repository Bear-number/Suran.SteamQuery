
# Suran.SteamQuery

Steam 游戏查询，移植自 steam-game-query 技能脚本：全部走 **Steam 商店官方公开接口**（appreviews / appdetails / storesearch + 商店页解析），无需登录、无需 API Key。

## 版本
4.0.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller

## AI 函数（4 个，对齐原脚本命令）
| 函数 | 对应脚本命令 | 说明 |
|---|---|---|
| SteamReviews(appId, count, language) | reviews | 评测统计（等级/好评/差评/总数/好评率）+ 最新评测列表（👍👎、作者、日期、内容截断600字） |
| SteamAppInfo(appIds) | appinfo | 游戏详情：名称/发行日期/价格/简介/商店链接，**逗号分隔批量查询** |
| SteamSearch(keyword) | search | 关键词搜索前10条：appid/名称/价格/链接 |
| SteamBaseGame(appId) | basegame | DLC反查本体（中英文页面关键词兼容），DLC 的好评率应查本体 |

appId 兼容三种输入：纯数字、商店链接（自动提取）、含杂质的字符串。

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| ReviewLanguage | schinese | 评测列表语言：schinese=仅简中，留空=全部语言（SteamReviews 可用 language 参数临时覆盖，all=全部） |
| RequestTimeoutSeconds | 25 | 请求超时 |
| DetailLog | 关 | 请求调试日志 |

## 行为细节（继承原技能）
- 评测接口固定 `filter=recent`（按时间最新）+ `query_summary=1`（统计）+ `purchase_type=all`（含免费获取评测，免费游戏必带）
- 请求带浏览器 UA + Accept-Language；接口空返回/超时**自动重试一次**，仍失败如实报错不编造数据
- DLC 反查本体：商店页中英文关键词（requires the base game / 需要在 Steam 上拥有基础游戏）兼容，多本体依赖时提示人工确认
- 汇报格式与原技能一致（等级+好评率、👍👎标注、长评截断600字）
