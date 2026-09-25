# Edge TTS Local — Handoff

## 現況

**交付狀態**：正常模式 overlay 運行中（`start.ps1` 啟動、無 `--ui-test`），本機後端 `127.0.0.1:8766` health=`ok`。
自動測試：Python 16/16、C# 18/18、Release 0 warnings / 0 errors。
repo 是 public `weib10/edge-tts-overlay`。開發歷史封存在 `docs/history/HANDOFF-2026-09-13.md`。

### 2026-09-20：念法搬進後端（`POST /api/prepare`）

使用者想要「英文用英文的人聲念、不是字的名詞念字母」，先用在 machine-monitor 面板的 Read aloud 上試。
新增 `server/reading.py`：先照搬 overlay 的 `TextProcessor`／`TextSegmenter`（相容模式，跟 C# 比過 5,066 筆 0 差異，工具在 `scripts/reading_parity/`），再疊兩條新規則——三個字以上的英文段落換英文聲音（呼叫端給 `english_voice`），`GPU`／`vLLM`／`k8s`／`npm` 這類非單字拆成字母念（`spell`，術語字典優先；字典裡對應到自己的詞不拆）。
`POST /api/prepare` 回 `[{text, voice, lang}]`，只在本機切文字、不連網。測試：`server/test_reading.py`（已加進 `test.ps1`）＋`test_app.py` 兩條；Python 38/38。**C# 沒動**（這包 09-14 改版還沒 commit，不想纏在一起），所以 overlay 還是舊念法，這輪也沒有重跑 C# 測試與 Release build。
驗證：machine-monitor 面板經新版後端念中英混合的例子，中文段 HsiaoChen、英文段 Ava；英文聲音的線上合成只用固定例句 “This is a test sentence.” 跑一次（11,232 bytes、1.87 秒）。

### 2026-09-14：UI／UX 整個重做（液態玻璃）

使用者說「UIUX 不好用，UI 想要像 iOS 26 那種半透明液態玻璃，現在還差很多」。這一輪重寫了
`App.xaml`（設計系統）、`MainWindow.xaml`／`.xaml.cs`（版面與互動）與兩個對話框。

**材質**：原本只有「一塊 65% 深色圓角＋1px 白邊」，看起來是灰板子。現在是疊層——底色斜向漸層、
內縮 6 DIP 的玻璃芯、1.5 DIP 鏡面亮邊、亮邊內側的暗髮絲線、上下內反光、上緣一道反光條，
外加一個帶模糊的空心環當陰影（見下面第二輪）。視窗因此比玻璃大 18 DIP 一圈當陰影留白，
`Window.Background` 設 `{x:Null}` 讓那一圈不吃點擊。各層的值與理由在
`design-system/edge-tts-overlay/MASTER.md`。

**沒有真模糊，而且是測過才放棄的**：`SetWindowCompositionAttribute` 的 `ACCENT_ENABLE_ACRYLICBLURBEHIND`
在 Windows 11 26200 對這個視窗完全沒作用——layered（per-pixel alpha）視窗收不到 DWM accent，
截圖前後一模一樣。官方的 `DWMWA_SYSTEMBACKDROP_TYPE` 要 `AllowsTransparency="False"`，代價是
失去去鋸齒的膠囊輪廓與柔和陰影、圓角被系統壓到 ~8 DIP。兩個都比沒有模糊更糟，所以維持 per-pixel alpha。
實驗碼已移除，結論寫在 MASTER.md「Why there is no real backdrop blur」。

**UX**（原本的問題：看不出讀到哪、要調語速得開會暫停朗讀的設定視窗、七顆同樣大小的按鈕擠成兩列、
空佇列只是一個黑框）：

- 收合列多了跳下一句，以及一條 3 DIP 進度條（第幾句／共幾句）。
- 展開區新增語速 −／＋ 與快速換聲音（只列目前語言的聲音），改它們不會中斷朗讀。
- 佇列有自己的 item template（標題＋狀態）、選取樣式，空的時候直接寫「按哪個鍵可以塞東西進來」。
- 動作列改成一顆主要按鈕＋五顆圖示鈕；播放／移除在沒選取時 disabled。
- 最下面一行列出目前這組快捷鍵（讀的是設定值，不是寫死字串）。
- 設定與編輯視窗換成同一套控制項樣式與深色標題列（`Services/DarkTitleBar.cs`；不叫它 Windows 11
  會配一條白色標題列）。

**第二輪（同日，使用者回饋「外面禿出去的那個很醜、邊框太突兀」）**：

- 陰影原本是三圈疊出來的實心環，步階之間有硬邊，在白底上看起來像膠囊外面又多了一個框。改成
  **一個空心環 + 真的 `DropShadowEffect`**：環的實線壓在膠囊邊上被亮邊蓋住，只有模糊散出去。
  實心投影體不能用（陰影會從半透明玻璃底下透上來把整片壓暗）。代價量過了：離屏渲染一次展開動畫，
  沒特效 3.8 ms/frame、有特效 8.6 ms/frame，還在 60fps 預算內，而且只有改變大小時才付這個成本。
- 亮邊整體收斂（頂角 `#E6` → `#8C`，側邊 `#24` → `#14`），bevel 與上緣反光條跟著降。
- 多行 TextBox 的文字原本被垂直置中（樣板寫死 `VerticalAlignment="Center"`），改成跟著
  `VerticalContentAlignment`。
- `capture_overlay.ps1` 的 `Get-OverlayWindow` 只用 pid 找視窗，滑鼠停在按鈕上時會抓到 tooltip 視窗
  （實際踩到了）。加上 `ControlType.Window` 條件。

**第三輪：0-context subagent 做 code review 之後的修正**（都已實機驗過）：

- **舊版存的視窗位置會讓膠囊掉出工作區**。舊版視窗 360×76 沒有陰影留白，存的 Left 就是玻璃的左緣；
  新版直接沿用會讓玻璃右邊超出 34 DIP（含停止與展開）。加了 `ClampIntoWorkArea()`，夾的是玻璃的
  矩形不是視窗的。實測：故意寫入舊版位置啟動，玻璃右緣正好落在工作區邊界。
- **展開往上讓位之後，收合沒有還回去**。每展開一次就往上跳一段（這個版本是 352 DIP），而且
  `OnClosing` 會把跳完的位置當成新家存起來。現在記住展開前的 Top，收合時還原（使用者中途拖過就不還）。
  夾的條件也改用玻璃的邊，原本會多讓 18 DIP。實測：1938 → 1446 → 1938（physical）。
- **後端起不來時 `PopulateVoices()` 不會跑**，留下一個看得到卻點不出東西的空下拉，而且之後從設定
  視窗改完聲音也救不回來。改成不管 try 成不成功都跑，並在 `OpenSettings` 補抓一次清單。
- **語速／聲音是「按一下就寫檔」**，`AppSettings.Save()` 沒有 try/catch，檔案被佔用時一個點擊就會
  讓整篇朗讀跟著死。加了 `TrySave()`，失敗只寫在狀態列。
- 點佇列原本會走 `UpdateUi()`，把「剪貼簿沒有文字」這種一次性訊息洗掉；拆出 `UpdateSelectionActions()`。
- `SyncHints()` 會蓋掉正在朗讀的那一行（目前被呼叫順序遮住，但名字不該做這件事）。
- 空的 `Voice` 會讓「同語言」篩選退化成整份清單、null 會直接炸；改在 `AppSettings.Load()` 正規化。
- 拖完視窗就把位置寫進 `_settings`，不要等 `OnClosing` 才抄（非正常結束會吃掉那次移動）。
- ComboBoxItem 少了 `IsSelected` 樣式：滑過別人時看不出目前用的是哪個聲音。
- ScrollBar 樣板的 `IsDirectionReversed` 寫死 True，橫向捲軸會變成拖曳方向相反（目前沒有橫向捲軸會
  出現，屬於埋著的）。
- `capture_overlay.ps1` 兩個 bug：`Get-OverlayWindow` 只用 pid 找，滑鼠停在按鈕上時會抓到 tooltip
  視窗（實際踩到）；`Save-OverlayShot` 固定加 70px 截圖範圍，視窗貼齊螢幕角落時會超出虛擬桌面而
  `CopyFromScreen` 失敗——現在夾進虛擬桌面。
- `check_overlay_visuals.ps1` 的陰影只驗正下方那一點（陰影本來就往下打），加驗側邊。

**沒有照 review 改的一項**：陰影投影體（`#A6000512`）在幾何上被亮邊蓋住，但那些層都是半透明的，
所以它確實會透上來壓掉一點鏡面亮度。亮邊的值本來就是在它存在的情況下調的，維持現狀；MASTER.md
已改成照實描述，並註明動投影體的 alpha 就要重看實機截圖（離屏檢查看不到亮邊）。

**踩到的坑**：用這套 ComboBox 樣板時只設 `DisplayMemberPath` 會讓 `SelectionBoxItemTemplate` 是 null，
收合狀態就印出物件的 ToString（實測確認）。兩個 ComboBox 都改設 `ItemTemplate`。

**自動化 UI 測試的血淚**：用座標點擊時，如果有 modal dialog 已經開著，座標會落在 dialog 上而不是
overlay 上。這一輪因此誤觸設定視窗並按到「儲存」，把「登入 Windows 時自動啟動」打開、寫了
`HKCU\...\Run\EdgeTtsLocal`（已還原：設定改回 false、登錄值刪除）。之後要自動點擊，先確認沒有
dialog 開著，或改用 UI Automation 的 InvokePattern（不吃座標）。

**驗收**：三級都做了。`check_overlay_visuals.ps1` 已配合新尺寸更新，並多驗一條「膠囊外緣要有陰影但不能過重」，
展開版還會塞三筆假佇列資料進去，這樣 PNG 看得到真正的 item template（不必連線合成）。實機截圖涵蓋
深色桌面與**純白背景**（用一個蓋滿虛擬桌面的白色 form 當背景，`artifacts\final_light_*.png`）——
白底的可讀性是這一輪唯一真正的取捨：沒有模糊可用，透明度要讓路給對比，所以玻璃芯那一塊偏濃。
真滑鼠點擊驗過展開／收合、語速 +10% 兩次（1.0× → 1.2×）、聲音下拉打得開，全程前景 hwnd 不變。

**真的朗讀過**：使用者授權測試後，用編輯視窗打一段固定的非敏感測試句（不碰剪貼簿）按「取代並朗讀」，
實測狀態列 `朗讀 1/1` → `已完成`、目前句卡片、佇列列（標題＋狀態）、進度條都正確。編輯視窗也在
把文字換掉之後才截圖，所以有外觀證據且沒有拍到剪貼簿內容。

### 仍然算數的舊結論

- **`/api/tts` 卡死已修**（2026-09-13）：`_wait_for_disconnect` 改成直接等 ASGI channel，不再輪詢
  `request.is_disconnected()`。同一支重現腳本 40 次由「7 次卡死」變成 0 次。`server/test_live.py`
  起真的 uvicorn 來守住這件事（`edge_tts` 照樣 mock，不連 Microsoft）。
- **斷線不再噴 ASGI traceback**：自訂 `ClientGone`，endpoint 收掉回 499；它必須在 `_synthesize` 的
  `except Exception` 之前重新丟出，否則會被當成上游失敗而重試再 502。
- **記憶體**：per-segment CTS 不再累積 registration；queue 上限 200 但只丟已完成的；`Dispose()` 會清 temp mp3。
- **啟動 502 已修**：`/api/voices` 一次瞬時失敗不再放大成永久錯誤畫面；取不到清單只降級狀態列。
- **429 是本地容量**：只有帶 `X-Edge-Tts-Error: busy` 的 429 是本地 slot 滿，上游失敗一律 502。
- **Smart App Control**：本機可正常執行；發到別台受保護的機器仍需正規 code signing。
- `smoke.mp3` 是測試 fixture；`artifacts\` 是離屏渲染與驗收截圖的輸出（整個 gitignore）。

## 下一步

0. **overlay 改成問 `/api/prepare`**：`MainWindow.AddText` 與編輯視窗的預覽改呼叫後端，`ReadingItem.Segments` 改成帶聲音，`PlaybackCoordinator` 每段用自己的聲音合成；`AppSettings` 加英文聲音與拆字母兩個設定。做完 C# 的 `TextProcessor`／`TextSegmenter` 與 `scripts/reading_parity/` 就可以刪。先等 09-14 那包改版 commit。
1. **白底時想再透一點，只有一條路**：桌面內容沒有模糊可擋，所以現在靠加濃玻璃芯換可讀性。要兩者兼得
   就得讓 overlay 知道背後是亮是暗——可行做法是 BitBlt 視窗**外圈**幾條細長條（那一圈沒被自己蓋住，
   不會拍到自己），取平均亮度後切換亮／暗兩套 palette。代價是這個工具要開始讀螢幕，值不值得由使用者決定。
2. 跨螢幕拖曳仍未驗（目前 overlay 在副螢幕，沒有實測拖到主螢幕的 DPI 變化）。
3. Python 壓測下 RSS 隨疊代等比例微增（~2KB/次），tracemalloc 指向 asyncio 內部而非 `server/app.py`，
   沒深挖到能下定論。目前不影響使用。
4. 要發佈到其他受 Smart App Control 保護的電腦時才需要處理 code signing。
5. LICENSE 的版權人寫的是 GitHub handle `weib10`；要換成真實姓名就改那一行。

## 驗證命令

```powershell
.\test.ps1                                                        # 改完必跑。Python（含真連線回歸，不連外網）＋ C# ＋ Release build
powershell -NoProfile -File .\scripts\check_overlay_visuals.ps1   # 改了 MainWindow.xaml／App.xaml 就跑
.\scripts\capture_overlay.ps1 -OutputDirectory .\artifacts -Prefix verify   # 第三級：實機截圖＋展開／收合＋no-activate
.\.venv\Scripts\python.exe -m server.stress_app <scenario> <n>    # Python 壓測
dotnet run --project .\tests\EdgeTtsOverlay.Tests\EdgeTtsOverlay.Tests.csproj --no-restore -- --stress <scenario> <n>
```

`capture_overlay.ps1` 要先用 `--ui-test` 啟動 overlay（正常模式有 TOOLWINDOW，UI Automation 看不到），
驗完 `--shutdown` 再用 `start.ps1` 開回正常版。從 Bash tool 跑它要明寫 `-OutputDirectory`：
`-File` 啟動時 `$PSScriptRoot` 在參數預設值裡是空的。

**跑 `test.ps1` 前先確認 overlay 沒開著**，否則 Release build 會因 DLL 被鎖而失敗；關法只有 `--shutdown`。

只在改後端併發或聲音清單時才跑（會真的打 Microsoft，見 AGENTS.md 紅線第三條）：

```powershell
.\.venv\Scripts\python.exe .\scripts\online_smoke.py
.\.venv\Scripts\python.exe .\scripts\repro_429.py                 # 對跑中的 8766 打
```
