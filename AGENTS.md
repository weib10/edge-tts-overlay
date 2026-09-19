# AGENTS.md — tts

Edge TTS Local：Windows 置頂朗讀 overlay（WPF，`src/`）＋本機 FastAPI 包 `edge-tts`（`server/`），聲音由 Microsoft Edge 線上服務合成。它只在使用者按快捷鍵時讀剪貼簿；**不是**剪貼簿監聽器，不跑本機 TTS 模型，不需要 API key。安裝、快捷鍵、API 端點與資料位置見 `README.md`。

## 紅線

- **不動全機安全原則**：Smart App Control、`Set-ExecutionPolicy` 的機器／使用者 scope、Code Integrity 一律不碰。2026-09-13 新建的 DLL 被 Smart App Control 擋下（Code Integrity 事件 3033／3077），使用者決定保留防護。改做：PowerShell 只用 process scope（`powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\start.ps1`）；要發到別台受保護的機器走正規 code signing。
- **關 overlay 只用 `--shutdown`**（指令在 README），不 `taskkill`／`Stop-Process` 整批 `dotnet`。其他專案的 dotnet 與 GPU 工作跟它同名；2026-09-13 一個舊 PID 不理 `--shutdown`，也是先核對完整 command line 才只殺那一個。
- **線上合成只送固定的非敏感例句，而且只在必要時跑。** `scripts/online_smoke.py` 與 `repro_429*.py` 真的打 Microsoft 服務；剪貼簿內容是使用者的私人文字，測試、log、HANDOFF 都不得引用。已通過的 smoke 不為「再確認一次」重跑。

## 進度真相

`HANDOFF.md`（現況／下一步），完成一件事的當下更新。**用取代不用追加**：2026-09-13 盤點時「現況」已疊到二十多條帶日期的段落，接手要整節讀完才知道哪條還算數。時間軸交給 `git log`，整段歷史放 `docs/history/`。寫法正本在 `..\AGENTS.md`「工作習慣」。

2026-09-13 起本專案是 git，remote 是公開的 `weib10/edge-tts-overlay`。**推上去的東西是公開的**：`artifacts/` 已整個 gitignore（`verify_*.png` 是實機截圖，會連帶拍到桌面上當時開著的東西），文件裡不要寫本機絕對路徑。

UI 材質與 token 的正本是 `design-system/edge-tts-overlay/MASTER.md`，改了 XAML 的材質或 token 就同步它。

**念法的正本是 `server/reading.py`**（`POST /api/prepare`：閱讀模式、術語字典、切句、縮寫拆成字母念、英文段落換英文聲音）。overlay 目前還用自己的 C# 那份（`TextProcessor.cs`／`TextSegmenter.cs`），`reading.py` 的相容模式（`spell=False`、不給英文聲音）必須跟它一字不差——改了任一邊就跑 `.venv\Scripts\python.exe scripts\reading_parity\compare.py`（拿 C# 原檔跑同一批假輸入逐句比對，要 .NET 8 SDK）。Python 的 `\w`、`\b` 不能照抄 C#：兩邊對「字元」的定義不同。

## 環境怪癖

- Python 在 `.venv\Scripts\python.exe`（3.12），沒有全域安裝；.NET 8 SDK。三支 `.ps1` 相容 Windows PowerShell 5.1 並檢查 native exit code，改它們維持這兩點。
- 後端通常是 overlay 啟動時自己從 `.venv` 拉起 `uvicorn server.app:app` 在 `127.0.0.1:8766`；**8766 已有健康服務就沿用、退出時不殺它**。machine-monitor 的面板（Read aloud）也會單獨起它（服務名 `tts-backend`，帶 `--no-access-log`，不開 overlay）。「服務在跑」不代表是這個 overlay 的，查 listener PID 再下結論。`start.ps1` 開的 GUI 行程是脫離的，session 結束不會自己收；收尾 `--shutdown`，不要留給下一個 session 猜是誰開的。
- **429 是本地容量，不是 Microsoft**：只有帶 `X-Edge-Tts-Error: busy` 的 429 是本地 slot 滿（client 自己退避兩次），上游失敗一律轉成 502。2026-09-13 使用者回報的 429 曾被誤判為上游限流，真因是舊的 50 ms semaphore。
- 正常模式是 `WS_EX_NOACTIVATE`＋`TOOLWINDOW`：不進工作列、視窗列舉與截圖工具看不到。**視覺驗收用 `--ui-test` 啟動**（進工作列、可被抓到，其餘行為相同），驗收完 `--shutdown` 關掉再用 `start.ps1` 開正常版。
- `smoke.mp3` 是測試 fixture（C# 測試的 `--decode`／`--audio-restart` 用），不是垃圾；重產跑 `online_smoke.py`。`artifacts/` 是離屏渲染輸出的 PNG。

## 慣例

- **驗收分三級，做到哪級寫哪級，沒做到的不宣稱。** `test.ps1` 自動測試 → `scripts/check_overlay_visuals.ps1` 離屏渲染（只證明 alpha 與版面）→ 實機截圖與操作（才能證明透明度、拖曳、no-activate）。2026-09-13 多輪實機工具抓不到畫面，HANDOFF 照實寫「未驗收」，這是本專案的寫法。
- 回覆、HANDOFF 與 UI 字串都是 zh-tw。
- 本專案不吃 GPU（合成在雲端）；檔尾的 gpu-rule 是全機固定件，留著不改。

## 驗證

```powershell
.\test.ps1                                                    # Python unittest（edge_tts 已 mock，不連網）＋ C# 測試 ＋ Release build；改完必跑
powershell -NoProfile -File .\scripts\check_overlay_visuals.ps1   # 改了 MainWindow.xaml／App.xaml 就跑，PNG 落在 artifacts\
```

連網的 `online_smoke.py` 與對跑中 8766 打的 `repro_429.py` 只在改後端併發或聲音清單時跑（見紅線第三條）；完整指令在 HANDOFF「驗證命令」。

<!-- gpu-rule -->
## 要用 GPU：先跟佇列要

全機只有一張卡，各專案的 session 看不到對方在跑什麼。**規則正本在 `..\AGENTS.md`（跨專案），必讀。**

- 有 argv 的（腳本、訓練、批次推論）→ `gq add --name … --project … --vram … --est … --window … -- <argv>`
- **自己要用 ComfyUI／Ollama 一段時間 → 先要 lease，拿到才可以動手**：

  ```
  gq lease comfyui --est 30 --project <本專案> --by "claude:<本專案>"   # exit 0 才是你的
  gq lease-check <id>    # 0 還握著 / 75 還在等 / 1 被收回，立刻停手
  gq release <id>        # 做完馬上還
  ```

  **ComfyUI 與 Ollama 沒有「5 分鐘以下直接跑」的豁免**：運算在它們的行程裡，不走佇列的話
  沒有人看得出是誰要的，也沒辦法幫你延後或在被遊戲搶佔後補跑。

- 停 daemon 只准 `gq daemon stop`，不要 `taskkill /T`。儀表板：http://127.0.0.1:8765/
