# 04 技術選型

## 1. 技術與套件一覽

版本以 `*.csproj`、`global.json` 為準。

| 技術／套件 | 版本 | 用途 | 選用理由 | 可能的替代方案 |
|---|---|---|---|---|
| .NET SDK | 9.0.100 起（`global.json`，`rollForward: latestFeature`） | 全部專案 | 文件、程式碼與 commit 中未記載選用原因。🔍 可能的理由：C# 的 `Span<T>`、`stackalloc`、record 適合寫無配置的內迴圈；與 WinUI 3 同一生態系 | .NET 8 LTS |
| WinUI 3（Microsoft.WindowsAppSDK） | 1.8.260921001 | 桌面 UI | 文件中未記載為何選 WinUI 3。📄 套件清單於 2026-11-09 經使用者同意（`docs/02-architecture.md` §7）。🔍 推斷規格指定 Windows 桌面程式 | WPF、Avalonia、MAUI |
| Win2D（Microsoft.Graphics.Win2D） | 1.3.2 | 地圖、時間軸繪製與列印 | 📄 列印方案經使用者確認採 `CanvasPrintDocument`（`docs/01-system-analysis.md` §4.13 🔶 決策 6）。🔍 2 萬節點路網需要 GPU 加速的即時繪製，XAML Shape 元素會太重 | SkiaSharp、XAML `Path` |
| CommunityToolkit.Mvvm | 8.4.2 | `[ObservableProperty]`、`[RelayCommand]` 原始碼產生器 | 📄 經使用者同意的套件清單 | 手寫 `INotifyPropertyChanged`、ReactiveUI |
| System.Text.Json | 隨 .NET | GeoJSON、專案檔、設定 | 📄 README：「GeoJSON 以 System.Text.Json 自行解析」 | NetTopologySuite.IO.GeoJSON |
| xUnit | 2.9.3 | 測試框架 | 📄 經使用者同意的套件清單 | NUnit、MSTest |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | `FakeTimeProvider`，測試時間上限、重新排程的「現在」 | 📄 `docs/01-system-analysis.md` §5「時間：所有現在透過 TimeProvider」 | 自訂 `IClock` 介面 |
| Microsoft.NET.Test.Sdk、xunit.runner.visualstudio | 18.11.0、3.1.5 | 測試執行 | 工具類 | — |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.7705 | WinUI 建置 | 工具類 | — |

## 2. 刻意「不用」的東西

這是本專案選型最重要的一點：

> 📄 README「架構」：「**不使用任何現成路徑規劃／VRP／最佳化套件**；GeoJSON 以 System.Text.Json 自行解析。」

所以下列全部自己寫：

| 自己寫的 | 常見的現成方案 |
|---|---|
| CSR 路網、Tarjan SCC、空間索引、吸附 | NetTopologySuite、OSRM 前處理 |
| 時間相依 Dijkstra／A\* | OSRM、GraphHopper、Itinero |
| ALNS 最佳化器 | Google OR-Tools、jsprit、PyVRP |
| 性質測試執行器（`tests/MealRoute.Tests/Infrastructure/Property.cs`） | FsCheck、CsCheck |
| 亂數產生器（`src/MealRoute.Core/Optimization/Rng.cs`） | `System.Random` |

理由（有依據的部分）：

- **自製亂數**：📄 `src/MealRoute.Core/Optimization/Rng.cs` 註解：「Own implementation so results never depend on the runtime's `Random` algorithm」。`System.Random` 的演算法在 .NET 版本間改過，用它就無法保證「同種子同結果」跨版本成立。
- **自製性質測試**：📄 `docs/02-architecture.md` §6：「以自製小型 `Property.ForAll` … 不引入 FsCheck」。只需要「固定種子、失敗時印出可重現的種子」兩個功能，一個 30 行的檔案就夠了。
- **不用 VRP 套件**：文件中只寫了「不使用」，沒有寫原因。🔍 推斷：規格要求自行實作（驗收條件中有「與參考實作比對」、「與窮舉最佳解比對」，這些在用黑盒套件時意義不大），而且時間相依行車時間、休息規則、必送／選送字典序目標的組合，現成套件不一定直接支援。

## 3. 部署形式

- **Unpackaged、自含（self-contained）**：`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`、`SelfContained=true`（`src/MealRoute.App/MealRoute.App.csproj`）。使用者不需要安裝 .NET 或 Windows App Runtime，也不需要 MSIX 簽章。📄 README：「不需要 Visual Studio；App 以 unpackaged 方式部署」。
- 代價：發佈資料夾較大；沒有 MSIX 的自動更新。

## 4. 建置設定

`Directory.Build.props` 對所有專案生效：

| 設定 | 值 | 效果 |
|---|---|---|
| `TreatWarningsAsErrors` | `true` | 任何警告都會讓建置失敗 |
| `AnalysisLevel` | `latest-recommended` | 啟用 .NET 分析器的建議規則 |
| `Nullable` | `enable` | 可空參考型別檢查 |
| `Deterministic` | `true` | 相同原始碼產生相同組件 |

部分專案用 `NoWarn` 關掉少數規則，並寫明原因（例如 App 專案：「XAML-generated code triggers some analyzers」；測試專案：「relax a few analyzers that are noise in tests」）。

## 5. 版本注意事項

- **.NET 9 是 STS（標準期支援）版本**，支援期比 LTS 短。🔍 若專案要長期維護，需要規劃升級到下一個 LTS。
- **WinUI 3 unpackaged 的列印**：使用 `PrintManagerInterop`，若列印對話框打不開會提示改用 CSV／GeoJSON 匯出。📄 README「已知限制」。
- **Windows 10 1809 以上（x64）**：`TargetPlatformMinVersion` 為 `10.0.17763.0`。📄 README「建置與執行」。
- `src/MealRoute.App/MealRoute.App.csproj` 的 `Platforms` 包含 ARM64，但 README 與 CI 只發佈 win-x64。🔍 ARM64 未經測試。
