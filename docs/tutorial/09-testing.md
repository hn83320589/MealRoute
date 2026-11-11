# 09 測試

## 1. 測試策略

本專案的難點是：最佳化結果「對不對」很難直接判斷。沒有人能手算 200 站的最佳路線。所以測試策略的核心是**用另一個獨立的實作來對照**，而不是只寫「輸入 X 應得 Y」。

| 層級 | 測什麼 | 例子 |
|---|---|---|
| 單元測試 | 明確的規則與邊界 | 時間窗等待、休息不插入、字典序比較、CSV 防公式、路徑白名單 |
| 性質測試（property test） | 對隨機輸入都應成立的性質 | L1 是上界且 FIFO、增量預覽 = 完整重算、最佳化結果通過參考檢查器 |
| 參考實作對照 | 正式實作與獨立寫法結果一致 | SCC（Tarjan vs Kosaraju）、TD 最短路徑、窮舉最佳解 |
| ViewModel 測試 | UI 流程，不啟動視窗 | 點選聯動、拖曳延遲 < 50 ms、取消最佳化、重新排程流程 |
| 端對端 | small 範例走完整流程 | `tests/MealRoute.Tests/Integration/SamplePipelineTests.cs` |
| 基準（不在 CI） | 效能與 Solomon | `Category=Benchmark`、CLI `solomon` |

## 2. 框架與工具

- **xUnit 2.9**：測試框架。
- **自製性質測試執行器** `Property.ForAll(baseSeed, cases, check)`（`tests/MealRoute.Tests/Infrastructure/Property.cs`）：每個案例的種子是 `baseSeed + i`，失敗時訊息印出種子，用 `Property.Replay(seed, ...)` 重現。
- **`FakeTimeProvider`**：測試時間上限與「現在時刻」，不必真的等待。
- **ViewModel 測試替身**：`FakeFiles`、`FakeMessages`、`FakePrint`、`InlineDispatcher`（`tests/MealRoute.Tests/ViewModels/ViewModelTestKit.cs`、`src/MealRoute.App.ViewModels/Services/Services.cs`）。

## 3. 執行指令

```powershell
# 全部（不含效能基準），與 CI 相同
dotnet test tests/MealRoute.Tests -c Release --filter "Category!=Benchmark"

# 只跑某個類別
dotnet test tests/MealRoute.Tests -c Release --filter "FullyQualifiedName~RouteEvaluatorTests"

# 效能基準（L1 建置時間）
dotnet test tests/MealRoute.Tests -c Release --filter "Category=Benchmark"

# Solomon 基準（約 90 分鐘）
dotnet run --project tools/MealRoute.Cli -c Release -- solomon --seconds 180 --out benchmarks/solomon/RESULTS.md
```

目前 254 個測試，本機約 15–30 秒。

## 4. 獨立參考實作

`tests/MealRoute.Tests/Reference/` 下的程式碼**只讀取 Core 的純資料型別**（`Stop`、`Vehicle`、`Solution`、`GeoPoint` 等 record），計算全部自己寫，而且**刻意用不同的演算法**。用同一個演算法再寫一次，兩邊會犯同樣的錯。

| 參考實作 | 刻意不同之處 | 對照的正式實作 |
|---|---|---|
| `RefHaversine`、`RefProjection` | 獨立公式 | `Haversine`、`PolylineMath` |
| `RefSccKosaraju` | Kosaraju（正式是迭代 Tarjan） | `StronglyConnectedComponents` |
| `RefDijkstra.StaticDistances` | O(V²) 陣列版 | 速度恆定時的 TD 結果 |
| `RefTdIntegrator` | **decimal** 算術、「距離為主」逐時段積分（正式是 double、「時間為主」） | `SpeedProfile.ArrivalTime()` |
| `RefDijkstra.TimeDependent` | O(V²) TD Dijkstra，每條邊用參考積分器 | `TimeDependentDijkstra`、`ExactOracle` |
| `RefFeasibilityChecker` | 只讀實例與解（出發時刻、休息位置），以參考行車函數重算一切 | 最佳化、重新排程、路線單 |
| `RefExhaustiveSolver` | 每車 DFS 枚舉站序前綴＋休息分支，出發時刻用**反向「最晚開始」**計算（正式是前向時間餘裕），再以子集 DP 合併各車 | `Optimizer` |

> ⚠️ **落差**：`docs/02-architecture.md` §1.1 寫「以一個架構測試（反射掃描 `Reference` 命名空間的 IL 呼叫目標）確保它沒呼叫 Core 的演算法命名空間」。**這個測試目前不存在**，獨立性只靠慣例維持（`tests/MealRoute.Tests/Reference/` 下的檔案只 `using` `MealRoute.Core.Model` 與 `MealRoute.Core.Geo`，且只用到其中的資料型別）。見第 7 節建議。

## 5. 代表性測試走讀

### 5.1 單元測試：中段等待被延後出發吸收

```csharp
// tests/MealRoute.Tests/Evaluation/RouteEvaluatorTests.cs
[Fact]
public void Departure_is_delayed_to_absorb_mid_route_waiting_which_makes_a_tight_work_limit_feasible()
{
    // depot 0 -> A 100 (open until 5000) -> B 200 (opens 5000). Leaving at 0 waits 4800 s at B
    // (work 5200 > 1000). Leaving at 4800 waits nowhere: work 400.
    var inst = Instance(new[] { Stop("A", 100, 0, 5000), Stop("B", 200, 5000, 5100) }, new[] { Vehicle(maxWork: 1000) });
    var r = Eval(inst, 0, 1);
    Assert.True(r.IsFeasible, r.FirstViolation?.Message);
    Assert.Equal(4800, r.Departure, 6);
    Assert.Equal(400, r.WorkSeconds, 6);

    var c = new RouteEvaluator(inst, Oracle(inst)).EvaluateCost(0, new[] { 0, 1 });   // ①
    Assert.True(c.Feasible);
    Assert.Equal(4800, c.Departure, 6);
}
```

設計重點：

- **一維座標、速度 1 m/s**（`tests/MealRoute.Tests/Evaluation/TestInstances.cs`）：站點放在數線上，行車時間等於距離，所有預期值都能心算。註解直接寫出推導。
- **測試名稱是一句完整的規格**：讀測試清單就是讀需求。
- ① 同一個情境也檢查 `EvaluateCost()`（快速路徑），確保兩條路徑結論一致。
- `Assert.True(r.IsFeasible, r.FirstViolation?.Message)`：失敗時訊息直接告訴你違反了什麼，不必除錯。

這個測試是在外部審查後補上的 red test：舊版評估器在這裡會判成不可行。

### 5.2 性質測試：最佳化結果與窮舉最佳解一致

```csharp
// tests/MealRoute.Tests/Optimization/OptimizerPropertyTests.cs
for (var i = 0; i < count; i++)
{
    var seed = 52_000 + i;
    var inst = TwoShiftInstance(new Random(seed));                     // ①
    var exhaustive = RefExhaustiveSolver.Solve(inst);
    var o = OptimizerTests.Optimize(inst, new OptimizerOptions { Seed = (ulong)seed, MaxIterations = 4_000, Runs = 4 }).Objective;
    // ... 前三層相同、行車差 ≤ 1e-6、工時差 ≤ 1 秒 → same++
    var ours = new RefExhaustiveSolver.Objective(/* ... */ o.WorkSeconds - 1.0);
    Assert.False(RefExhaustiveSolver.Less(ours, exhaustive) && !tiersEqual,
        $"seed {seed}: optimizer {o} beats exhaustive {exhaustive}");         // ②
    failures.Add($"seed {seed}: ours {o} vs exhaustive {exhaustive}");
}
Assert.True(same >= 0.95 * count, /* ... */);                              // ③
```

設計重點：

- ① **情境產生器針對要驗證的語意設計**：`TwoShiftInstance` 產生「午餐、晚餐兩段時間窗＋緊工時上限」，專門逼出中段等待。舊評估器在這組只有 86/120，新評估器 120/120。原本的 200 組隨機實例看不出這個 bug，因為參考解採用了同樣錯誤的語意。
- ② **最佳化器「贏過」窮舉解是硬性失敗**：啟發式搜尋可以找不到最佳解，但不可能比窮舉更好。如果發生，代表評估器或參考解其中一個的語意錯了。這是比「結果相同」更強的檢查。
- ③ 門檻 95% 而不是 100%：ALNS 是啟發式，迭代有限時偶爾找不到最佳解是正常的。實際結果三組都是 100%（📄 `docs/test-report.md`）。

### 5.3 決定性測試

`Same_seed_and_iteration_limit_give_identical_results_regardless_of_thread_count`（`tests/MealRoute.Tests/Optimization/OptimizerTests.cs`）用 1、4、3 個執行緒跑同一個實例，斷言每條路線的站序與每站時刻**完全相等**，不是近似相等。

## 6. 覆蓋範圍

| 範圍 | 狀態 |
|---|---|
| Core 全部模組 | 有單元與性質測試 |
| ViewModels | 32 個測試（`MainViewModelTests`），涵蓋主要流程 |
| 端對端（small 範例） | 1 個，以參考 TD Dijkstra 驗證時刻 |
| 效能需求 | 增量預覽 ≤ 1 ms、拖曳 < 50 ms 以測試斷言；大型範例與 Solomon 走 CLI，結果寫在 README |
| **App（WinUI、Win2D 繪製、列印）** | **沒有自動化測試**；靠把邏輯放在 ViewModel 降低風險 |
| **參考實作的獨立性** | **沒有測試**（見第 4 節） |
| medium、large 範例的端對端 | 沒有自動化測試（CI 時間考量）；以 CLI 手動量測 |
| 時間相依路網上的窮舉對照 | 窮舉測試主要是靜態歐氏實例；時間相依只驗證可行性與時刻 ≤ 1 秒 |

## 7. 建議補強的測試（建議）

1. **參考實作獨立性的架構測試**（建議）：用反射載入測試組件，掃描 `MealRoute.Tests.Reference` 命名空間中每個方法的 IL 呼叫目標，斷言不呼叫 `MealRoute.Core.Travel`、`Evaluation`、`Optimization`、`Network` 的方法。這是設計文件原本承諾的。
2. **時間相依的窮舉對照**（建議）：小路網（數十節點）＋ 3–5 站，以參考 TD Dijkstra 建行車表，對照最佳化結果。可以抓到「時間相依下出發時刻選擇」的問題（2026-11-11 的尖峰退步就是在這個情境才出現，靠大型範例手動量測才發現）。
3. **大型範例的效能回歸**（建議）：在 CI 以較少迭代數跑 large 範例，記錄「相同迭代數下的行車與工時」，超過基準一定比例就警告。
4. **專案檔往返的模糊測試**（建議）：隨機產生專案（含極端值、Unicode、公式字元）存讀，斷言內容相等。
