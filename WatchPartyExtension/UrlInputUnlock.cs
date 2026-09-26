using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;

namespace WatchPartyExtension;

/// <summary>
/// 解除 Watch Party 对网页 URL 的白名单校验：任意 http(s) URL 都能通过
/// WatchPartyYoutubeUrlChecker 的三个校验方法（TryExtractVideoInfo /
/// BuildCanonicalUrl / IsValidYoutubeVideoUrl）。
///
/// 机制：校验全部收口在上述三方法，因此**直接在收口方法上无条件放行**——
/// 日志实测存在守卫（调用栈开关）覆盖不到的第四个调用入口（首跳现场），
/// 收口放行一劳永逸。守卫仍保留：为提交链路做地址栏文本兜底 + 打点观测。
/// 副作用（已知取舍）：共享/重同步对非 YT 页面也放行，本机显示正常，
/// 未装 mod 的客人会收到假油管信息（跳到示例视频）——与"装了 mod 才能看"一致。
///
/// 关键约束（踩坑后立下的规矩）：**补丁里绝不触碰 YoutubeVideoInfo 等含 string 字段的
/// il2cpp 结构体**——它们经 Harmony 跨界时按 IntPtr 隐藏引用传递，托管侧读字段
/// （get_VideoId → Il2CppStringToManaged）会拿到垃圾指针直接抛异常。
/// 因此走私走"假 YouTube 链接旁路"：TryExtractPrefix 只改写字符串入参，让原逻辑
/// 自己产出结构体合法的 YoutubeVideoInfo；真实网址记在 _pendingRealUrl（5s 时效），
/// BuildCanonicalPrefix 直接返回它。全程只有 string 跨界。
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
    private static long _pendingRealAtMs;

    /// <summary>
    /// 走私 URL 的"通行证"集合。LoadUrlAsync 等 async 链的后续校验在守卫关闭后才发生，
    /// 放行过的 URL 要在这里留名，异步校验才认。
    /// </summary>
    private static readonly HashSet<string> _blessedUrls = new();

    /// <summary>
    /// _pendingRealUrl 时效（毫秒）。BuildCanonicalUrl 可能在走私动作之后很久才被别的流程
    /// （如客人跟随网络快照）调到，过期的走私结果必须作废，防止把陈旧 URL 顶给网络流程。
    /// 同一 UI 动作内的 TryExtract→BuildCanonical 间隔只有几毫秒，5 秒窗口绰绰有余。
    /// </summary>
    private const long PendingTtlMs = 5000;

    private const int ExpectedPatches = 6;

    public static void Apply()
    {
        // 注意：游戏程序集里有同名 Harmony 命名空间会遮蔽 HarmonyLib.Harmony 类型，必须全限定
        _harmony = new HarmonyLib.Harmony(HarmonyId);

        int ok = 0;

        // 输入框/按钮入口：进入时打开守卫，退出（含异常）时还原
        ok += PatchGuard(
            "Il2CppProject.HomeScene.YouTubeWebScene.WatchPartySetupUrlInputFieldObject.WatchPartySetupUrlInputFieldObjectPresenter",
            "b__19_0", nameof(GuardEnterSetup), nameof(GuardExitSetup));
        ok += PatchGuard(
            "Il2CppProject.HomeScene.YouTubeWebScene.GoToUrlButtonObject.GoToUrlButtonObjectPresenter",
            "b__16_0", nameof(GuardEnterGoTo), nameof(GuardExitGoTo));
        // "显示到物品上"按钮：点击时校验 CurrentOpenedWebViewUrl 并 TryExtract 出 VideoInfo 填进请求
        ok += PatchGuard(
            "Il2CppProject.HomeScene.YouTubeWebScene.WebViewDisplayToItemButtonObject.WebViewDisplayToItemButtonObjectPresenter",
            "b__25_2", nameof(GuardEnterDisplay), nameof(GuardExitDisplay));

        // 校验收口点：守卫期间放行非 YouTube 的 http(s) URL
        ok += PatchCheckerMethod(nameof(TryExtractPrefix), "TryExtractVideoInfo");
        ok += PatchCheckerMethod(nameof(BuildCanonicalPrefix), "BuildCanonicalUrl");
        ok += PatchCheckerMethod(nameof(IsValidPrefix), "IsValidYoutubeVideoUrl");

        MelonLogger.Msg($"WatchPartyExtension: patches applied {ok}/{ExpectedPatches}");
        if (ok < ExpectedPatches)
            MelonLogger.Warning("WatchPartyExtension: 部分补丁未生效，输入框限制可能未解除");
    }

    // ---------- 入口守卫 ----------
    // 注意：不能用 Harmony 的 MethodBase __method 参数——il2cpp 包装方法上注入会抛
    // "Parameter __method does not contain a valid index" 直接打断整个 Apply()。
    // 每处守卫用独立薄包装，名字自己报。

    private static void GuardEnterSetup() => GuardEnterCore();
    private static void GuardEnterGoTo() => GuardEnterCore();
    private static void GuardEnterDisplay() => GuardEnterCore();
    private static Exception GuardExitSetup(Exception __exception) => GuardExitCore(__exception);
    private static Exception GuardExitGoTo(Exception __exception) => GuardExitCore(__exception);
    private static Exception GuardExitDisplay(Exception __exception) => GuardExitCore(__exception);

    private static void GuardEnterCore()
    {
        _unlockedPrev = _unlocked;
        _unlocked = true;
        _pendingRealUrl = null; // 新提交栈从干净状态开始，防上一次未消费的走私串台
    }

    private static Exception GuardExitCore(Exception exception)
    {
        _unlocked = _unlockedPrev;
        _pendingRealUrl = null;
        return exception;
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
            if (string.IsNullOrEmpty(inputUrl) || !IsYoutubeUrl(inputUrl) && !TryNormalizeHttpUrl(inputUrl, out _))
            {
                // 空/非 URL 输入：守卫内（提交链路变量失同步的老问题）用地址栏文本兜底；
                // 守卫外保持 vanilla 语义，不替别的调用方猜输入。
                if (!_unlocked) return true;
                var alt = TryGetUrlBarText();
                if (alt == null) return true;
                inputUrl = alt;
                MelonLogger.Msg("UrlInputUnlock: fallback to input field text");
            }
            if (IsYoutubeUrl(inputUrl)) return true;
            if (!TryNormalizeHttpUrl(inputUrl, out var url)) return true;
            // 无条件走私（不看守卫）：校验方法直接放行输入的网址——覆盖全部调用入口
            // （含守卫挂不到的第四个入口）。真实网址记下供 BuildCanonical 返回。
            _pendingRealUrl = url;
            _pendingRealAtMs = Environment.TickCount64;
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
            if (!string.IsNullOrEmpty(text) && text.Contains("://")) return text;
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
    /// 不声明 videoInfo 参数（避免结构体跨界）。有【未过期】的走私 URL 就直接返回输入的
    /// 真实网址（可带 t= 起播偏移），否则交给原逻辑（合法 YouTube VideoInfo 走原路）。
    /// 过期判断防止陈旧走私结果被网络流程（客人跟随）误用。
    /// </summary>
    public static bool BuildCanonicalPrefix(int startAtSeconds, ref string __result)
    {
        var pending = _pendingRealUrl;
        if (pending == null || Environment.TickCount64 - _pendingRealAtMs > PendingTtlMs) return true;
        _pendingRealUrl = null;
        __result = startAtSeconds > 0
            ? pending + (pending.Contains("?") ? "&" : "?") + "t=" + startAtSeconds
            : pending;
        return false;
    }

    /// <summary>
    /// IsValidYoutubeVideoUrl(string inputUrl, out string errorMessage)。
    /// 无条件放行任意 http(s) URL（首跳现场证明存在守卫覆盖不到的调用入口，
    /// 直接在收口方法上全量放行一劳永逸）。非 http(s)（空、搜索词等）仍交还原逻辑。
    /// </summary>
    public static bool IsValidPrefix(string inputUrl, out string errorMessage, ref bool __result)
    {
        errorMessage = null;
        if (string.IsNullOrEmpty(inputUrl) || IsYoutubeUrl(inputUrl)) return true;
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

    private static int PatchGuard(string typeName, string methodKey, string enterName, string exitName)
    {
        var method = FindMethod(typeName, methodKey);
        if (method == null)
        {
            MelonLogger.Warning($"WatchPartyExtension: 未找到 {typeName}.{methodKey}");
            return 0;
        }
        _harmony.CreateProcessor(method)
            .AddPrefix(new HarmonyMethod(typeof(UrlInputUnlock), enterName))
            .AddFinalizer(new HarmonyMethod(typeof(UrlInputUnlock), exitName))
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
