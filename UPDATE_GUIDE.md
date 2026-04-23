# 🛠️ FocusFence Premium 更新守則 (v5.0+)

歡迎使用 **FocusFence Designer Edition**。為了維持本軟體的頂尖視覺質感與系統穩定性，請在進行任何代碼變更或版本更新時遵循以下守則。

---

## 🎨 1. 視覺與美學規範 (Aesthetic Standards)

FocusFence 的核心競爭力在於其「精品級」的 UI/UX。
- **發光效果 (Glow)**：所有倒數計時器與進度條必須維持 `DropShadowEffect` 的霓虹發光感。更新色彩時，請務必同步更新發光色彩。
- **動畫曲線**：請優先使用 `CubicEase (EaseOut)` 進行視窗或元素的移動。避免使用死板的線性動畫。
- **內容優先 (Content First)**：
    - 懸浮計時器應保持預設隱藏控制項，僅在 `MouseEnter` 時淡入。
    - 縮放比例必須鎖定在 **2.5 : 1**，確保在任何尺寸下都不會產生佈局堆疊。

## 🚀 2. 打包與發佈流程 (Distribution Workflow)

當您完成功能開發並準備對外發佈時，請執行以下步驟：

1. **版本更新**：在 `FocusFence.csproj` 中更新 `<Version>` 標籤。
2. **正式打包**：
   - 點擊控制台下方的 **「🛠️ 打包並固定到工具列」** 按鈕。
   - 系統會執行：`dotnet publish -c Release -r win-x64 --self-contained true`。
3. **交付物**：
   - 打包產出的單一 `FocusFence.exe` 檔案即為完整程式，不需安裝、不需依賴環境。

## 💾 3. Git 版本控制規範

為了確保開發軌跡清晰，請遵循以下 Commit 格式：
- **`Feat:`** 新功能 (例如：`Feat: 新增霓虹發光特效`)
- **`Fix:`** 修復 Bug (例如：`Fix: 解決極小尺寸下的文字堆疊問題`)
- **`UX:`** 介面優化 (例如：`UX: 預設置頂控制台`)
- **`Refactor:`** 代碼重構而不改變外觀。

## ⚠️ 4. 開發環境注意事項

- **框架**：.NET 9.0 / WPF。
- **字體依賴**：本軟體優先選用 `Segoe UI Variable`。若系統無此字體，請確保有優雅的字體回退方案。
- **置頂機制**：控制台與計時器視窗應預設 `Topmost="True"`。

---
*FocusFence - 讓專注成為一種視覺饗宴。*
