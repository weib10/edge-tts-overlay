@AGENTS.md

# CLAUDE.md — tts：Claude Code 專屬

<!-- 跨工具規則都在 AGENTS.md（上面那行匯入）；這裡只放 Claude Code 才懂的東西。 -->

## Skills

- 寫／改 AGENTS.md、CLAUDE.md、skill → `mattpocock-skills:writing-for-agents`；整份翻修 → `agents-kit`。

## 驗收分級的 Claude 位置

AGENTS.md「驗收分三級」的第三級（實機截圖與操作）Claude 自己做得到：用 `--ui-test` 啟動 overlay，跑 `scripts/capture_overlay.ps1`——它讀 StatusText、截 DWM 合成後的畫面、用真實滑鼠點展開／收合，並比對前景 hwnd 判定 no-activate。驗完 `--shutdown`，再用 `start.ps1` 開回正常版。

Browser pane 看不到原生 overlay。Codex 的 Computer Use（`@oai/sky`）要它自己的桌面 session 活著才有 native pipe，2026-09-13 那次回報 pipe 不存在。

## 工具怪癖

- 三支 `.ps1` 從 Bash tool 跑：`powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\start.ps1`（process scope，符合紅線）。PowerShell tool 是 5.1，腳本本來就相容，直接 `.\test.ps1` 即可。
