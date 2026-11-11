# 名詞對照表

## 業務術語

| 名詞 | 說明 | 出現位置 |
|---|---|---|
| 站點（Stop） | 一位受送餐的長者；有餐盒數、保溫箱數、服務時間、時間窗 | `src/MealRoute.Core/Model/Stop.cs` |
| 據點（Depot） | 車輛出發與回程的地點，可以不同 | `Vehicle.StartDepot`、`Vehicle.EndDepot` |
| 必送／選送 | 必送站是最高優先目標；選送站有優先權 1–5，5 最重要 | `Stop.Mandatory`、`Stop.Priority` |
| 時間窗 | 允許開始服務的時段 [開始, 結束]；早到要等，晚於結束為不可行 | `Stop.WindowStart`、`Stop.WindowEnd` |
| 餐盒、保溫箱 | 兩種容量維度，各自檢查；全部在出發前裝載 | `Vehicle.MealCapacity`、`Vehicle.BoxCapacity` |
| 行車時間 | 只計路上移動的時間 | `RouteCost.Travel` |
| 工時 | 回到據點時刻 − 出發時刻，含等待、服務、休息 | `RouteCost.Work` |
| 休息規則 | 累計行車超過 4.5 小時前，須在某站服務後休息 30 分鐘 | `BreakRule` |
| 路線單 | 給駕駛的列印資料：站序、時刻、地址、備註 | `src/MealRoute.Core/Reports/RouteSheet.cs` |
| 鎖定 | 鎖定站：原車、原位置；鎖定路線：整條不變 | `LockSet` |
| 重新排程 | 依駕駛回報（位置、已完成站、延誤、故障）重排剩下的站 | `src/MealRoute.Core/Rescheduling/Rescheduler.cs` |
| 未服務原因 | 送不到的站附上的說明（無法抵達、單獨前往也違反、容量或時間已滿） | `Optimizer.UnservedReason()` |

## 專案內的自訂名稱

| 名詞 | 說明 | 出現位置 |
|---|---|---|
| L1 | 取樣路徑快取 `PathSampledOracle`，最佳化內迴圈用 | `src/MealRoute.Core/Travel/PathSampledOracle.cs` |
| L2 | 精確行車時間 `ExactOracle`，最終裁決用 | `src/MealRoute.Core/Travel/ExactOracle.cs` |
| 地點索引 | oracle 使用的整數編號：站點 [0, S)、出發據點 [S, S+V)、回程據點 [S+V, S+2V) | `ProblemInstance` |
| `L0`、`L1`…（吸附警告 ID） | 吸附請求的 ID，即「L + 地點索引」，與上面的 L1 快取無關 | `TravelModel.LocationOf()` |
| 虛擬節點 | 吸附時在路段中間插入的節點 | `Snapper` |
| 小分量 | 最大強連通分量以外的分量，與主路網不互通 | `NetworkAnalysis.SmallComponents` |
| 靜態模式 | 無路網，以歐氏距離為行車時間（Solomon 基準） | `EuclideanOracle`、`PlanningWorkspace.OpenSolomon()` |
| 凍結前綴 | 鎖定站之前（含）不可變動的站數 | `LockSet.FrozenPrefixLength()` |
| 診斷排程 | 不可行路線的模擬：休息放最晚、遇違反繼續，找出時間最早的違反 | `RouteEvaluator.Simulate()` |
| Polish | ALNS 的局部改善：路線內 relocate／2-opt、路線間 relocate／swap、必送站交換 | `AlnsRun.Polish()` |
| 層級（Tier） | repair 的插入順序：必送 6，選送等於優先權 | `AlnsRun.Tier()` |
| 參考實作 | 與正式程式碼互相獨立、用來對照的實作 | `tests/MealRoute.Tests/Reference/` |
| 工作區 | 一個排班日載入的所有資料與模型 | `PlanningWorkspace` |

## 技術縮寫

| 名詞 | 說明 |
|---|---|
| VRPTW | Vehicle Routing Problem with Time Windows，帶時間窗的車輛路徑問題 |
| TD | Time-Dependent，時間相依（行車時間隨出發時刻變化） |
| FIFO | First-In-First-Out，「晚出發不會早到」性質 |
| SCC | Strongly Connected Component，強連通分量 |
| CSR | Compressed Sparse Row，邊依起點連續存放的圖表示法 |
| A\* | 帶啟發函數的最短路徑搜尋；本專案啟發函數為直線距離 ÷ 最高速度 |
| ALNS | Adaptive Large Neighborhood Search，自適應大鄰域搜尋 |
| SA | Simulated Annealing，模擬退火 |
| Regret-k | 插入啟發法：優先插入「最佳與第 k 佳位置成本差最大」的站 |
| Shaw removal | 依相關度（距離、時間窗）移除相近站點的 destroy 運算子 |
| SISR | Slack Induction by String Removals；本專案借用其「blink」隨機略過插入位置的做法 |
| BKS | Best Known Solution，基準實例的最佳已知解 |
| Haversine | 以球面計算兩經緯度點距離的公式（R = 6,371,008.8 m） |
| Equirectangular | 等距圓柱投影；吸附時以小段中點為原點的局部平面 |
| MVVM | Model-View-ViewModel |
| TRX | Visual Studio 測試結果檔格式（XML），CI 用它產生報告 |
| LOD | Level of Detail，縮小時隱藏巷道 |
| Unpackaged | WinUI 3 不打包 MSIX、直接執行 exe 的部署方式 |
