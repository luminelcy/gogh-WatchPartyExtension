using System;
using MelonLoader;

[assembly: MelonInfo(typeof(WatchPartyExtension.Core), "WatchPartyExtension", "0.1.0", "kasa", null)]
[assembly: MelonGame("gogh Japan", "gogh")]

namespace WatchPartyExtension;

public class Core : MelonMod
{
    public override void OnInitializeMelon()
    {
        try
        {
            UrlInputUnlock.Apply();
        }
        catch (Exception e)
        {
            MelonLogger.Error("WatchPartyExtension 初始化失败: " + e);
        }
    }

    public override void OnUpdate()
    {
        // WebViewUaFix —— 内嵌浏览器 UA 修复（标准 Chrome 137）。
        // 解码相关代码（能力检测 shim / 诊断面板 / AV1 兜底）已移除：
        // 2026-09-26 起游戏内核换为自编译 CEF 137.0.17，H.264/AAC 解码齐全。
        WebViewUaFix.Tick();
    }
}
