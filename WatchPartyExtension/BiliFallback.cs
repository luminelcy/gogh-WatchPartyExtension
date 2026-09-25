using System;
using System.Collections.Generic;
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
/// 而 B 站视频页自带 window.__playinfo__（服务端注入的 DASH 流清单，含 AV1 轨），
/// 这个内核恰好能解 AV1 —— 于是注入一段 JS 读 __playinfo__、自建 video+MSE 播 av01 流，
/// 盖掉原播放器。音频轨是 AAC：运行时检测 isTypeSupported，支持则一并播，否则静音并提示。
///
/// 触发：OnUpdate 节流轮询 WebView 当前 URL，进入 B 站视频页且未注入过时注入一次；
/// SPA 换 P（URL 变化）时对新 URL 重新注入。
/// </summary>
internal static class BiliFallback
{
    private const float TickIntervalSeconds = 1f;

    private static float _nextTick;
    private static string _injectedUrl;

    // il2cpp 代理对象字典键用 Pointer（类名扫描结果缓存）
    private static IntPtr _controllerPtr;
    private static WebViewControllerType _controller;

    public static void Tick()
    {
        if (Time.realtimeSinceStartup < _nextTick) return;
        _nextTick = Time.realtimeSinceStartup + TickIntervalSeconds;

        try
        {
            var webView = GetWebView();
            if (webView == null) return;

            string url = webView.Url;
            if (string.IsNullOrEmpty(url)) return;

            bool isBiliVideo = url.Contains("bilibili.com/video/") || url.Contains("bilibili.com/bangumi/play/");
            if (!isBiliVideo)
            {
                _injectedUrl = null;
                return;
            }
            if (url == _injectedUrl) return;

            webView.ExecuteJavaScript(BiliPlayerJs, null);
            _injectedUrl = url;
            MelonLogger.Msg($"BiliFallback: injected at {url}");
        }
        catch (Exception e)
        {
            // WebViewController/WebView 尚未初始化等情况会走到这里，下一拍重试
            if (e is NullReferenceException or InvalidOperationException) return;
            MelonLogger.Warning("BiliFallback tick failed: " + e.Message);
        }
    }

    private static Il2CppVuplex.WebView.IWebView GetWebView()
    {
        if (_controller != null && !_controller.WasCollected)
        {
            var wv = _controller.webViewPrefab?.WebView;
            if (wv != null) return wv;
            return null;
        }

        foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (mb == null) continue;
            var ptr = mb.Pointer;
            if (ptr == IntPtr.Zero) continue;
            var cls = IL2CPP.il2cpp_object_get_class(ptr);
            if (cls == IntPtr.Zero) continue;
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls));
            if (name != "WebViewController") continue;

            var controller = mb.TryCast<WebViewControllerType>();
            if (controller == null) continue;
            _controller = controller;
            _controllerPtr = ptr;
            return controller.webViewPrefab?.WebView;
        }
        return null;
    }

    // 语言：JS。自建迷你播放器（原生 <video controls>），av01 优先。
    private const string BiliPlayerJs = """
(function () {
  var log = function (m) { try { console.log('[WPE] ' + m); } catch (e) {} };
  if (window.__wpeUrl === location.href) return;
  window.__wpeUrl = location.href;

  var tryBuild = function (attempt) {
    var pi = window.__playinfo__;
    if (!pi || !pi.data || !pi.data.dash) {
      if (attempt < 20) { setTimeout(function () { tryBuild(attempt + 1); }, 500); }
      else { log('no __playinfo__ dash'); }
      return;
    }
    var dash = pi.data.dash;
    var vids = (dash.video || []).slice();
    var auds = (dash.audio || []).slice();
    if (!vids.length) { log('no video tracks'); return; }

    // 视频轨：av01 优先，同优先级内取高码率
    vids.sort(function (a, b) {
      var pa = (a.codecs || '').indexOf('av01') === 0 ? 1 : 0;
      var pb = (b.codecs || '').indexOf('av01') === 0 ? 1 : 0;
      if (pa !== pb) return pb - pa;
      return (b.bandwidth || 0) - (a.bandwidth || 0);
    });
    var v = vids[0];
    auds.sort(function (a, b) { return (b.bandwidth || 0) - (a.bandwidth || 0); });
    var a = auds[0];

    var ms = window.MediaSource;
    var videoOk = ms && ms.isTypeSupported('video/mp4; codecs="' + v.codecs + '"');
    var audioOk = a && ms && ms.isTypeSupported('audio/mp4; codecs="' + a.codecs + '"');
    log('video ' + v.codecs + ' ok=' + videoOk + ', audio ' + (a ? a.codecs : 'none') + ' ok=' + audioOk);
    if (!videoOk) { log('video codec unsupported'); return; }

    var box = document.querySelector('#bofqi')
           || document.querySelector('.bpx-player-container')
           || document.querySelector('#bilibili-player')
           || document.querySelector('.player-wrap');
    if (!box) {
      if (attempt < 20) { setTimeout(function () { tryBuild(attempt + 1); }, 500); }
      else { log('player box not found'); }
      return;
    }

    var wrap = document.createElement('div');
    wrap.style.cssText = 'position:relative;width:100%;height:100%;background:#000;';
    var video = document.createElement('video');
    video.controls = true;
    video.autoplay = true;
    video.style.cssText = 'width:100%;height:100%;display:block;';
    wrap.appendChild(video);
    if (!audioOk) {
      var n = document.createElement('div');
      n.textContent = '⚠ 此浏览器内核不支持 AAC 音频，当前无声播放';
      n.style.cssText = 'position:absolute;top:8px;left:8px;color:#fff;background:rgba(0,0,0,.55);padding:4px 10px;border-radius:4px;font-size:12px;z-index:10;';
      wrap.appendChild(n);
    }
    box.innerHTML = '';
    box.appendChild(wrap);

    var msource = new MediaSource();
    video.src = URL.createObjectURL(msource);

    var loadTrack = function (track, kind, sb) {
      // CDN 链接可能是 http://，https 页面下要升级（upos 支持 https）
      var u = (track.base_url || '').replace(/^http:/, 'https:');
      fetch(u).then(function (r) {
        if (!r.ok) throw new Error(kind + ' http ' + r.status);
        return r.arrayBuffer();
      }).then(function (buf) {
        sb.appendBuffer(buf);
        log(kind + ' appended ' + buf.byteLength + ' bytes');
      }).catch(function (e) {
        log(kind + ' fetch/append failed: ' + e);
        if (kind === 'audio') {
          var n = wrap.querySelector('div');
          if (n) n.textContent = '⚠ 音频拉取失败，当前无声播放';
        }
      });
    };

    msource.addEventListener('sourceopen', function () {
      try {
        var vsb = msource.addSourceBuffer('video/mp4; codecs="' + v.codecs + '"');
        loadTrack(v, 'video', vsb);
        if (audioOk && a) {
          var asb = msource.addSourceBuffer('audio/mp4; codecs="' + a.codecs + '"');
          loadTrack(a, 'audio', asb);
        }
      } catch (e) {
        log('mse error: ' + e);
      }
    });
  };

  tryBuild(0);
})();
""";
}
