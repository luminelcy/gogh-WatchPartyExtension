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
/// 原件（与 cef-builds.spotifycdn.com 官方分发 sha1 一致），H.264/AAC 齐全；B 站
/// "不支持 HTML5 播放器" + 站内链接点击不跳转，疑为 UA 缺 Chrome 标识被 sniffing 判为
/// 老浏览器走降级路径。本组件把 UA 置为标准 Chrome 137 并回传探测。
///
/// 回传通道说明：B 站的 CSP 禁止 ExecuteJavaScript 返回结果（Vuplex 官方支持文章
/// "standalone javascript result blocking"），所以探测结果走 **document.title 通道**
/// （JS 写标题、C# 轮询读，CSP 管不着）。B 站 SPA 会覆盖标题，JS 按间隔重复追加 `||WPE:` 段。
/// </summary>
internal static class WebViewUaFix
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.7151.104 Safari/537.36";

    private const float TickIntervalSeconds = 1f;
    private const float TitlePollIntervalSeconds = 2f;
    private const int UaSetMaxTimes = 40;

    private static float _nextTick;
    private static float _nextTitlePoll;
    private static int _uaSetCount;
    private static float _uaSetWindowEnd;
    private static bool _loggedFound;
    private static bool _probeInjected;
    private static string _lastReport;
    private static WebViewControllerType _controller;

    public static void Tick()
    {
        if (Time.realtimeSinceStartup < _nextTick) return;
        _nextTick = Time.realtimeSinceStartup + TickIntervalSeconds;

        try
        {
            // UA 设置窗口：前 60 秒反复设置，覆盖 webview 创建时序竞态
            if (_uaSetCount < UaSetMaxTimes && Time.realtimeSinceStartup < _uaSetWindowEnd + 60f)
            {
                _uaSetCount++;
                StandaloneWebView.GloballySetUserAgent(ChromeUa);
                if (_uaSetCount == 1) MelonLogger.Msg("WebViewUaFix: UA set to Chrome/137 (window)");
            }

            var webView = GetWebView();
            if (webView == null) return;

            if (!_probeInjected)
            {
                _probeInjected = true;
                webView.ExecuteJavaScript(ProbeJs, null);
                MelonLogger.Msg("WebViewUaFix: probe injected");
            }

            if (Time.realtimeSinceStartup >= _nextTitlePoll)
            {
                _nextTitlePoll = Time.realtimeSinceStartup + TitlePollIntervalSeconds;
                string title = webView.Title;
                if (!string.IsNullOrEmpty(title))
                {
                    int idx = title.IndexOf("||WPE:", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        string report = title.Substring(idx + 6);
                        if (report != _lastReport)
                        {
                            _lastReport = report;
                            MelonLogger.Msg("WebViewUaFix: " + report);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"WebViewUaFix tick failed: {e.GetType().Name}: {e.Message}");
        }
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
        // webview 刚被发现 → 重置 UA 设置窗口（覆盖创建时序竞态）
        _uaSetCount = 0;
        _uaSetWindowEnd = Time.realtimeSinceStartup;
        if (!_loggedFound)
        {
            _loggedFound = true;
            MelonLogger.Msg("WebViewUaFix: webview found");
        }
        var p = controller.webViewPrefab;
        return p == null ? null : p.WebView;
    }

    // 探测：UA + canPlayType + MSE 矩阵，结果写 document.title 的 ||WPE: 段（间隔重复防 SPA 覆盖）
    private const string ProbeJs = """
(function () {
  var report = function () {
    var p = function (c) {
      try { return (window.MediaSource && window.MediaSource.isTypeSupported(c)) ? 1 : 0; }
      catch (e) { return -1; }
    };
    var v = document.createElement('video');
    return 'ua=[' + navigator.userAgent + ']'
      + ' canplay_h264aac=' + (v.canPlayType('video/mp4; codecs="avc1.42E01E, mp4a.40.2"') || 'no')
      + ' mse=' + (window.MediaSource ? 1 : 0)
      + ' avc1=' + p('video/mp4; codecs="avc1.42E01E"')
      + ' mp4a=' + p('audio/mp4; codecs="mp4a.40.2"')
      + ' av01=' + p('video/mp4; codecs="av01.0.08M.08"');
  };
  var n = 0;
  var push = function () {
    try {
      document.title = document.title.replace(/\|\|WPE:[\s\S]*$/, '') + '||WPE:' + report();
    } catch (e) {}
    if (++n < 30) setTimeout(push, 2000);
  };
  push();
  return 'ok';
})()
""";
}
