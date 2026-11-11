# 12 延伸練習與自我檢核

## 1. 理解檢核

1. 為什麼「用進入路段時的速度走完整條路」不是 FIFO？本專案的速度模型為什麼是？
   <details><summary>答案</summary>
   前者在時段邊界前後進入的兩台車會用不同速度走完全程，晚進入的車可能先到。本專案逐時段積分，同一時刻兩台車速度相同，晚進入者永遠在後面。見 08 章第 1 節、`SpeedProfile.ArrivalTime()`。
   </details>

2. L1 快取為什麼「取樣路徑」而不是「取樣行車時間再內插」？
   <details><summary>答案</summary>
   取樣路徑後以實際出發時刻精確積分，值永遠是某條真實路徑的時間（上界），在取樣點上精確，且取最小值仍單調（FIFO）。內插無法保證誤差上限，也可能破壞 FIFO。見 08 章第 3 節、`src/MealRoute.Core/Travel/PathSampledOracle.cs`。
   </details>

3. 為什麼 L1 判定可行的路線，用 L2 重算後只有休息規則可能翻轉？
   <details><summary>答案</summary>
   L1 是上界，L2 的到達只會更早或相同，時間窗、最晚返回、工時都不會變差。但累計行車時間改變後，越過 4.5 小時的位置可能不同，休息安排就可能不再可行。見 07 章 7.1 節④、`Optimizer.FinalizeExact()`。
   </details>

4. 前向時間餘裕的公式是 `min(總等待, min_j(到 j 為止累計等待 + 時間窗結束_j − 開始服務_j))`。兩個 min 各自防止什麼？
   <details><summary>答案</summary>
   第一項保證延後不會讓回程變晚（只吸收等待，不推遲實際行程）；第二項保證延後不會讓任何一站超過時間窗結束。見 08 章 4.1 節、`RouteEvaluator.Forward()`。
   </details>

5. 為什麼 `ObjectiveValue` 比較行車時間時要量化到微秒？
   <details><summary>答案</summary>
   等價的解因加總順序不同會有 1e-12 秒級的浮點雜訊，直接比較會讓第五層（工時）永遠無法決定勝負。見 07 章第 6 節、`src/MealRoute.Core/Evaluation/ObjectiveValue.cs`。
   </details>

6. 最佳化結果為什麼與執行緒數無關？如果把 `Runs` 預設值改成 `Environment.ProcessorCount` 會發生什麼？
   <details><summary>答案</summary>
   run 的數量固定、每個 run 的種子由主種子衍生、結果依 run 編號寫入陣列並以（目標值, run 編號）選取。若 run 數跟著核心數變，不同機器跑的 run 集合不同，結果就不再可重現。見 08 章第 5 節「決定性與平行」。
   </details>

7. 重新排程沒有寫新的求解器，它怎麼重用 `Optimizer`？
   <details><summary>答案</summary>
   把途中的車改寫成一台普通的 `Vehicle`（出發據點＝回報位置、固定出發、載量＝剩餘餐盒、已累計行車、工時起點），再提供 `canServe` 限制每台車可服務的站。見 07 章第 9 節、`Rescheduler.EnRoute()`。
   </details>

8. 坑 1 中，為什麼 200 組窮舉對照全部一致卻沒抓到出發時刻的 bug？
   <details><summary>答案</summary>
   參考窮舉器採用了相同的出發政策，兩邊對可行的定義一起錯；而且隨機實例很少出現中段長等待。見 11 章坑 1。
   </details>

## 2. 動手練習

### 練習 1：新增「車輛最多服務站數」限制

社工希望每位駕駛一趟最多送 25 站。

<details><summary>提示</summary>

- 資料：`Vehicle`（`src/MealRoute.Core/Model/Stop.cs`）加欄位；專案檔 DTO（`ProjectSerializer` 的 `VehicleDto`）、名單 CSV（`RosterCsv.VehicleHeader`）跟著改。注意 `schemaVersion` 與舊檔相容性。
- 評估：`RouteEvaluator.Evaluate()` 與 `EvaluateCost()` 在容量檢查旁加一個判斷，並在 `ViolationKind` 加一種違反，讓 UI 能說明。
- 最佳化：`AlnsRun.BestInsertion()` 的容量預檢可以順便擋掉，避免浪費評估。
- 參考：`RefFeasibilityChecker` 與 `RefExhaustiveSolver` 也要支援，否則對照測試會不一致。
- 先寫測試：在 `RouteEvaluatorTests` 加一個 red test。
</details>

### 練習 2：補上參考實作獨立性的架構測試

<details><summary>提示</summary>

- 用 `typeof(RefExhaustiveSolver).Assembly.GetTypes()` 找出 `MealRoute.Tests.Reference` 命名空間的型別。
- 對每個方法取 `GetMethodBody().GetILAsByteArray()`，解析 `call`／`callvirt`／`newobj` 指令的 metadata token，用 `Module.ResolveMethod()` 取得目標方法。
- 斷言目標方法的宣告型別不在 `MealRoute.Core.Travel`、`Evaluation`、`Optimization`、`Network` 命名空間。
- 這是 `docs/02-architecture.md` §1.1 承諾但沒有實作的測試（見 09 章第 4 節）。
</details>

### 練習 3：把 `ExactOracle` 的記憶表改成分段淘汰

<details><summary>提示</summary>

- 目前達到 `MaxMemoEntries` 時整批 `Clear()`。
- 簡單做法：維持兩代字典（新、舊），查詢先查新再查舊，新的滿了就把舊的丟掉、新的變舊的。
- 注意執行緒安全（`ConcurrentDictionary`）與決定性（記憶表只影響速度，不影響結果，所以可以放心改）。
- 可用 `Property_exact_oracle_matches_reference_td_dijkstra` 確認結果不變。
</details>

### 練習 4：讓靜態行車的出發時刻只算一次

<details><summary>提示</summary>

- 靜態行車時，延後出發 δ 秒後每站到達剛好晚 δ 秒，前向時間餘裕一次就是精確解，`Settle()` 的迭代是多餘的。
- 可以讓 `ITravelTimeOracle` 提供「是否時間相依」的資訊，或在 `Settle()` 中偵測第一次延後後剩餘可吸收量為 0 就停止（目前已有 0.5 秒門檻，確認實際迭代次數）。
- 用大型範例的 CLI `solve --iterations` 比較每秒迭代數，確認改善；用窮舉性質測試確認結果不變。
</details>

## 3. 延伸學習

| 主題 | 本專案用在哪 | 搜尋關鍵字 |
|---|---|---|
| 帶時間窗的車輛路徑問題 | 整個問題 | `VRPTW`, `Solomon benchmark` |
| 時間相依路徑與 FIFO | `SpeedProfile`、`TimeDependentDijkstra` | `time-dependent shortest path FIFO`, `Ichoua Gendreau Potvin speed model` |
| ALNS | `AlnsRun` | `Adaptive Large Neighborhood Search Ropke Pisinger` |
| SISR | 隨機順序 repair 的 blink | `Slack Induction by String Removals` |
| 顆粒化鄰域 | 路線間 Polish 只看 12 個鄰居 | `granular neighborhood Toth Vigo` |
| 前向時間餘裕 | 出發時刻決策 | `forward time slack Savelsbergh` |
| 性質測試 | `Property.ForAll` | `property-based testing`, `QuickCheck` |
| 迭代版 Tarjan | `StronglyConnectedComponents` | `iterative Tarjan SCC` |
| CSR 圖表示法 | `RoadNetwork` | `compressed sparse row graph` |
| xoshiro256\*\* | `Rng` | `xoshiro256** SplitMix64` |
| CSV 注入 | `CsvText` | `CSV injection OWASP` |
