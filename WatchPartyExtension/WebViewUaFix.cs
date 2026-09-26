using System;
using Il2CppVuplex.WebView;
using MelonLoader;
using UnityEngine;
using WebViewControllerType = Il2CppProject.HomeScene.YouTubeWebScene.WebViewControllerObject.WebViewController;

namespace WatchPartyExtension;

/// <summary>
/// Watch Party 内嵌浏览器 UA 修复。
///
/// 背景：Vuplex 的默认 UA 缺 Chrome 版本标识时，B 站等站点会按"旧内核"策略
/// 拒绝 HTML5 播放器；把 UA 设为标准 Chrome 137 即可正常进入 H5 播放。
///
/// 历史：这里曾带能力检测 shim（对 avc1/mp4a 的 canPlayType 谎报支持）和页面内
/// 诊断面板，用于绕过原内核缺失的 H.264/AAC 解码器。2026-09-26 起游戏内核已
/// 换为自编译的 CEF 137.0.17（ffmpeg_branding=Chrome + proprietary_codecs，
/// 解码器齐全），解码相关的 shim/面板代码已随之移除——内核对能力检测如实作答，
/// 无需再骗。
/// </summary>
internal static class WebViewUaFix
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.7151.104 Safari/537.36";

    private const float TickIntervalSeconds = 0.25f;
    private const int UaSetMaxTimes = 40;

    private static float _nextTick;
    private static int _uaSetCount;
    private static float _uaSetWindowEnd;
    private static bool _loggedFound;
    private static WebViewControllerType _controller;

    public static void Tick()
    {
        if (Time.realtimeSinceStartup < _nextTick) return;
        _nextTick = Time.realtimeSinceStartup + TickIntervalSeconds;

        try
        {
            // UA 设置窗口：webview 创建前后反复设置，覆盖时序竞态
            if (_uaSetCount < UaSetMaxTimes && Time.realtimeSinceStartup < _uaSetWindowEnd + 60f)
            {
                _uaSetCount++;
                StandaloneWebView.GloballySetUserAgent(ChromeUa);
                if (_uaSetCount == 1) MelonLogger.Msg("WebViewUaFix: UA set to Chrome/137");
            }

            // 跟踪 controller 实例：场景重开 = 新实例 → 重开 UA 设置窗口
            DetectController();
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"WebViewUaFix tick failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void DetectController()
    {
        if (_controller != null && !_controller.WasCollected) return;
        var go = GameObject.Find("P_WebViewControllerObject");
        if (go == null) return;
        // GameObject.GetComponent(String) 可用；Component/Transform.GetComponent(String) 会抛异常
        var comp = go.GetComponent("WebViewController");
        var controller = comp == null ? null : comp.TryCast<WebViewControllerType>();
        if (controller == null) return;
        _controller = controller;
        _uaSetCount = 0;
        _uaSetWindowEnd = Time.realtimeSinceStartup;
        if (!_loggedFound)
        {
            _loggedFound = true;
            MelonLogger.Msg("WebViewUaFix: webview found");
        }
    }
}
