# MealRoute 教學文件導覽

MealRoute 是一個 Windows 桌面排班工具：匯入街道圖（GeoJSON）與當天送餐名單後，自動排出每台車的路線與時刻，讓社工可以拖曳微調、鎖定站點、在當天有車延誤或故障時重新排程，最後列印每位駕駛的路線單。

**技術棧**：C# / .NET 9、WinUI 3 + Win2D（桌面 UI）、CommunityToolkit.Mvvm（MVVM）、xUnit；路網、最短路徑與最佳化演算法全部自行實作，沒有使用 VRP 或路徑規劃套件。

**文件對應版本**：`commit ce59bfe`（2026-11-11）

> 這套文件是為「日後回頭看的自己」寫的。設計文件 `docs/01-system-analysis.md`、`docs/02-architecture.md` 是開發前的規格與設計；本教學文件則以完成後的程式碼為準，說明「實際怎麼做、為什麼、值得學什麼」。兩者有出入時，以程式碼與本文件為準（差異會在文中註明）。

## 標記說明

- 📄 **依據**：有文件、commit 訊息或開發對話可查證。
- 🔍 **推斷**：從程式碼行為反推，未經確認。

## 閱讀路線

| 路線 | 章節 | 適合 |
|---|---|---|
| 快速掌握（約 30 分鐘） | 00 → 03 → 07 | 想先知道程式碼在哪、怎麼串起來 |
| 完整學習 | 依序 01 → 12 | 想完整理解需求到實作 |
| 想學演算法 | 08（再回頭看 07 的對應小節） | 時間相依最短路徑、快取設計、ALNS |
| 想學測試設計 | 09 | 性質測試、獨立參考實作、窮舉對照 |
| 想看踩過的坑 | 11 | 出發時刻 bug、CI 雜湊失敗、路徑安全性 |

## 章節清單

| 章節 | 檔案 | 狀態 |
|---|---|---|
| 01 需求分析 | [01-requirements.md](01-requirements.md) | 已產生 |
| 02 系統分析 | [02-system-analysis.md](02-system-analysis.md) | 已產生 |
| 03 架構設計 | [03-architecture.md](03-architecture.md) | 已產生 |
| 04 技術選型 | [04-tech-stack.md](04-tech-stack.md) | 已產生 |
| 05 資料庫設計 | — | **略過**：本專案沒有資料庫。資料以 JSON 專案檔儲存，格式說明併入 06 章 |
| 06 輸入輸出介面與資料流 | [06-api-dataflow.md](06-api-dataflow.md) | 已產生（無 HTTP API，改寫檔案格式、CLI 與資料流） |
| 07 程式內容導讀 | [07-code-walkthrough.md](07-code-walkthrough.md) | 已產生 |
| 08 核心演算法 | [08-algorithms.md](08-algorithms.md) | 已產生 |
| 09 測試 | [09-testing.md](09-testing.md) | 已產生 |
| 10 建置、發佈與環境設定 | [10-deployment.md](10-deployment.md) | 已產生 |
| 11 設計取捨與踩坑紀錄 | [11-tradeoffs-pitfalls.md](11-tradeoffs-pitfalls.md) | 已產生 |
| 12 延伸練習與自我檢核 | [12-exercises.md](12-exercises.md) | 已產生 |
| 名詞對照表 | [glossary.md](glossary.md) | 已產生 |

## 修訂紀錄

| 日期 | 對應 commit | 更新內容 |
|---|---|---|
| 2026-11-11 | `ce59bfe` | 初版，涵蓋 M0–M14 與 2026-11-11 外部審查後的修正（出發時刻、必送站交換、測試報告、CLI 警告站名） |
