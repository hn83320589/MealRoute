# medium 範例：預期結果

```powershell
dotnet run --project tools/MealRoute.Cli -c Release -- solve --project samples/medium/project.mealroute.json --iterations 1500 --seconds 600
```

| 指標 | 預期 |
|---|---|
| 路網 | 5,045 節點、17,228 有向邊、7 個小分量 |
| 吸附警告 | 2（S001、S002 位於城市南緣外 160–250 m） |
| 第一個可行解 | 約 1 秒（L1 快取 < 1 秒） |
| 結果（種子 22、每次 1,500 迭代 × 4 次） | 必送 86/86 全部服務、選送全部服務、**5 車**（7 車可用）、總行車約 4:05、總工時約 12:06 |
| 休息 | 0 次 |

檢查方式：
- 必送站全數服務；未服務清單為空。
- 在 App 中開啟，對任一站做拖曳到另一路線，應在 50 ms 內顯示可行性與指標變化。
