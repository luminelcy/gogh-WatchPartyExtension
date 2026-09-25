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
}
