using System;
using Il2CppVuplex.WebView;
using MelonLoader;
using UnityEngine;
using WebViewControllerType = Il2CppProject.HomeScene.YouTubeWebScene.WebViewControllerObject.WebViewController;

namespace WatchPartyExtension;

/// <summary>
/// B 站播放器修复（UA + 能力检测 shim）+ 页面内诊断面板。
///
/// 背景（2026-09-25 字节级比对确证）：游戏内嵌浏览器是 Spotify CEF 137.0.17 codecs 构建的
/// 原件（sha1 与 cef-builds.spotifycdn.com 官方分发一致），H.264/AAC 真实解码能力齐全；
/// 但 B 站播放器报"不支持 HTML5 播放器"（页面外壳是现代 SPA，只有播放器组件挂）——
/// 是能力检测误判（UA / canPlayType / isTypeSupported 某环），不是解码问题。
///
/// 对策：
/// 1. UA 修到标准 Chrome 137（C# 侧 GloballySetUserAgent + 页面侧 navigator 覆写双保险）；
/// 2. **PageLoadScripts 前置 shim**（在页面任何 JS 之前跑）：对 avc1/mp4a 的
///    canPlayType/isTypeSupported 放行——检测过了，真实播放交给内核（真的有解码器）；
/// 3. 诊断面板画在页面左上角（截图可见）：shim 状态 / UA / 检测矩阵。
///
/// 通道教训：B 站 CSP 阻止 ExecuteJavaScript 回传（Vuplex 支持文章 "standalone javascript
/// result blocking"），文档标题通道也不可靠——所以诊断直接显示在页面上。
/// </summary>
internal static class WebViewUaFix
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.7151.104 Safari/537.36";

    private const float TickIntervalSeconds = 1f;
    private const int UaSetMaxTimes = 40;

    private static float _nextTick;
    private static int _uaSetCount;
    private static float _uaSetWindowEnd;
    private static bool _loggedFound;
    private static bool _shimInstalled;
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
                if (_uaSetCount == 1) MelonLogger.Msg("WebViewUaFix: UA set to Chrome/137 (window)");
            }

            var webView = GetWebView();
            if (webView == null || _shimInstalled) return;

            // 1) 前置 shim + 诊断面板都注册为 PageLoadScripts（每次页面加载都跑，面板不怕 reload）
            var scripts = webView.PageLoadScripts;
            if (scripts != null)
            {
                scripts.Add(ShimJs);
                scripts.Add(OverlayJs);
                MelonLogger.Msg("WebViewUaFix: page-load shim registered");
            }
            // 2) 当前页立即补一针面板（reload 后由 PageLoadScripts 接管）
            webView.ExecuteJavaScript(OverlayJs, null);
            // 3) 让当前页面带 shim 重新初始化一次
            webView.Reload();
            _shimInstalled = true;
            MelonLogger.Msg("WebViewUaFix: shim installed, page reloaded");
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

    // 前置 shim：UA 覆写 + avc1/mp4a 检测放行（真实解码交给内核）
    private const string ShimJs = """
(function () {
  try {
    var UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.7151.104 Safari/537.36';
    try { Object.defineProperty(navigator, 'userAgent', { get: function () { return UA; } }); } catch (e) {}
    try { Object.defineProperty(navigator, 'appVersion', { get: function () { return UA.replace('Mozilla/', ''); } }); } catch (e) {}
    try {
      var realCpt = HTMLMediaElement.prototype.canPlayType;
      HTMLMediaElement.prototype.canPlayType = function (t) {
        var r = realCpt.call(this, t);
        if (!r && t && /avc1|mp4a|h264|aac/i.test(t)) return 'probably';
        return r;
      };
    } catch (e) {}
    try {
      if (window.MediaSource) {
        var realSup = MediaSource.isTypeSupported.bind(MediaSource);
        MediaSource.isTypeSupported = function (t) {
          if (t && /avc1|mp4a|h264|aac/i.test(t)) return true;
          return realSup(t);
        };
      }
    } catch (e) {}
    window.__wpeShim = 'applied';
  } catch (e) {
    window.__wpeShim = 'err:' + e;
  }
})();
""";

    // 诊断面板：shim 状态 / UA / 检测矩阵，直接画在页面上（截图可见）
    private const string OverlayJs = """
(function () {
  try {
    var old = document.getElementById('wpe-report');
    if (old) old.remove();
    var d = document.createElement('div');
    d.id = 'wpe-report';
    d.style.cssText = 'position:fixed;top:8px;left:8px;z-index:2147483647;background:rgba(0,0,0,.82);color:#3f6;font:12px/1.5 monospace;padding:8px 12px;border-radius:6px;max-width:70%;white-space:pre-wrap;';
    (document.body || document.documentElement).appendChild(d);
    var upd = function () {
      try {
        var p = function (c) {
          try { return (window.MediaSource && window.MediaSource.isTypeSupported(c)) ? 1 : 0; }
          catch (e) { return -1; }
        };
        var v = document.createElement('video');
        d.textContent = '[WPE] shim=' + (window.__wpeShim || 'none')
          + ' | ua=' + (navigator.userAgent.indexOf('Chrome/') >= 0 ? 'CHROME-OK' : 'BAD')
          + ' | canplay(h264aac)=' + (v.canPlayType('video/mp4; codecs="avc1.42E01E, mp4a.40.2"') || 'no')
          + ' | avc1=' + p('video/mp4; codecs="avc1.42E01E"')
          + ' mp4a=' + p('audio/mp4; codecs="mp4a.40.2"')
          + ' av01=' + p('video/mp4; codecs="av01.0.08M.08"')
          + ' | ' + new Date().toLocaleTimeString();
      } catch (e) {
        d.textContent = '[WPE] panel err:' + e;
      }
    };
    upd();
    setInterval(upd, 2000);
    window.__wpePanel = 1;
  } catch (e) {}
})();
""";
}
