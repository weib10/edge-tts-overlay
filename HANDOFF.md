# Edge TTS Local — Handoff

## 現況

**交付狀態**：正常模式 overlay 運行中（`start.ps1` 啟動、無 `--ui-test`），本機後端 `127.0.0.1:8766` health=`ok`。
自動測試：Python 16/16、C# 18/18、Release 0 warnings / 0 errors。
**repo：public `weib10/edge-tts-overlay`**（2026-09-13 從非 git 狀態初始化並推上去）。
開發歷史封存在 `docs/history/HANDOFF-2026-09-13.md`。

### 2026-09-13（四）：清掉兩個已知 bug，然後開 repo 推上 GitHub

- **斷線不再噴 ASGI traceback**：`_run_cancellable`／`_synthesis_slot` 原本用 `CancelledError` 表示
  「client 走了」，uvicorn 當成未處理例外，每次 skip／replace 都印整段 traceback，會蓋掉
  `PythonServer.LastError` 的啟動診斷。改成自訂的 `ClientGone`，endpoint 收掉回 499。
  注意它必須在 `_synthesize` 的 `except Exception` **之前**重新丟出，否則會被當成上游失敗而重試再 502。
  回歸測試的 uvicorn 因此改回 `--log-level warning`——傳回噪音的話測試輸出會自己抓到。
- **MASTER.md 與程式碼同步**：不只材質描述（acrylic blur → per-pixel alpha，且現在沒有 opaque
  fallback），token 也多處過時，照 XAML 實際值更新：surface tint `#A6182232`、border `#38FFFFFF`、
  control surface `#10FFFFFF`／hover `#26FFFFFF`、圓角 24／16 DIP。
- **git 化**：`.gitignore` 排除 `bin/`、`obj/`、`.venv/`、`__pycache__/` 與整個 `artifacts/`。
  `artifacts/` 要排除是因為 `verify_*.png` 是實機截圖，會連帶拍到桌面上當時開著的東西。
  推之前掃過祕密（沒有，這工具本來就不需要 key）與本機絕對路徑（README 與封存的 HANDOFF 各兩處，已清）。

### 2026-09-13（三）：真連線下 /api/tts 會卡死——本輪最重要的修復

壓測時發現的，比原本在查的記憶體問題嚴重得多：**沒有併發、沒有斷線、一個正常會成功的請求，
也有約 17% 的機率讓 client 永遠收不到回應**（本機實測 7/40，卡住的請求 10 秒 timeout 還不回，
成功的只要 0.33 秒）。server 沒死、`/health` 正常、其他請求還能成功，就是「這一個」回應消失。
使用者端的表現就是按下朗讀之後沒反應、像當機。

根因在 `server/app.py` 的 `_wait_for_disconnect`：它輪詢 `request.is_disconnected()`，而 Starlette
那個 API 每次探測都把 ASGI `receive()` 包進一個微秒級的 `wait_for` 再取消掉——等於每秒 20 次去
中斷 uvicorn 進行中的 `receive()`，這個 race 會把連線卡住，讓合成好的回應永遠沒被 flush 出去。

改成直接等 ASGI channel（`await request.receive()` 直到 `http.disconnect`），不需要 timeout、
不需要輪詢、也就沒有取消 race。同一支重現腳本跑同樣 40 次：**修前 7 次卡死，修後 0 次**。

**為什麼 14 個綠燈測試漏掉它**：`server/test_app.py` 全部用假 Request，`receive()` 只是個普通
coroutine，根本碰不到真的 ASGI channel。所以新增了 `server/test_live.py`——起真的 uvicorn
（隔離 port 8799，`edge_tts` 照樣 mock 掉，不連 Microsoft），做假 Request 做不到的斷言：
25 次序列請求必須全部 200，以及中途斷線的請求不可以吃掉 synthesis slot。已掛進 `test.ps1`。

### 同一輪的記憶體修復

1. **per-segment CTS 洩漏**（`PlaybackCoordinator.PlayItemAsync`）：原本每個 segment 都
   `CreateLinkedTokenSource` 賦值給 `_segmentCts`，只有 finally 裡最後一個被 Dispose，所以 N 段
   文章會在 runToken 上累積 N 個 callback registration。改成建新的之前先 Dispose 前一個；
   `Skip()` 的 Cancel 因此要容忍 `ObjectDisposedException`（UI thread 與 playback thread 會搶）。
2. **Queue 無界成長**：每個 `ReadingItem` 帶 `OriginalText`（上限 20,000 字元）與 `Segments` 副本，
   長期 append 會一路累積。加了 `MaxQueuedItems = 200`，但**只丟已完成的最舊項目**——使用者排進來
   還沒聽的一律保留，即使因此超過上限（這是刻意行為，不是 bug）。
3. **temp mp3**：`Dispose()` 現在會 `CleanupTempFiles()`，不必等下次啟動才清。

### 壓測結果（sonnet 跑的，全部用 fake，沒打 Microsoft）

C# 六個情境全過，沒有未處理例外、沒有死結：4000 segment × 3 輪的長文章 heap 只 +6.3 KB 且中途
採樣不隨進度線性上升（CTS 洩漏修好的樣子）；50000 次零間隔 Skip() heap -96 B、handle -1；
10000 次併發 replace 只花 0.7 秒；append 洪水下 queue 穩在 200，全未讀的 100000 筆則一筆不少地留著。
Python 端 20000 次併發請求全 200、活躍合成數全程 ≤2、`_gate` 精準回到 2；5000 次中途斷線也沒有
slot 洩漏。工具留著：C# 是 `tests\...\Program.cs` 的 `--stress <scenario> <iterations>`，
Python 是 `server/stress_app.py`（要 RSS 數字的話 `pip install psutil`，它是選用的）。

### 仍然算數的舊結論

- **啟動 502 已修**（前一輪）：`/api/voices` 一次瞬時失敗就被放大成永久錯誤畫面。後端補了有界重試，
  前端把取不到清單降級成「就緒 · 語音清單暫時無法取得」，不再吐英文例外。朗讀不依賴語音清單。
- **第三級驗收自己做得到**：`scripts/capture_overlay.ps1`（讀 StatusText、截 DWM 合成後的畫面、
  真實滑鼠點展開／收合、比對前景 hwnd 判定 no-activate）。要點：per-monitor v2 DPI awareness 不能少，
  否則副螢幕會截出一片黑；StatusText 要用 `AutomationId` 找；overlay 要 `--ui-test` 啟動才抓得到；
  所有 `.ps1` 維持 ASCII-only（PowerShell 5.1 用 ANSI 讀沒 BOM 的檔，中文字串會壞）。
  no-activate 已在實機拿到證據（點擊前後前景 hwnd 不變）。
- **Smart App Control**：本機可正常執行；發到別台受保護的機器仍需正規 code signing。
- **429 是本地容量**：只有帶 `X-Edge-Tts-Error: busy` 的 429 是本地 slot 滿，上游失敗一律 502。
- **材質是 per-pixel alpha**（`AllowsTransparency=True`），不是 acrylic blur。
- `smoke.mp3` 是測試 fixture；`artifacts\` 是離屏渲染與驗收截圖的輸出。

## 下一步

1. 白底／高對比背景的透明度仍未驗（前兩輪 overlay 後方剛好是深色頁面），跨螢幕拖曳也未驗。
2. Python 壓測下 RSS 隨疊代等比例微增（~2KB/次），tracemalloc 指向 asyncio 內部結構而非 `server/app.py`，
   但沒深挖到能完全下定論。目前不影響使用。
3. 要發佈到其他受 Smart App Control 保護的電腦時才需要處理 code signing。
4. LICENSE 的版權人寫的是 GitHub handle `weib10`；要換成真實姓名就改那一行。

## 驗證命令

```powershell
.\test.ps1                                                        # 改完必跑。Python（含真連線回歸，不連外網）＋ C# ＋ Release build
powershell -NoProfile -File .\scripts\check_overlay_visuals.ps1   # 改了 MainWindow.xaml／App.xaml 就跑
.\scripts\capture_overlay.ps1 -Prefix verify                      # 第三級：實機截圖＋展開／收合＋no-activate；overlay 先用 --ui-test 開
.\.venv\Scripts\python.exe -m server.stress_app <scenario> <n>    # Python 壓測
dotnet run --project .\tests\EdgeTtsOverlay.Tests\EdgeTtsOverlay.Tests.csproj --no-restore -- --stress <scenario> <n>
```

**跑 `test.ps1` 前先確認 overlay 沒開著**，否則 Release build 會因 DLL 被鎖而失敗；關法只有 `--shutdown`。

只在改後端併發或聲音清單時才跑（會真的打 Microsoft，見 AGENTS.md 紅線第三條）：

```powershell
.\.venv\Scripts\python.exe .\scripts\online_smoke.py
.\.venv\Scripts\python.exe .\scripts\repro_429.py                 # 對跑中的 8766 打
```
