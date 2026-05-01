# 可行性分析

## TL;DR

**核心構想可行，技術選型合理，但有幾個被低估的風險，動工前需正視。**

JSON-driven CPU spec + LLVM JIT 的路徑在學界與工業界都有先例（Ghidra SLEIGH、QEMU TCG/decodetree、Sail、Ryujinx ARMeilleure 等），不是空中樓閣。但前期 Gemini 對談中對 LLVM 的描繪過於樂觀，實務上 LLVM 不是「全部丟給它就好」的銀彈。下面分項評估。

---

## ✅ 高度可行的部分

### 1. JSON 資料驅動 CPU 規格

- **先例**：Ghidra **SLEIGH**、劍橋 **Sail**、**ArchC**、QEMU **decodetree** 都在做類似的事
- **ARM7TDMI 適合度**：高。RISC、固定長度、規律編碼，10 種左右 encoding format 涵蓋 90% 指令
- **Encoding-based 描述**：認知正確（不窮舉 opcode，描述格式）

### 2. C# + LLVMSharp 整合

- ✅ **已驗證**（Phase 0 完成）：**LLVMSharp.Interop 20.x + libLLVM 20 +
  .NET 10** 組合在 Windows 11 跑通。CLI `aprcpu --jit-only` 與
  `aprcpu --spec ... --output ...` 都穩定。
- 後續 Phase 2.5 在這個組合上產出 ARM7TDMI 完整 ISA 的 LLVM IR、159 個
  測試全綠、`Verify()` 0 diagnostics — 證明這個 stack 在實作規模成長
  後仍可信賴。

### 3. ARM7TDMI 規格資源充足

- ARM 官方 ARMv4T Reference Manual、ARM7TDMI Datasheet 齊全
- **GBATEK**（社群維護的 GBA 完整規格）細節極豐
- 既有開源實作（mGBA、VBA-M、NanoBoyAdvance）可逆向比對

### 4. 測試 ROM 可用

- `arm.gba` / `thumb.gba`（armwrestler）
- FuzzARM、mGBA test suite
- 可用 mGBA / NanoBoyAdvance 當作行為比對的 reference

### 5. GBA 效能門檻不高

- CPU 只有 16.78 MHz
- 即使 pure interpreter 在現代機器都跑超速數倍
- **JIT 純為「學習與優雅」**，不是「為了跑得動」— 這是重要的心理定位

---

## ⚠️ 被低估的風險

### 1. LLVM 編譯時間可能拖累遊戲體驗（最大風險）

**Gemini 對談中沒有充分強調這點。**

- LLVM 設計初衷是 AOT C++ 編譯，每個 pass 都很重
- GBA basic block 通常很短（10–30 條指令），LLVM 編譯一個 block 可能要數毫秒
- 遊戲跑起來時持續遇到新 block，會出現 **stutter（卡頓）**
- **Ryujinx 為何不用 LLVM**：他們試過，發現編譯太慢，所以自己寫 ARMeilleure（輕量化 IR + 快速 codegen）
- **Dolphin 為何不用 LLVM JIT**：同理，曾經 LLVM-IL 後端被移除

**緩解方案**：
- LLVM 開 `-O0` 或 `-O1`，不開 `-O3`
- 只對熱點 block 升級 OptLevel（tiered compilation）
- 接受偶發 stutter（本專案是研究/學習取向，可以容忍）
- 後備：改用 .NET `System.Reflection.Emit` / `DynamicMethod` 做 IL JIT，效能對 GBA 也夠

### 2. Self-Modifying Code (SMC) 與 Cache Invalidation

GBA 部分遊戲會把 ROM 程式碼搬到 IWRAM 執行，甚至在執行中改寫：
- 必須對「已編譯區域被寫入」攔截 → invalidate cache
- 攔截機制本身有效能成本（write barrier）
- **規劃時必須視為核心問題**，不是 day-2 後話

### 3. Indirect Branch（間接跳轉）的 dispatch

- `BX R0`、`MOV PC, Rx`、function pointer call：編譯期不知目標
- 需要 block lookup table + 退出 JIT 回 host 的機制
- 設計不好就變成每個 block 都要回 host 查表，吃光 JIT 紅利
- 進階優化：block linking（直接 patch native call 跳到下一個 block）

### 4. PPU 即使簡化仍有底線

「Framebuffer 驗證」聽起來簡單但仍需要：
- VRAM 寫入攔截（CPU 寫 VRAM → 標記 dirty 或觸發更新）
- LCD 時序基本概念（VBlank / HBlank 中斷）— 多數 ROM 啟動需要 VBlank IRQ 才能繼續
- DISPCNT、DISPSTAT、VCOUNT 暫存器
- 「最小驗證」的範圍要明確界定（見 Phase 8）

### 5. JSON 描述能力的天花板

- ARM Barrel Shifter、LDM/STM register list、PSR transfer 隱藏在 Data Processing 編碼空間裡、不對齊讀取的 rotated read… 這些都是 edge case
- JSON 表達力不夠時會回到「在 micro-op 裡寫死特殊邏輯」
- 結果可能變成「JSON 大部分是模板，但少數指令還是要硬編 C# handler」
- **這不是失敗**，但要心理準備：100% 純 JSON 不切實際

### 6. 工作量估計被低估

參考既有專案規模：
- **mGBA**：~10 年、純 interpreter、團隊維護
- **NanoBoyAdvance**：~3-4 年、cycle-accurate、單人主導
- **ARMeilleure**：Ryujinx 團隊多年迭代

本專案是 **JSON 框架 + LLVM JIT + GBA 模擬**三件事疊加，單人業餘進度合理估計：
- **6 個月**：能跑簡單 ARM 測試 ROM、基本 framebuffer
- **12-18 個月**：跑得動主流商業 GBA ROM 進入遊戲畫面
- **>2 年**：高相容性、無顯著 bug

要嘛接受長期投入，要嘛把目標砍小。

### 7. LLVMSharp 的維護不確定性 — ✅ 已緩解

- 已固定鎖定 **LLVMSharp.Interop 20.x + libLLVM.runtime.win-x64 20.x**
- Phase 0/1/2/2.5 全程運作穩定，沒踩到 binding bug
- 後備方案（直接 P/Invoke libLLVM 或改 ClangSharp）暫不需要

---

## 戰略選項

### Plan A：完整目標（理想但長期）
按 roadmap 完整跑，預期 12–18 月見到 GBA 跑遊戲。

### Plan B：階段性可交付 ✅ **採用中**

1. **第 1 階段**（~3 月）：JSON → LLVM IR CLI 工具獨立可用 ✅ 已完成（比預期快）
2. **第 2 階段**（~6 月）：接 .NET 直譯器跑通 ARM 測試 ROM（不上 JIT）— 進行中（Phase 3）
3. **第 3 階段**（~12 月）：上 LLVM JIT，跑 GBA homebrew
4. 每階段獨立 demo / 開源，士氣維持

> **Update（2026-05）**：Phase 0/1/2/2.5/2.6 已完成，比 Plan B 原估
> 快不少。Phase 2.5 把 ARM7TDMI 完整 ISA 都寫進 spec 並通過 159 個
> 測試。下一步 Phase 3（host runtime + 直譯器）後，將插入 Phase 4.5
> 用 GB LR35902 驗證 framework 真的可換 CPU。

### Plan C：研究取向（最低風險）
只做「ARM7TDMI JSON spec + LLVM IR generator」CLI 工具，當作論文/部落格/開源工具發布。不做完整模擬器。

---

## 必須在動工前回答的問題

1. **LLVM 版本鎖定**：選 LLVM 17 還是 18？對應 LLVMSharp 版本？
2. **JIT 後備計畫**：若 LLVM 編譯太慢，是否能接受改 IL JIT？
3. **時程預期**：能否接受 12–18 個月才看到 GBA 畫面？
4. **目標明確度**：要做完整 GBA 模擬器，還是研究型 CPU spec 工具？

---

## 結論

構想本質可行，技術選型合理。**前提是**：

1. 對 LLVM 編譯時間風險有正確認知
2. 設定階段性可交付目標，避免單一大爆炸
3. 接受 100% 純 JSON 不切實際，少數指令會落到 C# handler
4. 估計工作量至少 12 個月起跳（業餘投入）

如果以上能接受，這是個原創性高、學習回報極大的專案。即使最終沒做出完整 GBA 模擬器，光是「JSON-driven ARM7TDMI 規格 + LLVM IR generator」本身就是社群可用的工具。
