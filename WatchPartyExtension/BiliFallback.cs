using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using WebViewControllerType = Il2CppProject.HomeScene.YouTubeWebScene.WebViewControllerObject.WebViewController;

namespace WatchPartyExtension;

/// <summary>
/// B 站 AV1 兜底播放器。
///
/// 背景：游戏内嵌浏览器（Vuplex WebView.vuplex + libcef.dll，CEF/Chromium 137）只带开源解码器
/// （AV1/VP9/Opus ✓，H.264/AAC ✗），B 站播放器检测 avc1 失败会显示"不支持 HTML5 播放器"。
/// B 站视频页自带 window.__playinfo__（DASH 流清单，含 AV1 轨），于是注入 JS 自建
/// video+MSE 播放器。JS 侧把过程/结果写进 window.__wpeStatus，C# 轮询读回游戏日志（排障生命线）。
///
/// 运行时注意：GetComponent(String) 会抛 MissingMethodException（Il2Cpp ReadOnlySpan AOT 缺失），
/// 定位一律用 GameObject.Find + GetComponent(Il2CppTypeOf&lt;T&gt;())。
/// </summary>
internal static class BiliFallback
{
    private const float TickIntervalSeconds = 1f;
    private const float StatusPollIntervalSeconds = 2f;

    private static float _nextTick;
    private static float _nextStatusPoll;
    private static string _injectedUrl;
    private static string _lastStatus;
    private static bool _loggedScan;
    private static bool _loggedController;
    private static bool _loggedWebView;
    private static bool _loggedNoWebView;

    private static WebViewControllerType _controller;
    private static Il2CppSystem.Action<string> _injectCb;
    private static Il2CppSystem.Action<string> _pollCb;

    private static void JsLog(string s)
    {
        if (!string.IsNullOrEmpty(s)) MelonLogger.Msg("BiliFallback: " + s);
    }

    private static Il2CppSystem.Action<string> InjectCb =>
        _injectCb ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(new Action<string>(JsLog));

    private static Il2CppSystem.Action<string> PollCb =>
        _pollCb ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(new Action<string>(OnStatusPolled));

    public static void Tick()
    {
        if (Time.realtimeSinceStartup < _nextTick) return;
        _nextTick = Time.realtimeSinceStartup + TickIntervalSeconds;

        try
        {
            if (!_loggedScan)
            {
                _loggedScan = true;
                MelonLogger.Msg("BiliFallback: tick alive");
            }
            var webView = GetWebView();
            if (webView == null)
            {
                if (!_loggedNoWebView)
                {
                    _loggedNoWebView = true;
                    MelonLogger.Msg("BiliFallback: webview not found yet (will retry)");
                }
                return;
            }
            _loggedNoWebView = false;

            string url = webView.Url;
            if (string.IsNullOrEmpty(url)) return;

            bool isBiliVideo = url.Contains("bilibili.com/video/") || url.Contains("bilibili.com/bangumi/play/");

            if (url != _injectedUrl)
            {
                if (!isBiliVideo)
                {
                    _injectedUrl = null;
                    _lastStatus = null;
                    return;
                }
                webView.ExecuteJavaScript(BiliPlayerJs, InjectCb);
                _injectedUrl = url;
                _lastStatus = null;
                _nextStatusPoll = Time.realtimeSinceStartup + StatusPollIntervalSeconds;
                MelonLogger.Msg($"BiliFallback: injected at {url}");
                return;
            }

            // 同一 URL：轮询 JS 侧状态（页面被重载后 window.__wpeStatus 变空 → 重新注入）
            if (isBiliVideo && Time.realtimeSinceStartup >= _nextStatusPoll)
            {
                _nextStatusPoll = Time.realtimeSinceStartup + StatusPollIntervalSeconds;
                webView.ExecuteJavaScript("window.__wpeStatus || ''", PollCb);
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"BiliFallback tick failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void OnStatusPolled(string status)
    {
        status = (status ?? string.Empty).Trim().Trim('"');
        if (status == _lastStatus) return;
        if (string.IsNullOrEmpty(status))
        {
            // 页面重载（window 重建）→ 允许重新注入
            if (_lastStatus != null) MelonLogger.Msg("BiliFallback: page reloaded, will re-inject");
            _injectedUrl = null;
            _lastStatus = null;
            return;
        }
        _lastStatus = status;
        MelonLogger.Msg("BiliFallback: status " + status);
    }

    private static Il2CppVuplex.WebView.IWebView GetWebView()
    {
        if (_controller != null && !_controller.WasCollected)
        {
            var prefab = _controller.webViewPrefab;
            return prefab == null ? null : prefab.WebView;
        }

        // 快路径：按 GameObject 名直取（Bridge 实证对象名为 P_WebViewControllerObject）
        var go = GameObject.Find("P_WebViewControllerObject");
        if (go != null)
        {
            // GameObject.GetComponent(String) 可用；Component/Transform.GetComponent(String) 会抛异常
            var comp = go.GetComponent("WebViewController");
            var controller = comp == null ? null : comp.TryCast<WebViewControllerType>();
            if (controller != null) return Cache(controller);
        }

        // 兜底：按 il2cpp 类名扫描全部 MonoBehaviour
        foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (mb == null || mb.Pointer == IntPtr.Zero) continue;
            var cls = IL2CPP.il2cpp_object_get_class(mb.Pointer);
            if (cls == IntPtr.Zero) continue;
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls));
            if (name != "WebViewController") continue;
            var controller = mb.TryCast<WebViewControllerType>();
            if (controller == null) continue;
            return Cache(controller);
        }
        return null;
    }

    private static Il2CppVuplex.WebView.IWebView Cache(WebViewControllerType controller)
    {
        _controller = controller;
        if (!_loggedController)
        {
            _loggedController = true;
            MelonLogger.Msg("BiliFallback: controller found");
        }
        var prefab = controller.webViewPrefab;
        var webView = prefab == null ? null : prefab.WebView;
        if (webView != null && !_loggedWebView)
        {
            _loggedWebView = true;
            MelonLogger.Msg("BiliFallback: webview ready");
        }
        return webView;
    }

    // 语言：JS。自建迷你播放器（原生 <video controls>），av01 优先；
    // 过程/结果写 window.__wpeStatus（C# 轮询读回），返回值为注入时的同步状态。
    private const string BiliPlayerJs = """
(function () {
  var probe = function (c) {
    try { return (window.MediaSource && window.MediaSource.isTypeSupported(c)) ? 1 : 0; }
    catch (e) { return -1; }
  };
  var caps = function () {
    return 'mse=' + (window.MediaSource ? 1 : 0)
      + ' avc1=' + probe('video/mp4; codecs="avc1.42E01E"')
      + ' av01=' + probe('video/mp4; codecs="av01.0.08M.08"')
      + ' vp09=' + probe('video/mp4; codecs="vp09.00.10.08"')
      + ' mp4a=' + probe('audio/mp4; codecs="mp4a.40.2"');
  };

  var build = function () {
    try {
      var C = caps();
      if (document.getElementById('wpe-player')) { window.__wpeStatus = 'ok:already ' + C; return; }
      var pi = window.__playinfo__;
      if (!pi || !pi.data || !pi.data.dash) {
        window.__wpeTry = (window.__wpeTry || 0) + 1;
        if (window.__wpeTry < 20) {
          window.__wpeStatus = 'wait:noplayinfo#' + window.__wpeTry + ' ' + C;
          setTimeout(build, 500);
        } else {
          window.__wpeStatus = 'fail:noplayinfo ' + C;
        }
        return;
      }
      var dash = pi.data.dash;
      var vids = (dash.video || []).slice();
      var auds = (dash.audio || []).slice();
      if (!vids.length) { window.__wpeStatus = 'fail:notracks ' + C; return; }

      var sup = function (c) { return probe('video/mp4; codecs="' + c + '"') === 1; };
      vids.sort(function (a, b) {
        var pa = sup(a.codecs) ? (a.codecs.indexOf('av01') === 0 ? 2 : 1) : 0;
        var pb = sup(b.codecs) ? (b.codecs.indexOf('av01') === 0 ? 2 : 1) : 0;
        if (pa !== pb) return pb - pa;
        return (b.bandwidth || 0) - (a.bandwidth || 0);
      });
      var v = vids[0];
      if (!sup(v.codecs)) {
        window.__wpeStatus = 'fail:novideocodec ' + C + ' tracks='
          + vids.map(function (t) { return t.codecs; }).join(',');
        return;
      }
      auds.sort(function (a, b) { return (b.bandwidth || 0) - (a.bandwidth || 0); });
      var a = auds[0];
      var aok = a && probe('audio/mp4; codecs="' + a.codecs + '"') === 1;

      var box = document.querySelector('.bpx-player-container')
             || document.querySelector('#bofqi')
             || document.querySelector('#bilibili-player')
             || document.querySelector('.player-wrap');
      if (!box) { window.__wpeStatus = 'fail:nobox ' + C; return; }

      var wrap = document.createElement('div');
      wrap.id = 'wpe-player';
      wrap.style.cssText = 'position:relative;width:100%;height:100%;background:#000;';
      var video = document.createElement('video');
      video.controls = true;
      video.autoplay = true;
      video.style.cssText = 'width:100%;height:100%;display:block;';
      wrap.appendChild(video);
      if (!aok) {
        var n = document.createElement('div');
        n.textContent = '⚠ 此浏览器内核不支持 AAC 音频，当前无声播放';
        n.style.cssText = 'position:absolute;top:8px;left:8px;color:#fff;background:rgba(0,0,0,.55);padding:4px 10px;border-radius:4px;font-size:12px;z-index:10;';
        wrap.appendChild(n);
      }
      box.innerHTML = '';
      box.appendChild(wrap);

      var ms = new MediaSource();
      video.src = URL.createObjectURL(ms);

      var load = function (track, kind, sb) {
        var u = (track.base_url || '').replace(/^http:/, 'https:');
        fetch(u).then(function (r) {
          if (!r.ok) throw new Error(kind + ' http ' + r.status);
          return r.arrayBuffer();
        }).then(function (buf) {
          sb.appendBuffer(buf);
          if (kind === 'video') window.__wpeStatus = 'ok:playing v=' + v.codecs + ' a=' + (aok ? a.codecs : 'muted') + ' ' + C;
          else window.__wpeStatus = 'ok:playing v=' + v.codecs + ' a=' + a.codecs + ' ' + C;
        }).catch(function (e) {
          window.__wpeStatus = 'fail:fetch-' + kind + ' ' + e + ' ' + C;
          if (kind === 'audio') {
            var n = wrap.querySelector('div');
            if (n) n.textContent = '⚠ 音频拉取失败，当前无声播放';
          }
        });
      };

      ms.addEventListener('sourceopen', function () {
        try {
          var vsb = ms.addSourceBuffer('video/mp4; codecs="' + v.codecs + '"');
          load(v, 'video', vsb);
          if (aok && a) {
            var asb = ms.addSourceBuffer('audio/mp4; codecs="' + a.codecs + '"');
            load(a, 'audio', asb);
          }
        } catch (e) {
          window.__wpeStatus = 'fail:mse ' + e + ' ' + C;
        }
      });

      window.__wpeStatus = 'ok:mounted v=' + v.codecs + ' a=' + (aok ? a.codecs : 'muted') + ' ' + C;
    } catch (e) {
      window.__wpeStatus = 'fail:ex ' + e + ' ' + (typeof C !== 'undefined' ? C : '');
    }
  };

  build();
  return window.__wpeStatus || '';
})()
""";
}
