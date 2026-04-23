# 📊 FocusFence

FocusFence 是一款專為 Windows 打造的開源桌面生產力引擎。它透過「分區收納 (Zones)」重塑桌面生態，將檔案管理、番茄鐘、沉浸底噪與備忘錄整合進高度自定義的透明視窗中，極小化視覺干擾，最大化工作效率。

![FocusFence Header](https://raw.githubusercontent.com/xj04991010/FocusFence/main/FocusFence.ico)

## ✨ 核心功能

*   **📦 自動化分區收納 (Focus Zones)**
    *   在桌面建立透明收納區塊，依專案或工作流分類。
    *   **支援拖曳歸檔**：將檔案拖入區塊即可自動移至對應的底層資料夾。
*   **⏲️ 任務級番茄鐘 (Pomodoro Timer)**
    *   **設計師級別體驗**：計時數字隨剩餘時間**平滑漸變 (Teal → Yellow → Orange)**，末期內建「呼吸感」縮放動畫。
    *   **高效率 Chips 介面**：提供 **15/25/50/90** 分鐘四檔專注深度按鈕，極速開啟任務。
    *   內建週目標進度視覺化。
*   **🎧 沉浸底噪 (Ambient Soundscapes)**
    *   內建**白噪音 (White Noise)** 與**褐色噪音 (Brown Noise)**。
    *   **即時音量調節**：計時窗內建 Pop-up 滑桿，支援音訊採樣級別的音量即時同步。
*   **📝 分區便利貼 (Sticky Notes)**
    *   綁定於個別分區的快速備忘錄，隨手記錄待辦事項與專案靈感。
*   **📂 零摩擦檔案操作**
    *   **快速導航**：在分區內「左鍵雙擊」直接開啟對應子資料夾。
    *   **原生解壓縮**：右鍵選單內建「📦 解壓縮」功能。
*   **🎨 極致設計美學 (Aesthetic Excellence)**
    *   採用**多層次磨砂玻璃 (Glassmorphism)** 與非線性動畫 (CubicEase)。
    *   **極致懸浮感**：優化陰影裁切演算法，實現平滑圓角與專業深度視覺。

## 🚀 快速上手

1.  **安裝**：前往 [Releases](https://github.com/xj04991010/FocusFence/releases) 下載最新版安裝包。
2.  **初始化**：啟動後會自動生成預設工作區 (Inbox / Active / Arsenal)。
3.  **收納**：將檔案直接拖入對應區塊，FocusFence 會接手後續整理。
4.  **專注**：點擊區塊標題啟動番茄鐘，進入深度工作模式。

## 🛠️ 技術架構

*   **語言**: C# 12.0
*   **框架**: .NET 9.0 (WPF)
*   **介面**: Vanilla XAML / CSS-like Styling
*   **數據儲存**: JSON-based Config Service

## 🤝 參與貢獻

這是一個開源專案，我們歡迎任何提升效率的 PR 與建議：
*   **🐛 Bug 回報**: 發現問題？請開啟 [Issue](https://github.com/xj04991010/FocusFence/issues)。
*   **💡 功能建議**: 歡迎透過 Issue 討論。

## 📄 授權協議

本專案採用 [MIT License](LICENSE) 授權。

---
*FocusFence - 讓您的桌面回歸純粹，讓您的專注更有價值。*
