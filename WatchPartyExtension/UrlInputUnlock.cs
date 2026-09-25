using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
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
/// 关键约束（踩坑后立下的规矩）：**补丁里绝不触碰 YoutubeVideoInfo 等含 string 字段的
/// il2cpp 结构体**——它们经 Harmony 跨界时按 IntPtr 隐藏引用传递，托管侧读字段
/// （get_VideoId → Il2CppStringToManaged）会拿到垃圾指针直接抛异常。
/// 因此走私走"假 YouTube 链接旁路"：TryExtractPrefix 只改写字符串入参，让原逻辑
/// 自己产出结构体合法的 YoutubeVideoInfo；BuildCanonicalPrefix 不收结构体参数，
/// 靠自己记下的真实 URL 字符串返回。全程只有 string 跨界。
/// </summary>
internal static class UrlInputUnlock
{
    private const string HarmonyId = "gogh.WatchPartyExtension";

    /// <summary>游戏自带示例链接（WebView_HowTo_UrlGuide_ExsampleUrl），原 TryExtract 必然接受。</summary>
    private const string FakeYoutubeUrl = "https://www.youtube.com/watch?v=cxsXkFRQZHA";

    private static HarmonyLib.Harmony _harmony;
    private static bool _unlocked;
    private static bool _unlockedPrev;
    private static string _pendingRealUrl;

    /// <summary>
    /// 走私 URL 的"通行证"集合。LoadUrlAsync 在 async 链的后续才做 IsValidYoutubeVideoUrl
    /// 校验（WebViewController.LoadUrlAsync，VA 0x180E40480），此时输入框守卫早已关闭，
    /// 所以放行过的 URL 要在这里留名，异步校验才认。
    /// </summary>
    private static readonly HashSet<string> _blessedUrls = new();

    public static void Apply()
    {
        // 注意：游戏程序集里有同名 Harmony 命名空间会遮蔽 HarmonyLib.Harmony 类型，必须全限定
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
        _pendingRealUrl = null;
        return __exception;
    }

    // ---------- checker 放行（只碰字符串，不碰结构体） ----------

    /// <summary>
    /// TryExtractVideoInfo(string inputUrl, out YoutubeVideoInfo videoInfo, out string errorMessage)。
    /// 走私链接时把入参替换成假 YouTube 链接、放行原逻辑：原逻辑产出的 VideoInfo 全程在
    /// il2cpp 侧生成，结构体不跨托管边界；真实 URL 记在 _pendingRealUrl 里。
    /// 入参为空/非法时兜底直读地址栏控件文本（曾出现变量与输入框失同步、点击读到空值的情况）。
    /// 任何异常都不得漏进游戏调用栈。
    /// </summary>
    public static bool TryExtractPrefix(ref string inputUrl)
    {
        try
        {
            if (!_unlocked) return true;
            if (string.IsNullOrEmpty(inputUrl) || !IsYoutubeUrl(inputUrl) && !TryNormalizeHttpUrl(inputUrl, out _))
            {
                var alt = TryGetUrlBarText();
                if (alt == null) return true;
                inputUrl = alt;
            }
            if (IsYoutubeUrl(inputUrl)) return true;
            if (!TryNormalizeHttpUrl(inputUrl, out var url)) return true; // 连 URL 都不是，交给原逻辑报错
            _pendingRealUrl = url;
            _blessedUrls.Add(url);
            inputUrl = FakeYoutubeUrl;
            return true;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("UrlInputUnlock: TryExtractPrefix error: " + e.Message);
            return true;
        }
    }

    /// <summary>
    /// 直读地址栏控件（CommonInputFieldBehaviour.GetText）里非空的 URL 文本。
    /// 不走 CurrentWatchPartyInputFieldUrl 变量——场景恢复 URL 时只写输入框文本不写该变量，
    /// 首次点"移动"会读到空。定位用 GameObject 名 + transform 递归 + GetComponent(Type)：
    /// GetComponent(String) 在本运行时会抛 MissingMethodException（ReadOnlySpan AOT 缺失），禁用。
    /// </summary>
    private static string TryGetUrlBarText()
    {
        foreach (var rootName in new[] { "P_WatchPartyUrlInputFieldObject", "P_WatchPartySetupUrlInputFieldObject" })
        {
            var go = UnityEngine.GameObject.Find(rootName);
            if (go == null) continue;
            var text = FindUrlText(go.transform);
            if (text != null) return text;
        }
        return null;
    }

    private static string FindUrlText(UnityEngine.Transform t)
    {
        // 注意：必须走 GameObject.GetComponent(String)——Component.GetComponent(String)
        // （Transform.GetComponent 走的）在本运行时会抛 MissingMethodException（ReadOnlySpan AOT 缺失）
        var comp = t.gameObject.GetComponent("CommonInputFieldBehaviour");
        if (comp != null)
        {
            var field = comp.TryCast<Il2CppCommon.Prefabs.CommonInputField.CommonInputFieldBehaviour>();
            string text = field == null ? null : field.GetText;
            if (!string.IsNullOrEmpty(text) && text.Contains("://"))
            {
                MelonLogger.Msg("UrlInputUnlock: fallback to input field text");
                return text;
            }
        }
        for (int i = 0; i < t.childCount; i++)
        {
            var found = FindUrlText(t.GetChild(i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// BuildCanonicalUrl(YoutubeVideoInfo videoInfo, int startAtSeconds = 0)。
    /// 不声明 videoInfo 参数（避免结构体跨界），有走私 URL 就原样返回（可带 t= 起播偏移），
    /// 否则交给原逻辑（此时必是合法 YouTube VideoInfo）。
    /// </summary>
    public static bool BuildCanonicalPrefix(int startAtSeconds, ref string __result)
    {
        if (!_unlocked || _pendingRealUrl == null) return true;
        var url = _pendingRealUrl;
        _pendingRealUrl = null;
        __result = startAtSeconds > 0
            ? url + (url.Contains("?") ? "&" : "?") + "t=" + startAtSeconds
            : url;
        return false;
    }

    /// <summary>
    /// IsValidYoutubeVideoUrl(string inputUrl, out string errorMessage)。
    /// 两道调用点：输入框提交（守卫内同步）和 WebViewController.LoadUrlAsync（异步、守卫外），
    /// 后者靠 _blessedUrls 通行证放行。
    /// </summary>
    public static bool IsValidPrefix(string inputUrl, out string errorMessage, ref bool __result)
    {
        errorMessage = null;
        if (IsYoutubeUrl(inputUrl)) return true;
        bool allowed = _unlocked || (inputUrl != null && _blessedUrls.Contains(inputUrl));
        if (!allowed) return true;
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
    /// 纯反射实现，避免 AccessTools.Method 查不到时刷 WARNING。
    /// </summary>
    private static MethodBase FindMethod(string typeName, string methodKey)
    {
        var type = AccessTools.TypeByName(typeName)
                   ?? AccessTools.TypeByName(typeName.StartsWith("Il2Cpp") ? typeName.Substring(6) : "Il2Cpp" + typeName);
        if (type == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        return (MethodBase)type.GetMethod(methodKey, flags)
               ?? type.GetMethods(flags).FirstOrDefault(m => m.Name.Contains(methodKey));
    }
}
