using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2CppProject.HomeScene.YouTubeWebScene;
using MelonLoader;

namespace WatchPartyExtension;

/// <summary>
/// 解除输入框的网页加载限制：Setup 输入框回车 / 场景内"移动"按钮允许打开任意 http(s) URL。
///
/// 机制：游戏对输入框内容的白名单校验全部收口在 WatchPartyYoutubeUrlChecker 的三个方法上
/// （TryExtractVideoInfo / BuildCanonicalUrl / IsValidYoutubeVideoUrl）。
/// 本补丁只在"输入框提交"的调用栈内（守卫开关）放行非 YouTube 的 http(s) URL，
/// 其余调用方（开始共享 / 加入 / 重新同步 / 消息接收）保持 vanilla 校验不变。
///
/// 全程不从托管侧调用任何游戏方法——放行后由 vanilla 原生流程自己完成
/// BuildCanonicalUrl → OpenWebViewRequest 推送，避免 il2cpp 反射传字符串的静默失效坑。
/// </summary>
internal static class UrlInputUnlock
{
    private const string HarmonyId = "gogh.WatchPartyExtension";

    // 注意：游戏程序集里有同名 Harmony 命名空间会遮蔽 HarmonyLib.Harmony 类型，必须全限定
    private static HarmonyLib.Harmony _harmony;
    private static bool _unlocked;
    private static bool _unlockedPrev;

    public static void Apply()
    {
        _harmony = new HarmonyLib.Harmony(HarmonyId);

        int ok = 0;

        // 输入框提交入口：进入时打开守卫，退出（含异常）时还原
        ok += PatchInputGuard(
            "Il2CppProject.HomeScene.YouTubeWebScene.WatchPartySetupUrlInputFieldObject.WatchPartySetupUrlInputFieldObjectPresenter",
            "b__19_0");
        ok += PatchInputGuard(
            "Il2CppProject.HomeScene.YouTubeWebScene.GoToUrlButtonObject.GoToUrlButtonObjectPresenter",
            "b__16_0");

        // 校验收口点：守卫期间放行非 YouTube 的 http(s) URL
        ok += PatchCheckerMethod(nameof(TryExtractPrefix), "TryExtractVideoInfo");
        ok += PatchCheckerMethod(nameof(BuildCanonicalPrefix), "BuildCanonicalUrl");
        ok += PatchCheckerMethod(nameof(IsValidPrefix), "IsValidYoutubeVideoUrl");

        MelonLogger.Msg($"WatchPartyExtension: patches applied {ok}/5");
        if (ok < 5)
            MelonLogger.Warning("WatchPartyExtension: 部分补丁未生效，输入框限制可能未解除");
    }

    // ---------- 输入框守卫 ----------

    private static void GuardEnter()
    {
        _unlockedPrev = _unlocked;
        _unlocked = true;
    }

    private static Exception GuardExit(Exception __exception)
    {
        _unlocked = _unlockedPrev;
        return __exception;
    }

    // ---------- checker 放行 ----------

    /// <summary>TryExtractVideoInfo(string inputUrl, out YoutubeVideoInfo videoInfo, out string errorMessage)</summary>
    public static bool TryExtractPrefix(string inputUrl, out YoutubeVideoInfo videoInfo, out string errorMessage, ref bool __result)
    {
        videoInfo = default;
        errorMessage = null;
        if (!_unlocked || IsYoutubeUrl(inputUrl)) return true;
        if (!TryNormalizeHttpUrl(inputUrl, out var url)) return true; // 连 URL 都不是，交给原逻辑报错
        videoInfo = new YoutubeVideoInfo(url, YoutubeVideoType.Video);
        __result = true;
        return false;
    }

    /// <summary>BuildCanonicalUrl(YoutubeVideoInfo videoInfo, int startAtSeconds = 0) —— 原实现是 youtube 模板，对走私 URL 原样返回。</summary>
    public static bool BuildCanonicalPrefix(YoutubeVideoInfo videoInfo, int startAtSeconds, ref string __result)
    {
        if (!_unlocked) return true;
        var id = videoInfo.VideoId;
        if (string.IsNullOrEmpty(id) || !id.Contains("://")) return true;
        __result = startAtSeconds > 0
            ? id + (id.Contains("?") ? "&" : "?") + "t=" + startAtSeconds
            : id;
        return false;
    }

    /// <summary>IsValidYoutubeVideoUrl(string inputUrl, out string errorMessage)</summary>
    public static bool IsValidPrefix(string inputUrl, out string errorMessage, ref bool __result)
    {
        errorMessage = null;
        if (!_unlocked || IsYoutubeUrl(inputUrl)) return true;
        if (!TryNormalizeHttpUrl(inputUrl, out _)) return true;
        __result = true;
        return false;
    }

    // ---------- URL 判定 ----------

    private static bool IsYoutubeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
               || host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeHttpUrl(string input, out string url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 形如 bilibili.com/video/... 的裸域名补 https://；其余（含空格的搜索词等）拒绝
            if (!Regex.IsMatch(s, @"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+([/?#].*)?$")) return false;
            s = "https://" + s;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        url = s;
        return true;
    }

    // ---------- 补丁装配 ----------

    private static int PatchInputGuard(string typeName, string methodKey)
    {
        var method = FindMethod(typeName, methodKey);
        if (method == null)
        {
            MelonLogger.Warning($"WatchPartyExtension: 未找到 {typeName}.{methodKey}");
            return 0;
        }
        _harmony.CreateProcessor(method)
            .AddPrefix(new HarmonyMethod(typeof(UrlInputUnlock), nameof(GuardEnter)))
            .AddFinalizer(new HarmonyMethod(typeof(UrlInputUnlock), nameof(GuardExit)))
            .Patch();
        return 1;
    }

    private static int PatchCheckerMethod(string prefixName, string methodName)
    {
        var method = FindMethod(
            "Il2CppProject.HomeScene.YouTubeWebScene.WatchPartyYoutubeUrlChecker", methodName);
        if (method == null)
        {
            MelonLogger.Warning($"WatchPartyExtension: 未找到 WatchPartyYoutubeUrlChecker.{methodName}");
            return 0;
        }
        _harmony.CreateProcessor(method)
            .AddPrefix(new HarmonyMethod(typeof(UrlInputUnlock), prefixName))
            .Patch();
        return 1;
    }

    /// <summary>
    /// 按类型名找 il2cpp 代理类型（Il2CppInterop 对 Project.* 加 Il2Cpp 前缀，这里两种都试）；
    /// 方法名支持子串匹配（编译器生成的 lambda 名形如 &lt;StartLifeCycle&gt;b__19_0）。
    /// </summary>
    private static MethodBase FindMethod(string typeName, string methodKey)
    {
        var type = AccessTools.TypeByName(typeName)
                   ?? AccessTools.TypeByName(typeName.StartsWith("Il2Cpp") ? typeName.Substring(6) : "Il2Cpp" + typeName);
        if (type == null) return null;
        return AccessTools.Method(type, methodKey)
               ?? type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                   .FirstOrDefault(m => m.Name.Contains(methodKey));
    }
}
