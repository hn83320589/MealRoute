# 測試報告

最近一次完整測試（2026-11-11）。之後每次 push 的報告請看 [GitHub Actions › CI](https://github.com/hn83320589/MealRoute/actions/workflows/ci.yml) 各次執行頁面的 Summary（「Test report」表格）與 `test-results` artifact（TRX）。

## 對應版本

| 項目 | 內容 |
|---|---|
| 程式碼版本 | `1ce58a0`（之後加入本報告的 commit 只修改文件與基準結果） |
| CI 執行 | [run 42938043313](https://github.com/hn83320589/MealRoute/actions/runs/42938043313)：建置、測試、發佈 win-x64，結果 **success** |
| 本機執行 | `dotnet test tests/MealRoute.Tests -c Release --filter "Category!=Benchmark"`，Windows 11、.NET 9 |

## 總結

**251 個測試全部通過**（失敗 0、略過 0），本機耗時約 28 秒。建置（`dotnet build MealRoute.sln -c Release -p:Platform=x64`）0 警告、0 錯誤。標記 `Category=Benchmark` 的效能量測不在此列。

## 各測試類別

| 測試類別 | 通過 | 失敗 |
|---|---:|---:|
| Benchmark.SolomonTests | 8 | 0 |
| Editing.PlanEditorTests | 9 | 0 |
| Evaluation.ObjectiveValueTests | 6 | 0 |
| Evaluation.RouteCostPropertyTests | 1 | 0 |
| Evaluation.RouteEvaluatorPropertyTests | 4 | 0 |
| Evaluation.RouteEvaluatorTests | 22 | 0 |
| Geo.HaversineTests | 4 | 0 |
| GeoJson.GeoJsonRoadReaderTests | 29 | 0 |
| Infrastructure.PropertyTests | 3 | 0 |
| Integration.SamplePipelineTests | 1 | 0 |
| Network.NetworkBuilderTests | 11 | 0 |
| Network.SnapperTests | 15 | 0 |
| Network.StronglyConnectedComponentsTests | 5 | 0 |
| Optimization.LockTests | 4 | 0 |
| Optimization.OptimizerPropertyTests | 5 | 0 |
| Optimization.OptimizerTests | 13 | 0 |
| Optimization.UnservedSwapTests | 2 | 0 |
| Projects.ProjectAndExportTests | 23 | 0 |
| Projects.RosterCsvTests | 10 | 0 |
| Rescheduling.ReschedulerTests | 8 | 0 |
| Synthetic.GeneratorTests | 3 | 0 |
| Travel.SpeedModelTests | 14 | 0 |
| Travel.TimeDependentDijkstraTests | 9 | 0 |
| Travel.TravelTimeOracleTests | 10 | 0 |
| ViewModels.MainViewModelTests | 32 | 0 |
| **合計** | **251** | **0** |

## 與獨立窮舉器的比較（`OptimizerPropertyTests`）

每組都要求最佳化結果的目標值（未服務必送數、未服務選送優先權、用車數、行車、工時）與 `RefExhaustiveSolver` 的最佳解相同，並通過 `RefFeasibilityChecker`。

| 測試 | 情境 | 結果 |
|---|---|---|
| `Property_optimizer_matches_exhaustive_optimum_on_200_small_instances` | ≤ 9 站、1–3 車，含時間窗、休息 | 200/200 |
| `Property_optimizer_matches_exhaustive_optimum_with_lunch_and_dinner_windows` | 午餐／晚餐兩時段＋緊工時上限（中段長等待須以延後出發吸收） | 120/120（修正前的評估器為 86/120） |
| `Property_optimizer_matches_exhaustive_optimum_when_not_all_mandatory_stops_fit` | 必送需求超過容量，須選擇放棄哪個必送站 | 150/150（其中 144 組有必送短缺） |
| `Property_every_optimized_static_solution_passes_the_reference_checker` | 25 組 5–34 站、1–4 車（靜態） | 25/25 通過參考檢查器，未服務必送站皆附原因 |
| `Property_time_dependent_optimized_solutions_match_reference_times_within_one_second` | 12 組隨機路網＋時間相依速度 | 12/12，各站時刻與參考積分差 ≤ 1 秒 |

## 基準（不在 CI 執行）

Solomon VRPTW 29 個實例、每個 180 秒：29/29 符合「車輛數 ≤ BKS + 1 且距離 ≤ BKS × 105%」，15 個達到 BKS。完整表格見 [benchmarks/solomon/RESULTS.md](../benchmarks/solomon/RESULTS.md)。
