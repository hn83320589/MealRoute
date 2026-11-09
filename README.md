# MealRoute 社區送餐排班台

[![CI](https://github.com/hn83320589/MealRoute/actions/workflows/ci.yml/badge.svg)](https://github.com/hn83320589/MealRoute/actions/workflows/ci.yml)

社區關懷據點的送餐排班桌面工具：匯入街道圖與當天名單，自動排出每台車的路線與時間，並支援當天臨時重新排程。

> 開發中。設計文件：[系統分析](docs/01-system-analysis.md)、[系統架構](docs/02-architecture.md)。

## 建置

需求：Windows 10 1809+、.NET 9 SDK。

```powershell
dotnet build MealRoute.sln -c Release
dotnet test tests/MealRoute.Tests -c Release
dotnet publish src/MealRoute.App -c Release -r win-x64 -p:Platform=x64 -o publish/win-x64
```
