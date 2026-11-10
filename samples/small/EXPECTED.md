# small 範例：預期結果

```powershell
dotnet run --project tools/MealRoute.Cli -c Release -- solve --project samples/small/project.mealroute.json --iterations 3000 --seconds 600
```

| 指標 | 預期 |
|---|---|
| 路網 | 1,028 節點、3,477 有向邊、4 個小分量（含 `trap`、`island`） |
| 吸附警告 | 1（S001 距最近可用路段 > 150 m） |
| 第一個可行解 | < 1 秒 |
| 結果（種子 11、每次 3,000 迭代 × 4 次） | 必送 26/26 全部服務、選送全部服務、**2 車**、總行車約 0:59、總工時約 4:36 |
| 休息 | 0 次（路線行車皆遠低於 4.5 小時） |

檢查方式：
- `SamplePipelineTests` 以參考 TD Dijkstra 與參考可行性檢查器獨立驗證此範例的最佳化結果（時間窗、容量、工時、休息、車輛時段、必送站，時刻誤差 ≤ 1 秒）。
- 在 App 開啟 `project.mealroute.json`：地圖應以警示色顯示 4 個小分量；S001 出現在吸附警告清單。
