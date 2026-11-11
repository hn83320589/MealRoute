# 07 程式內容導讀

## 1. 建議的閱讀順序

| 順序 | 檔案 | 為什麼先讀它 |
|---|---|---|
| 1 | `src/MealRoute.Core/Model/Stop.cs`、`src/MealRoute.Core/Model/ProblemInstance.cs`、`src/MealRoute.Core/Model/Solution.cs` | 全部是純資料，一次看懂所有名詞與「地點索引」約定 |
| 2 | `tools/MealRoute.Cli/SampleCommands.cs` 的 `Solve()` | 不到 60 行就把整條管線串完，沒有 UI 干擾 |
| 3 | `src/MealRoute.Core/Evaluation/RouteEvaluator.cs` | 「一條路線可不可行、花多少時間」的唯一定義，其他模組都靠它 |
| 4 | `src/MealRoute.Core/Optimization/Optimizer.cs` → `src/MealRoute.Core/Optimization/AlnsRun.cs` | 先看外層的協調（平行、精確化），再看內層的搜尋 |
| 5 | `src/MealRoute.Core/Travel/PathSampledOracle.cs` | 效能的關鍵；讀完 08 章再讀會更容易 |
| 6 | `src/MealRoute.Core/Editing/PlanEditor.cs`、`src/MealRoute.Core/Rescheduling/Rescheduler.cs` | 兩個「重用評估器與最佳化器」的範例 |
| 7 | `src/MealRoute.App.ViewModels/PlanningWorkspace.cs`、`src/MealRoute.App.ViewModels/MainViewModel.cs` | UI 怎麼把 Core 串起來 |

## 2. 進入點

### 2.1 桌面 App

```csharp
// src/MealRoute.App/MainWindow.xaml.cs
public MainWindow()
{
    var messages = new ContentDialogMessageService(() => Content?.XamlRoot);
    ViewModel = new MainViewModel(
        new WinUiFileDialogService(this),
        messages,
        new Win2DPrintService(this, messages),
        new WinUiDispatcher(DispatcherQueue),
        TimeProvider.System,                       // ①
        new JsonSettingsStore());
    InitializeComponent();
    // ...
    Map.Attach(ViewModel);                         // ②
    Timeline.Attach(ViewModel);
    Activated += OnFirstActivated;                 // ③
}
```

① 這是整個 App 唯一使用 `TimeProvider.System` 的地方，往下全部是注入的。測試時換成 `FakeTimeProvider`，就能測「最佳化 60 秒時間上限」而不必真的等 60 秒。

② 地圖與時間軸是 Win2D 的 `CanvasControl`，不是純 XAML 綁定，所以用 `Attach()` 讓它們訂閱 ViewModel 的變化並自行重繪。

③ 第一次啟用時才開啟專案（命令列參數或上次開啟的專案）。放在建構子裡會在視窗還沒顯示時就跑長時間工作。

`src/MealRoute.App/App.xaml.cs` 只做兩件事：建立視窗、把未預期例外寫入 `crash.log`。注意它**不吞例外**：

```csharp
// src/MealRoute.App/App.xaml.cs
catch (IOException)
{
    // logging must never mask the original failure
}
```

只有「寫 log 失敗」被吞掉，原本的例外照常讓程式崩潰。

### 2.2 CLI

`tools/MealRoute.Cli/Program.cs` 是簡單的指令分派，值得注意的是第一行：

```csharp
// tools/MealRoute.Cli/Program.cs
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;
```

固定文化特性，讓「1.5」不會在某些語系的機器上被印成「1,5」，基準結果的 Markdown 才能比對。

## 3. 一次求解的完整生命週期（CLI `solve`）

以 `SampleCommands.Solve()`（`tools/MealRoute.Cli/SampleCommands.cs`）為主線，依序經過每一層。

### 第 1 站：讀檔與建圖

```csharp
// tools/MealRoute.Cli/SampleCommands.cs
var read = GeoJsonRoadReader.ReadFile(networkPath);
var net = NetworkBuilder.Build(read.Features);
var analysis = NetworkAnalysis.Analyze(net);
```

`NetworkBuilder.Build()` 只把每條 LineString 的**兩個端點**變成節點，中間點只是幾何。座標「位元相等」才合併，`-0.0` 先正規化成 `0.0`：

```csharp
// src/MealRoute.Core/Network/NetworkBuilder.cs
int NodeOf(GeoPoint p)
{
    p = new GeoPoint(p.Lat == 0 ? 0.0 : p.Lat, p.Lon == 0 ? 0.0 : p.Lon);   // ①
    if (!nodeIndex.TryGetValue(p, out var id)) { /* 新節點 */ }
    return id;
}
// ...
if (feature.Oneway != OnewayDirection.Backward) raw.Add(new(a, b, len, feature.Class, f, false, 0, len));
if (feature.Oneway != OnewayDirection.Forward)  raw.Add(new(b, a, len, feature.Class, f, true, 0, len));
```

① README 的規則是「位元相等才合併，`-0.0` 視同 `0.0`」。在目前的 .NET 中，`double.Equals` 與 `GetHashCode` 本來就把 `0.0` 與 `-0.0` 當成相同，所以字典查找不受影響；這行正規化的實際效果是讓**存進節點陣列的座標**一律是 `+0.0`，輸出與比對時不會出現 `-0`。🔍 推斷：這是防禦性寫法，也讓「`-0.0` 視同 `0.0`」這條規則在程式碼中明確可見，而不是依賴執行階段的實作細節。

最後 `RoadNetwork.Create()` 用計數排序把邊依起點排好，變成 CSR（compressed sparse row）：節點 v 的出邊就是 `FirstOut[v]..FirstOut[v+1]` 這段連續陣列，Dijkstra 展開時對快取很友善。

### 第 2 站：吸附與行車時間模型

```csharp
// tools/MealRoute.Cli/SampleCommands.cs
var inst = project.ToInstance();
var (h0, h1) = TravelModel.Horizon(inst);
var model = TravelModel.Build(net, analysis, project.SpeedProfile, inst.LocationPoints(), h0, h1, new OracleBuildOptions());
```

`TravelModel.Build()`（`src/MealRoute.Core/Planning/TravelModel.cs`）依序做三件事：

1. `Snapper.Snap()`：每個地點吸附到最大 SCC 上最近的路段，插入虛擬節點，產生**新的** `RoadNetwork`（原路網不變）。
2. `PathSampledOracle.Build()`：L1 快取（平行）。
3. `new ExactOracle(...)`：L2，不需要預先計算。

`Horizon()` 取所有車輛的「最早出發」到「最晚返回＋30 分」，L1 只需要涵蓋這段時間的取樣。

### 第 3 站：最佳化

```csharp
// tools/MealRoute.Cli/SampleCommands.cs
var res = new Optimizer(inst, model.Search, model.Exact).Run(null, LockSet.Empty, new OptimizerOptions
{
    Seed = project.Optimizer.Seed,
    MaxIterations = args.GetInt("iterations", 1_000_000),
    Runs = args.GetInt("runs", project.Optimizer.Runs),
    TimeLimit = TimeSpan.FromSeconds(seconds),
}, progress, default);
```

`Optimizer` 收兩個 oracle：搜尋用 L1、最終裁決用 L2。內部流程見第 6 節。

### 第 4 站：輸出

`res.Solution` 裡每條 `RoutePlan` 的時刻都來自 L2 精確計算；`res.Objective` 是字典序的五項目標值。CLI 印出指標與吸附警告（警告透過 `ProblemInstance.DescribeLocation()` 轉成站名）。

## 4. 模組：路網（Network）

**職責**：GeoJSON → 有向圖 → 強連通分量 → 吸附。

**關鍵類別**：`RoadNetwork`、`NetworkBuilder`、`StronglyConnectedComponents`、`NetworkAnalysis`、`SegmentSpatialIndex`、`Snapper`。

**值得學的寫法**：

1. **迭代版 Tarjan**（`src/MealRoute.Core/Network/StronglyConnectedComponents.cs`）：用 `callNode[]`、`callEdge[]` 兩個陣列手動模擬遞迴堆疊，每個深度記住「目前處理到第幾條出邊」。遞迴版在 5 萬節點的長鏈上會堆疊溢位。分量編號最後依「各分量最小節點編號」重新排序，讓結果與走訪順序無關（決定性）。

2. **吸附時長度守恆**（`Snapper.SplitEdges()`）：切開後各段長度用「沿線累計長度之差」計算，而不是重新量 haversine：

   ```csharp
   // src/MealRoute.Core/Network/Snapper.cs
   for (var k = 0; k + 1 < chainNodes.Length; k++)
   {
       var len = chainOffsets[k + 1] - chainOffsets[k];
       var (from, to) = reversed ? (chainNodes[k + 1], chainNodes[k]) : (chainNodes[k], chainNodes[k + 1]);
       edges.Add(new(from, to, len, network.Class[e], f, reversed, chainOffsets[k], chainOffsets[k + 1]));
   }
   ```

   投影點不一定剛好在原折線上（有投影誤差），重算 `haversine(A,P) + haversine(P,B)` 會比原長略長。用累計長度相減，各段加總恰好等於原長，「有站點與沒站點時同一條路的行車時間一樣」才會成立。

3. **合併容差**：投影點距端點 < 1 m 直接用端點；同一路段上相距 < 1 m 的切點合併。這兩條規則保證**不會產生零長度路段**（零長度邊會讓某些除法或比較出問題）。

4. **只吸附到最大 SCC**：兩端都在最大 SCC 的路段才是候選，所以孤島與單行死巷不會被吸附到，避免「站點吸附上去之後出不來」。

**可以改進的地方**：

- `RoadNetwork.EdgeGeometry()` 每次呼叫都重算整條 feature 的累計長度（`PolylineMath.Cumulative`），畫路線時會重複計算。量測沒有顯示瓶頸，但可快取。

## 5. 模組：行車時間（Travel）

**職責**：速度模型、時間相依最短路徑、L1/L2 兩層 oracle。

**關鍵類別**：`SpeedProfile`、`TimeDependentDijkstra`、`CompressedPath`、`PathSampledOracle`（L1）、`ExactOracle`（L2）、`EuclideanOracle`。

**值得學的寫法**：

1. **重複使用工作陣列的時間戳記技巧**（`TimeDependentDijkstra`）：

   ```csharp
   // src/MealRoute.Core/Travel/TimeDependentDijkstra.cs
   private void Relax(int v, double time, int viaEdge, double heuristic)
   {
       if (_stamp[v] != _current)      // 這個節點在本次搜尋還沒碰過
       {
           _stamp[v] = _current;
           _settled[v] = false;
           _arrival[v] = double.PositiveInfinity;
           _parentEdge[v] = -1;
       }
       // ...
   }
   private void Reset()
   {
       _queue.Clear();
       if (++_current == int.MaxValue) { Array.Clear(_stamp); _current = 1; }
   }
   ```

   每次搜尋不必清空 2 萬個元素的陣列，只要把 `_current` 加一；碰到的節點才惰性初始化。L1 建快取要跑數千次 one-to-all 搜尋，這個技巧省下大量時間。代價是物件不是執行緒安全的，所以 L1 建置用 `ThreadLocal<TimeDependentDijkstra>`、L2 用 `ConcurrentBag` 物件池。

2. **優先佇列用 (時刻, 節點) 當鍵**：`PriorityQueue<int, (double Key, int Node)>`，時刻相同時以節點編號破平手，結果決定性。

3. **壓縮路徑**（`CompressedPath`）：速度只看道路等級，所以相鄰的同等級邊可以合併成一段「(等級, 總長)」，積分結果完全相同。L1 查詢時只需積分幾段而不是幾十條邊。

4. **`Heal()` 只在決定性的時間點呼叫**：L1 允許把 L2 找到的更好路徑加入候選，但如果在平行搜尋中途加入，不同執行緒會看到不同的快取內容，結果就不可重現。程式註解明寫：「must be called only at deterministic points」。

**可以改進的地方**：

- `ExactOracle` 的記憶表達到 200 萬筆時**整個清空**（`_memo.Clear()`）。長時間使用時快取命中率會週期性歸零；LRU 或分段淘汰會比較平滑。
- `PathSampledOracle.Build()` 每對地點最多 255 條不同路徑（`byte` 索引），超過會丟例外。實務上每對約 1–3 條，但這是硬上限。

## 6. 模組：評估（Evaluation）

**職責**：給定一台車與站序，算出可不可行、出發時刻、各站時刻、休息位置、行車與工時；不可行時給出時間最早的違反。

**關鍵類別**：`RouteEvaluator`、`RouteCost`（純量結果）、`RouteEvaluation`（含每站時刻）、`ObjectiveValue`、`Violation`。

這是整個專案最核心的檔案，演算法細節見 08 章第 3 節。這裡看它的**兩條路徑**：

```csharp
// src/MealRoute.Core/Evaluation/RouteEvaluator.cs
public RouteEvaluation Evaluate(int vehicle, IReadOnlyList<int> stops)   // 完整：每站時刻、診斷
public RouteCost EvaluateCost(int vehicle, ReadOnlySpan<int> route,      // 快速：只有數字
                              Span<double> earliestDepartures = default)
```

- `Evaluate()` 給 UI、路線單、最終解使用，會配置 `StopVisit[]`，不可行時跑一次「診斷模擬」找出第一個違反。
- `EvaluateCost()` 給最佳化內迴圈使用，參數是 `ReadOnlySpan<int>`，呼叫端可以傳 `stackalloc` 的緩衝區；除非需要搜尋休息位置，否則**不配置任何物件**。兩者共用同一個 `Plan()`，所以判定結果一定一致（有性質測試 `Property_fast_cost_path_agrees_with_full_evaluation` 確認）。

`ObjectiveValue.CompareTo()` 有一個值得學的細節：

```csharp
// src/MealRoute.Core/Evaluation/ObjectiveValue.cs
// Travel sums of equivalent solutions differ by floating-point noise (different summation
// order); compare on a microsecond grid so the work-time tier can decide real ties.
c = Quantize(TravelSeconds).CompareTo(Quantize(other.TravelSeconds));
return c != 0 ? c : WorkSeconds.CompareTo(other.WorkSeconds);
```

字典序目標的第四層是 double。兩個其實等價的解，因為加總順序不同，行車時間可能差 1e-12 秒；直接比較的話第五層（工時）永遠輪不到。量化到微秒再比，才能讓「行車相同、工時較短」的解勝出。

**可以改進的地方**：

- `Settle()` 為了選出發時刻，最多會多跑十幾次 `Forward()` 模擬。這讓每次迭代變慢（大型範例 4 × 6,000 次迭代由 182 秒變 241 秒，📄 README「效能」）。靜態行車時可以只算一次前向時間餘裕，不必迭代。

## 7. 模組：最佳化（Optimization）

**職責**：從目前的解（或空解）出發，找出字典序目標最好的完整可行解。

**關鍵類別**：`Optimizer`（協調）、`AlnsRun`（一次搜尋）、`SearchState`（可變工作狀態）、`Rng`、`LockSet`、`OptimizerOptions`。

### 7.1 `Optimizer.Run()` 的骨架

```csharp
// src/MealRoute.Core/Optimization/Optimizer.cs
var start = InitialState(initial);
RepairInfeasibleRoutes(start, frozen, locked);                 // ①
var constructor = new AlnsRun(_inst, _search, new Rng(options.Seed), /* ... */);
constructor.Construct(start);                                  // ②
// ...
Parallel.For(0, options.Runs, /* ... */, i =>
{
    var run = new AlnsRun(_inst, _search, new Rng(Rng.Derive(options.Seed, i)), /* ... */);
    var best = run.Run(start, options.MaxIterations, options.VehicleReductionShare);
    results[i] = (best, best.Objective(_inst));                 // ③
});
// 依（目標值, run 編號）選出 winner
var solution = FinalizeExact(winner, frozen, locked);          // ④
```

① 起始解可能不可行（延誤回報、使用者強制套用的拖曳），搜尋只處理可行路線，所以先從違反處逐一移出站點。

② 建構解（regret-2 插入）只做一次，所有 run 從同一個起點出發。

③ 每個 run 的結果寫進以 run 編號為索引的陣列，而不是「誰先跑完誰先比」。所以不管有幾個執行緒、誰先結束，選出的 winner 都一樣。這就是「結果與執行緒數無關」的實作方式。

④ `FinalizeExact()` 用 L2 重新評估：L1 是上界（只會高估行車時間），所以 L1 可行的路線在 L2 下到達只會更早；唯一可能翻轉的是休息規則（累計行車變了）。翻轉時移出違反站；接著因為 L2 比較快，原本放不下的站可能放得下，再嘗試補插。

### 7.2 `AlnsRun` 的搜尋迴圈

```csharp
// src/MealRoute.Core/Optimization/AlnsRun.cs
var cand = current.Clone();
Destroy(cand, d);
Repair(cand, r);

if (AllFeasible(cand))
{
    var candObj = cand.Objective(_inst);
    if (candObj < bestObj)                                       // ① 精確字典序
    {
        if (_inst.Stops.Count <= 80) Polish(cand);
        // ... 更新 best 與 current，score = 33
    }
    else if (Accept(candObj, curObj, temperature, ignoreVehicleTier))   // ② 純量＋退火
    {
        // ... 更新 current，score = 9 或 13
    }
}
```

① 「是不是新的最佳解」永遠用精確的字典序判斷。

② 「要不要接受這個比較差的解」用模擬退火，比較的是**純量**：`未服務必送 × 10P + 未服務優先權 × 2P + 車輛數 × 3P + 行車 + 0.001 × 工時`，P 是平均每站行車時間。用有限懲罰而不是無限大，搜尋才能暫時「少送一站」或「多開一台車」繞過障礙。📄 README「演算法」與 `AlnsRun.Scalar()`。

這個「兩套比較標準」的設計很值得帶走：**接受準則可以寬鬆近似，但最佳解的記錄必須精確。**

### 7.3 Repair 的「層級優先」

```csharp
// src/MealRoute.Core/Optimization/AlnsRun.cs
private static int Tier(Stop stop) => stop.Mandatory ? 6 : stop.Priority;
// ...
if (tier > bestTier || (tier == bestTier && (key > bestKey || (key == bestKey && c1 < bestCost))))
```

必送站的層級是 6，選送站是優先權 1–5。repair 一定先處理層級高的站，同層級才看 regret 值或成本。這讓「必送優先於一切」在插入階段就成立，而不是靠懲罰權重碰運氣。

但這也帶來一個盲點：必送站放不完時，repair 總是把同一個必送站插回去。2026-11-11 加入的 `UnservedSwapPass()` 就是為了這個情況（見 11 章）。

### 7.4 插入前的時間窗預檢

```csharp
// src/MealRoute.Core/Optimization/AlnsRun.cs
if (p >= 1)
{
    // Departures along the route increase, so once the predecessor leaves after the
    // window end every later position is infeasible too.
    if (deps[p - 1] > st.WindowEnd) break;
    var arr = _eval.Oracle.ArrivalTime(_inst.StopLocation(route[p - 1]), _inst.StopLocation(stop), deps[p - 1]);
    if (arr > st.WindowEnd) continue;
}
```

在完整評估之前，先用「前一站的離開時刻＋一段行車」判斷能不能趕上時間窗，大部分不可能的位置在這裡就被剔除。關鍵是 `deps` 必須是**下界**：插入只會讓之後的站更晚，所以「最早出發排程」的離開時刻是有效下界。出發時刻改為決策變數後，實際排程可能延後出發，若用實際排程的時刻做預檢，會誤刪可行位置。所以 `EvaluateCost()` 輸出的是最早出發排程的離開時刻（📄 commit `1a25bf2`）。

### 7.5 可以改進的地方

- **插入的評估成本**：`BestInsertion()` 對每個位置呼叫一次完整的 `EvaluateCost()`，單站插入是 O(n²)（n 為路線站數）。文獻中常用「前綴／後綴資料串接」做到 O(1) 可行性檢查，但在時間相依行車與休息規則下比較複雜。目前每路線約 14 站，可以接受。
- `AlnsRun.cs` 中 `Accept()` 附近有兩段相疊的 `<summary>` 註解（模擬退火的說明被放在 `AllFeasible()` 上方），讀起來會混淆。
- `LockSet` 定義在 `OptimizerOptions.cs`，從檔名不容易找到。

## 8. 模組：編輯（Editing）

**職責**：互動調整的工作階段：預覽、套用、復原、鎖定。

`PlanEditor`（`src/MealRoute.Core/Editing/PlanEditor.cs`）為每條路線各保存一份 L1 與 L2 的評估結果。一次移動最多改變兩條路線：

```csharp
// src/MealRoute.Core/Editing/PlanEditor.cs
var after = (RouteEvaluation[])evals.Clone();
foreach (var (v, route) in changed)
{
    after[v] = evaluator.Evaluate(v, route);   // 只重算改到的路線
    // ...
}
```

`Changed()` 會比較新舊站序，把「實際上沒變」的路線剔除，所以把一站拖回原位不會觸發任何重算。

**值得學的寫法**：

- **復原以「站序快照」實作**：`Stack<int[][]>`，每次套用前推入所有路線的站序。不記錄時刻，因為時刻可以由站序重算。`Restore()` 只重算與快照不同的路線。
- **移動是資料**：`MoveStop`、`SwapStops` 是 record，預覽與套用用同一個物件，`RoutesAfter()` 讓測試可以檢查移動後的站序。

## 9. 模組：重新排程（Rescheduling）

**職責**：依駕駛回報，只重排還沒有人出發去服務的站。

`Rescheduler.Run()`（`src/MealRoute.Core/Rescheduling/Rescheduler.cs`）最值得學的是 `EnRoute()`：把途中的車改寫成一台普通的車。

```csharp
// src/MealRoute.Core/Rescheduling/Rescheduler.cs
return veh with
{
    StartDepot = position,              // 從回報位置出發
    EarliestStart = resume,             // 回報時刻 + 延誤
    FixedDeparture = true,              // 不允許延後出發
    InitialDrivingSeconds = driving,    // 距上次休息已開了多久
    WorkStartedAt = workStart,          // 工時從原本的出發時刻算
    MealCapacity = meals,               // 車上剩下的餐盒
    BoxCapacity = boxes,
};
```

再加上限制函數：

```csharp
// src/MealRoute.Core/Rescheduling/Rescheduler.cs
bool CanServe(int v, int s) => statuses[v].State switch
{
    VehicleState.NotDeparted => free.Contains(s),
    VehicleState.EnRouteReported or VehicleState.EnRouteCommitted => exclusive[v]!.Contains(s),
    _ => false,
};
```

就能交給同一個 `Optimizer`。評估器只需要支援 `FixedDeparture`、`InitialDrivingSeconds`、`WorkStartedAt` 三個欄位，最佳化器只需要支援 `canServe`。**不需要第二套求解器，也不需要第二套可行性規則**，這兩件事都會讓「重新排程的結果與一般最佳化的語意不一致」的風險大幅降低。

**可以改進的地方**：

- 累計行車時間採保守假設：「從最後完成站離開後到回報時刻，都在開車」。如果駕駛其實停在路邊，會高估累計行車，可能讓休息提早。📄 `docs/01-system-analysis.md` §4.12 明列這是保守假設。

## 10. 模組：ViewModels

**職責**：把 Core 包成可綁定的狀態與命令。

| 類別 | 職責 |
|---|---|
| `PlanningWorkspace` | 一個排班日載入的所有東西：專案、路網、行車時間模型、編輯器、警告；不是 ViewModel，沒有 UI 通知 |
| `MainViewModel` | 專案開關存、匯入、警告清單、鎖定命令、子 ViewModel |
| `OptimizationViewModel`、`RescheduleViewModel` | 背景執行、進度、取消 |
| `DragPreviewViewModel` | 拖曳預覽、延遲量測（`LatencyMs`） |
| `MapViewModel`、`TimelineViewModel` | 圖層與甘特圖資料 |
| `SelectionService` | 選取的單一事實來源 |

**值得學的寫法**：

- **子 ViewModel 拿到的是 `Func<PlanningWorkspace?>` 而不是工作區本身**：開新專案時 `MainViewModel` 換掉 `_workspace`，所有子 ViewModel 下次呼叫就自動拿到新的，不需要逐一通知「工作區換了」。
- **進度只增不減**：`OptimizationViewModel` 收到進度時 `if (p.Fraction > Progress) Progress = p.Fraction;`，再加上 `Optimizer` 內部的 `ProgressTracker` 也保持單調，兩層保護。
- **`IsAllowedReferencedPath()` 是 `public static`**：安全檢查寫成純函數，所以可以用大量路徑字串做單元測試，不需要真的建立網路磁碟。

## 11. 模組：App（WinUI 3 + Win2D）

`src/MealRoute.App/Views/MapView.xaml.cs`、`src/MealRoute.App/Views/TimelineView.xaml.cs` 是最大的兩個檢視檔。重點技巧（📄 README「效能」、`docs/02-architecture.md` §5）：

- 路網依 1 km 分塊建立 Win2D 幾何，只畫視窗內的分塊。
- 平移縮放只改轉換矩陣，不重建幾何。
- 縮小時隱藏巷道（LOD），維持 2 萬節點的流暢度。
- 拖曳時讀取 Shift 鍵狀態決定是否強制套用不可行的移動。

App 層沒有自動化測試（見 09 章），這是刻意把邏輯壓到 ViewModel 的原因之一。
