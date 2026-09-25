using System;
using Il2CppVuplex.WebView;
using MelonLoader;
using UnityEngine;
using WebViewControllerType = Il2CppProject.HomeScene.YouTubeWebScene.WebViewControllerObject.WebViewController;

namespace WatchPartyExtension;

/// <summary>
/// UA 修复 + 内核能力探测。
///
/// 背景（2026-09-25 字节级比对确证）：游戏内嵌浏览器就是 Spotify CEF 137.0.17 codecs 构建的
/// 原件（libcef.dll 等与 cef-builds.spotifycdn.com 官方分发 sha1 一致），H.264/AAC 齐全。
/// B 站"不支持 HTML5 播放器"的真凶是 UA 缺 Chrome 标识——Vuplex 宿主的 product_name 是
/// 自定义值，UA 形如 "...AppleWebKit/537.36 (KHTML, like Gecko) &lt;自定义名&gt; Safari/537.36"，
/// B 站 UA sniffing 直接判定不是现代浏览器。本组件把 UA 置为标准 Chrome 137，
/// 并注入探测 JS 把 UA + 编解码器矩阵回传到游戏日志（一次，验证用）。
/// </summary>
internal static class WebViewUaFix
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.7151.104 Safari/537.36";

    private const float TickIntervalSeconds = 1f;
    private const float ProbeIntervalSeconds = 5f;

    private static float _nextTick;
    private static float _nextProbe;
    private static bool _globalUaSet;
    private static bool _loggedFound;
    private static bool _probed;
    private static string _lastStatus;
    private static Il2CppSystem.Action<string> _pollCb;
    private static WebViewControllerType _controller;

    public static void Tick()
    {
        if (Time.realtimeSinceStartup < _nextTick) return;
        _nextTick = Time.realtimeSinceStartup + TickIntervalSeconds;

        try
        {
            if (!_globalUaSet)
            {
                _globalUaSet = true;
                StandaloneWebView.GloballySetUserAgent(ChromeUa);
                MelonLogger.Msg("WebViewUaFix: global UA set to Chrome/137");
            }

            var webView = GetWebView();
            if (webView == null) return;

            // 找到后补一次（覆盖已创建的 webview 实例）
            StandaloneWebView.GloballySetUserAgent(ChromeUa);

            if (!_probed || (Time.realtimeSinceStartup >= _nextProbe && _lastStatus == null))
            {
                _nextProbe = Time.realtimeSinceStartup + ProbeIntervalSeconds;
                _pollCb ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(
                    new Action<string>(OnStatus));
                if (!_probed)
                {
                    _probed = true;
                    webView.ExecuteJavaScript(ProbeJs, _pollCb);
                }
                else
                {
                    webView.ExecuteJavaScript("window.__wpeStatus || ''", _pollCb);
                }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"WebViewUaFix tick failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void OnStatus(string status)
    {
        status = (status ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrEmpty(status) || status == _lastStatus) return;
        _lastStatus = status;
        MelonLogger.Msg("WebViewUaFix: " + status);
    }

    private static Il2CppVuplex.WebView.IWebView GetWebView()
    {
        if (_controller != null && !_controller.WasCollected)
        {
            var prefab = _controller.webViewPrefab;
            return prefab == null ? null : prefab.WebView;
        }
        var go = GameObject.Find("P_WebViewControllerObject");
        if (go == null) return null;
        // GameObject.GetComponent(String) 可用；Component/Transform.GetComponent(String) 会抛异常
        var comp = go.GetComponent("WebViewController");
        var controller = comp == null ? null : comp.TryCast<WebViewControllerType>();
        if (controller == null) return null;
        _controller = controller;
        if (!_loggedFound)
        {
            _loggedFound = true;
            MelonLogger.Msg("WebViewUaFix: webview found");
        }
        var p = controller.webViewPrefab;
        return p == null ? null : p.WebView;
    }

    // 回传：UA + canPlayType + MSE 矩阵（写 window.__wpeStatus，返回值同步带回）
    private const string ProbeJs = """
(function () {
  var p = function (c) {
    try { return (window.MediaSource && window.MediaSource.isTypeSupported(c)) ? 1 : 0; }
    catch (e) { return -1; }
  };
  var v = document.createElement('video');
  window.__wpeStatus = 'ua=[' + navigator.userAgent + ']'
    + ' canplay_h264aac=' + (v.canPlayType('video/mp4; codecs="avc1.42E01E, mp4a.40.2"') || 'no')
    + ' mse=' + (window.MediaSource ? 1 : 0)
    + ' avc1=' + p('video/mp4; codecs="avc1.42E01E"')
    + ' mp4a=' + p('audio/mp4; codecs="mp4a.40.2"')
    + ' av01=' + p('video/mp4; codecs="av01.0.08M.08"');
  return window.__wpeStatus;
})()
""";
}
