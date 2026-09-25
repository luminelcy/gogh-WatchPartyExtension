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
        // BiliFallback（AV1 兜底播放器）已弃用（无声且不稳定），代码保留备查。
        // 现行：WebViewUaFix —— 内核本身是 Spotify CEF codecs 构建（H.264/AAC 齐全），
        // B 站打不开是 UA 缺 Chrome 标识被 sniffing 拒绝，修复 UA 并回传能力探测。
        WebViewUaFix.Tick();
    }
}
