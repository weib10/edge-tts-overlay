# Edge TTS Local

Windows 置頂文字朗讀 overlay。它只在你按快捷鍵時讀取剪貼簿，不監聽剪貼簿，也不需要 API key。語音由 Microsoft Edge 的線上 TTS 服務產生，因此合成時需要網路。

## 安裝與啟動

需要 Python 3.12 與 .NET 8 SDK。在 PowerShell 執行：

```powershell
git clone https://github.com/weib10/edge-tts-overlay.git
cd edge-tts-overlay
.\setup.ps1
.\start.ps1
```

若啟動回報 `FileLoadException 0x800711C7`，請到「Windows 安全性 → 應用程式與瀏覽器控制 → 智慧型應用程式控制」查看狀態。Smart App Control 沒有單一 app 例外；要保留此防護並發佈本機建置，需使用有效 CA code-signing certificate 簽署。請勿為本專案關閉全機安全原則。

程式會自行啟動 localhost FastAPI 服務。API 文件在 `http://127.0.0.1:8766/docs`，健康檢查為 `GET /health`，聲音清單為 `GET /api/voices`，合成為 `POST /api/tts`，念法為 `POST /api/prepare`（文字 → 一句一句、每句標好用哪個聲音：閱讀模式、術語字典、縮寫拆成字母念、英文段落換英文聲音；規則在 `server/reading.py`，目前 overlay 自己還沒用它）。若 8766 已有健康的服務，overlay 會沿用它，退出時不會終止該外部服務。

`start.ps1` 會先建置 Release，再由已安裝的 `dotnet.exe` 啟動 DLL，並等待 localhost health 成功；若 Windows 應用程式控制原則封鎖組件，腳本會回傳失敗，而不會誤報啟動成功。它不會留下 console 視窗。自動化或疑難排解時可執行下列命令，讓現有實例經正常清理流程退出：

```powershell
dotnet .\src\EdgeTtsOverlay\bin\Release\net8.0-windows\EdgeTtsOverlay.dll --shutdown
```

## 操作

- `Ctrl+Alt+T`：顯示／隱藏 overlay
- `Ctrl+Alt+R`：以剪貼簿文字取代目前朗讀
- `Ctrl+Alt+A`：把剪貼簿文字追加到佇列
- `Ctrl+Alt+P`：暫停／繼續
- `Ctrl+Alt+S`：停止並把目前文章回到開頭

收合狀態就有播放／暫停、跳下一句、停止，以及一條顯示「這篇讀到第幾句」的進度條。點最右邊的箭頭展開，會多出目前句子、語速加減、快速換聲音、佇列與動作列；語速與聲音在這裡改不會中斷朗讀（開設定視窗則會先暫停）。雙擊佇列文章可查看、修改原文與實際送讀預覽。

設定視窗可選聲音、速度、音量、音高、閱讀/完整模式、術語字典與快捷鍵。展開區的聲音下拉只列出目前語言的聲音，要跨語言請到設定視窗。預設優先使用 `zh-TW-HsiaoChenNeural`；若服務清單沒有它，程式會要求你到設定選擇，不會暗中換聲音。

閱讀模式會保留標題、正文、清單與行內術語；Markdown link 只念標題，路徑只念檔名，裸 URL 與程式碼塊會說明略過。每篇上限 20,000 字元，超出時會提示。

## 測試

```powershell
.\test.ps1
```

真實線上 smoke（會合成固定的非敏感例句並在專案根目錄建立 `smoke.mp3`）：

```powershell
.\.venv\Scripts\python.exe .\scripts\online_smoke.py
dotnet run --project .\tests\EdgeTtsOverlay.Tests --no-restore -- --decode .\smoke.mp3
```

## 資料與限制

程式只保存聲音、速度、閱讀模式、術語、快捷鍵、視窗位置及自動啟動選項，位置在 `%LOCALAPPDATA%\EdgeTtsLocal\settings.json`。文章與佇列不會跨執行保存。合成音訊暫存在 `%TEMP%\EdgeTtsLocal`，播放、取消及下次啟動時清理。單段失敗時停在該篇，可按「重試」或「跳過」。

## 授權

MIT，見 [LICENSE](LICENSE)。

本專案本身不含語音模型：聲音由 Microsoft Edge 的線上 TTS 服務合成，該服務不在此授權範圍內，使用時請自行遵守 Microsoft 的條款。
