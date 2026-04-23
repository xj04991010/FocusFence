# 📊 FocusFence

FocusFence 是一款專為 Windows 打造的開源桌面生產力引擎。它透過「分區收納 (Zones)」重塑桌面生態，將檔案管理、番茄鐘、沉浸底噪與備忘錄整合進高度自定義的透明視窗中，極小化視覺干擾，最大化工作效率。

![FocusFence Header](https://raw.githubusercontent.com/xj04991010/FocusFence/main/FocusFence.ico)

## ✨ 核心功能

*   **📦 自動化分區收納 (Focus Zones)**
    *   在桌面建立透明收納區塊，依專案或工作流分類。
    *   **支援拖曳歸檔**：將檔案拖入區塊即可自動移至對應的底層資料夾。
*   **⏲️ 任務級番茄鐘 (Pomodoro Timer)**
    *   各分區獨立配置計時器，精準追蹤單一任務的投入成本。
    *   支援自定義「專注/休息」週期，並內建週目標進度視覺化。
*   **🎧 沉浸底噪 (Ambient Soundscapes)**
    *   內建**白噪音 (White Noise)** 與**褐色噪音 (Brown Noise)** 播放功能。
    *   支援與番茄鐘連動，有效遮蔽環境雜音，協助大腦快速進入心流狀態。
*   **📝 分區便利貼 (Sticky Notes)**
    *   綁定於個別分區的快速備忘錄，隨手記錄待辦事項與專案靈感，不干擾主畫面。
*   **📂 零摩擦檔案操作**
    *   **快速導航**：在分區內「左鍵雙擊」直接開啟對應子資料夾。
    *   **原生解壓縮**：右鍵選單內建「📦 解壓縮」功能，跳過開啟檔案總管的繁瑣步驟。
*   **🎨 沉浸式 UI 體驗**
    *   採用磨砂玻璃 (Glassmorphism) 與動態配色。
    *   **智慧休眠 (Smart Dormancy)**：閒置時視窗自動收縮隱藏，確保桌面維持乾淨俐落的視覺。

## 🚀 快速上手

1.  **安裝**：前往 [Releases](https://github.com/xj04991010/FocusFence/releases) 下載最新版安裝包。
2.  **初始化**：啟動後會自動生成預設工作區 (Inbox / Active / Arsenal)，你也可以透過控制台自訂新分區。
3.  **收納**：將散落桌面的檔案直接拖入對應區塊，FocusFence 會接手後續的實體檔案整理。
4.  **專注**：點擊區塊標題啟動番茄鐘與底噪，進入深度工作模式。

## 🛠️ 技術架構

*   **語言**: C# 12.0
*   **框架**: .NET 9.0 (WPF)
*   **介面**: Vanilla XAML / CSS-like Styling
*   **數據儲存**: JSON-based Config Service

## 🤝 參與貢獻

這是一個開源專案，我們歡迎任何提升效率的 PR 與建議：
*   **🐛 Bug 回報**: 發現問題？請開啟 [Issue](https://github.com/xj04991010/FocusFence/issues)。
*   **💡 功能建議**: 有優化工作流的新點子？歡迎透過 Issue 討論。
*   **💻 提交程式碼**: 請 Fork 本專案並發送 Pull Request。

## 📄 授權協議

本專案採用 [MIT License](LICENSE) 授權。

---
*FocusFence - 讓您的桌面回歸純粹，讓您的專注更有價值。*
