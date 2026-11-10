# 合成範例

由 `MealRoute.Cli generate` 產生（固定種子，可重現）：

| 範例 | 路網 | 名單 | 特色 |
|---|---|---|---|
| [small](small/EXPECTED.md) | 約 1 千節點 | 30 站、3 車 | 單行道、選送站、1 個離路網過遠的站 |
| [medium](medium/EXPECTED.md) | 約 5 千節點 | 100 站、7 車 | 單行道、選送站、2 個過遠的站 |
| [large](large/EXPECTED.md) | 約 2 萬節點 + 50 km 郊區道路 | 200 站、15 車 | 6 個郊區村落站由長工時車服務，必須休息 |

所有範例共用內建速度表（`SpeedProfile.Default`）：午餐尖峰 11:30–13:00 各道路等級降速，名單時間窗 10:30–14:00 會跨越尖峰。
每個城市都刻意包含一條單行死巷（`trap`）與一個孤島（`island-*`），匯入時會列為小分量警告。

重新產生：

```powershell
dotnet run --project tools/MealRoute.Cli -c Release -- generate --size small --out samples/small
```
