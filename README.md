# WatchPartyExtension

gogh 观影派对（Watch Party）扩展 mod。当前版本 **v0.1.0：解除输入框网页加载限制**。

## 功能

游戏原版会在网页输入框提交时校验链接白名单（仅允许 YouTube 域名），非 YouTube 链接直接忽略、页面打不开。装上本 mod 后：

- **Setup 输入框**（"输入想观看的 YouTube 链接"）回车，可以打开任意 `http(s)` 链接（如 B 站）；
- 场景内地址栏 + **"移动"按钮**同样放行；
- 裸域名会自动补 `https://`（`bilibili.com/video/...` 直接输也能开）。

**范围限制（有意为之）**：只放行"输入框加载网页"这一条路径。"开始共享 / 加入 / 重新同步"等 Watch Party 同步链路仍是原版校验（非 YouTube 链接无法发起共享），转发同步是后续版本的目标。

## 机制

原版的输入校验全部收口在 `WatchPartyYoutubeUrlChecker` 的三个方法（`TryExtractVideoInfo` / `BuildCanonicalUrl` / `IsValidYoutubeVideoUrl`）。本 mod 用 Harmony 在"输入框提交"的调用栈内临时放行非 YouTube 的 http(s) URL，调用栈外（共享/加入/收包等）保持原版行为不变。放行后由原版流程自行完成 URL 打开，mod 不从托管侧调用游戏方法。

背景调查（网络层校验分布、vanilla 行为矩阵等）见 `D:/gogh_dev/docs/gogh-观影派对转发mod可行性调查.md`。

## 安装

依赖 [MelonLoader](https://github.com/LavaGang/MelonLoader)（0.6+）。把 `WatchPartyExtension.dll` 放进游戏目录的 `Mods/` 后启动游戏即可。

## 构建

```bash
dotnet build -c Release
# 产物：WatchPartyExtension/bin/Release/net6.0/WatchPartyExtension.dll
```
