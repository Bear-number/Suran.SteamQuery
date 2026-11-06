using Alife.Framework;
using Microsoft.AspNetCore.Components.Rendering;

namespace Suran.SteamQuery;

/// <summary>Steam游戏查询 的设置页。内容由 SuranUiKit 按配置项自动分组生成。</summary>
public sealed class SteamQueryUI : ModuleUIBase<SteamQueryModule, SteamQueryConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }
        SuranUiKit.Render(this, StateHasChanged, b, Configuration, "Steam游戏查询", "好评率、价格、市场磨损比价、资料与好友；只有已购库存需要 API Key", "#2563eb");
    }
}
