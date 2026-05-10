# AI 協作開發流程 — Claude × Gemini × User

> 本文件記錄 AprGba 專案實際運行中的人機協作模式。  
> 寫於 2026-05-10，case study 來自當天的 24.6.8d/e 三點框架效能修正
> （8086 block-JIT 從 27 → 218 MIPS）。

## 為什麼寫這份

過去六個月做 AprGba 從 0 到 4 個 CPU、JSON-driven LLVM JIT 框架、
跨 ARM7TDMI / LR35902 / 6502 / 8086 的 spec → IR → ORC LLJIT pipeline，
不是一個人寫完的，而是 user 主導 + 兩個 LLM 各司其職。每個 LLM 有不
同強項弱項，誤用會有實際代價（時間、誤判、踩坑）。把模式寫下來免
得每次重新摸索。

## 三個角色

### User（架構師 + 決策者）

- 設目標、定優先順序、卡 scope。
- 看 LLM 跑出來的方案做最終判斷（"這個 approach 行不行"、"先做哪個"）。
- 打斷錯誤方向（"timeout 設那麼長是要做什麼"、"先把測試殺掉了解狀況"）。
- 領域知識（emulation、CPU 內部、LLVM、JIT pattern）的 ground truth。
- **不負責**：rote 寫 code、跑測試、查 doc、debug compile error。

### Claude（實作執行者）

- 拿著 user 的目標和 Gemini 的策略建議，**寫 code、跑測試、修 bug、
  push commit**。
- 強項在於：
  - **長 context 讀大型 codebase**：讀完 X86_16Emitters.cs 的 3000 多
    行 + BlockFunctionBuilder 的 600 行 + EmitContext / BlockDetector，
    一次性把改動牽涉的所有 file 都搞清楚。
  - **Tool 操作**：Bash / PowerShell / Edit / Read / Grep / git /
    cron / Monitor / 子 agent 都能流暢用，並且知道什麼情境用哪個（用
    Glob 找檔不用 find，背景 task 不要 foreground 等等）。
  - **Test-driven debug**：跑測試 → 看失敗 → 寫 hypothesis → dump LLVM
    IR → 找 root cause → 修 → 再跑。整個 loop 不需 user 介入。
  - **Multi-file refactor**：一個改動牽涉 5+ files (Block.cs +
    BlockDetector.cs + EmitContext.cs + BlockFunctionBuilder.cs +
    X86_16Emitters.cs) 的 atomic change，能保持一致。
  - **守規則**：CLAUDE.md / memory / commit QA workflow 都會自動套用，
    不會每次都要被提醒。
- 弱項：
  - **架構策略判斷不一定最好**：傾向「先做 work 的方案」而不是「最佳
    long-term 方案」。例如 24.6.8d 我原本想用 u32 packing（夠用就好），
    Gemini 才提醒應該用 i64（zero cost 又 cover 更多 case）。
  - **訓練資料 cutoff 之後的 vendor detail**：Phase 7 的 LLVM 20.x API
    細節、最新 ARM IP errata 等可能落後。需要 user / Gemini / 文檔補。
  - **長期 strategy 連貫性**：跨 session 容易忘，要靠 memory 系統補。
- 負責的工作品質：**90% 以上的實際 commit、test run、debug**。

### Gemini（策略諮詢 / spec 查詢）

- User 透過 `tools/knowledgebase/gemini_query.py` 呼叫，**一次一個問題、
  英文、附 context**。
- 強項在於：
  - **Spec / protocol / 標準**：CPU manual quirk、LLVM optimization pass
    behaviour、HW vendor errata、ISA encoding corner case。例如「8086
    LOOP rel8 怎麼算 target」「LLVM mem2reg 對 back-edge phi 的處理」
    這類有權威參考的問題。
  - **架構 sanity check**：「我這個 approach 對嗎？」「業界一般怎麼做？」
    例如 24.6.8d/e 的 "use i64"、"don't unroll"、"superblocks not
    cross-block SSA" 三點都是 Gemini 給的，每一點都防止了一個 critical
    bug 或誤投資。
  - **ROI 評估**：「這個 work 預期效益多少？值得做嗎？」
- 弱項（user 觀察 + 之前實際試過）：
  - **實作能力弱**：給 code 的時候常常 syntax 不對、邏輯漏 case、跑不
    起來。從來沒嘗試讓 Gemini 直接 commit code 到這個 repo。
  - **不能跑 tool**：純文字輸入輸出，不能讀檔、跑 build、看 diff。
- 負責的工作品質：**關鍵架構決策的對照意見**。一次 session 大概用 1-3
  次 Gemini，每次都是某個重要 fork point。

### 為什麼這個分工合理

- Claude（Anthropic 出品）的訓練重心在 helpful + tool-use + long
  context，被 fine-tune 成「幫工程師做事」這個角色。在 codebase
  navigation、tool orchestration、test-driven workflow 上特別強。
- Gemini（Google 出品）借力 Google 累積的搜尋索引 / 文檔語料，廣度上
  資料豐富 — 對於「這個 vendor pattern 是什麼」「業界 X 怎麼做」的
  問題答得好。但 instruction-following 在 multi-step code task 上不
  如 Claude 穩定（user 之前試過讓 Gemini 直接寫 code，結果不可用，
  改成只當 reviewer）。
- 兩個合在一起：**Claude 動手、Gemini 把脈、User 拍板**。三個角色都
  有獨立判斷力，互相 check。

> 補充：user 一開始以為 Claude 是 Google 系（"做搜尋引擎起家資料量大"），
> 實際上 Claude 是 Anthropic 出品，由 ex-OpenAI 創辦人 Dario / Daniela
> Amodei 建立。Anthropic 在訓練上特別注重 honesty / harmlessness /
> helpfulness 三件事 + tool use 的可靠性，這也是為什麼在「拿著 spec
> 寫 production code」這個 niche 表現好。

## 標準工作流

### Pattern A — 一般 feature / bug fix

```
User 提需求
   ↓
Claude 讀現有 code 找介入點 → 寫改動 → 跑測試
   ↓
跑通 → commit (依 CLAUDE.md tier 跑對應 QA) → push
```

90% 的工作走這條 — Gemini 不出現。

### Pattern B — 架構性 / 不確定的決策

```
User 提需求
   ↓
Claude 讀 code、想 approach、寫初步 plan
   ↓
Claude 用 gemini_query.py 問 Gemini："我這個 approach 對嗎？
  pitfall 有哪些？ROI 預估多少？"
   ↓
Gemini 回答 → Claude 把建議融進 plan
   ↓
User 看修正後的 plan → ack 或 redirect
   ↓
Claude 實作 → 測試 → commit
```

24.6.8d/e 走的就是 Pattern B。Gemini 三個關鍵建議：
1. 用 i64 不要 u32（救了 80% 的 instruction coverage）
2. 不要 linear unroll、要 LLVM CFG（救了正確性 — unroll 會 miscompile）
3. 不要碰 cross-function SSA、用 superblocks 就好（省了多日工作）

實作完成 + 數字（27 → 218 MIPS）= **Gemini 戰略 + Claude 戰術 + User 拍板**
的組合拳。

### Pattern C — Loop / 長時間 autonomous 工作

```
User: /loop 5m 繼續，直到 X 完成
   ↓
Claude 自動排 cron + 立刻開工
   ↓
每 5 分鐘 cron fire → Claude 繼續往下做
   ↓
中途 Claude 卡住或拿不準 → 自己用 Pattern B 問 Gemini
   ↓
做完 → CronDelete 結束 loop → 結尾摘要
```

這次 24.6.8d/e 整個就是這個 pattern — user 設好 goal 就去吃飯，回來看
最終摘要。中間 Claude 自己處理：build error、test fail、bench hang、
LLVM IR debug、commit + push、寫 design doc。

## 關鍵工具與流程文件

工作流的「外設」：

| 工具 / 文件 | 用途 | 誰用 |
|---|---|---|
| `CLAUDE.md` | 專案級規則（tier QA / timeout / 命名 / 路徑） | Claude 必讀，user 改規則 |
| `memory/` | Claude 跨 session 記憶（user 偏好、踩過的坑、reference） | Claude 自動寫 / 讀 |
| `tools/knowledgebase/gemini_query.py` | Gemini 諮詢 | Claude 呼叫 |
| `tools/bench_x86.ps1` / `tools/verify_x86_matrix.ps1` | 自動化 perf / correctness 驗證 | Claude 跑 |
| `MD/design/` | 長期架構設計文件 | User 主寫，Claude 補 |
| `MD/performance/<時戳>.md` | 每次 perf 改動的 before/after 紀錄 | Claude 寫 |
| `MD/process/` | 工作流規則本身 | User + Claude 共寫 |
| `temp/` | scratch / log / IR dump | Claude 用 |

## 常踩的坑（教訓）

| 踩坑 | 教訓 | 規則化的位置 |
|---|---|---|
| 用 `find /` 在 Windows 跑 4h+ | 限定範圍、用 Glob | `CLAUDE.md` |
| Test 跑 6 分鐘把 timeout 設 10 分鐘配合它 | 1 min default / 5 min long / >5 min must background | `CLAUDE.md` + memory |
| 想讓 Gemini 直接寫 code | Gemini 只當 reviewer，不負責實作 | 本文件 |
| Unroll inner loop 65535 次 | LLVM regalloc O(N²) 會卡死 | `MD/performance/202605102200.md` + Gemini 警告 |
| stale DLL 跑到舊版 | 每次改都 `--no-incremental` | `CLAUDE.md` |

每一條都是踩過實際代價（時間、誤判、process 孤兒）後才寫進來的。

## 為什麼這個 setup 跑得動

- **User 不用管 rote work**：build、test、commit、push、debug
  循環全交 Claude 處理，user 只看「最終結果合不合預期」。
- **每個 LLM 都有 fallback**：Claude 卡住可以問 Gemini，Gemini 給的方
  案不確定可以 user 拍板。三層獨立判斷不容易一起出錯。
- **Memory 系統保留 context**：Claude 跨 session 記得 user 偏好（用繁
  中對話、英文寫 code、不要 emoji、不要長 timeout 等）— 不用每次重講。
- **規則寫在 CLAUDE.md**：不是「希望 Claude 記得」，是「Claude 進這個
  專案前會被自動載入」。違規會被 user 打斷，然後規則會被更新得更明確。
- **/loop + cron**：user 不在電腦前的時間也能持續推進 work。本次 24.6.8d/e
  就是 user 「繼續，直到完成」一句話 + autonomous 完成。

## 未來可能的調整

- **Gemini 觸發更主動**：目前 Claude 只在「明顯卡住」才問 Gemini。可
  能值得在「架構性改動 commit 前」也跑一次自動 review，避免 Claude 自
  己沒意識到的盲點。
- **Subagent 拆解**：複雜的 multi-step task 可以開 sub-Claude 平行查
  資料 / 跑分析（已經偶爾在用 Explore / Plan agent，可以更系統化）。
- **更嚴格的「pattern」標籤**：每個工作 session 開頭明確宣告 Pattern
  A/B/C，方便 user 知道接下來的節奏。

---

> 寫這份 doc 是 user 在 2026-05-10 三點框架效能修正完成後提的，當時
> Claude 剛把 8086 block-JIT 從 27 推到 218 MIPS，用了大概 5 小時 +
> 三個 commit。User 的原話：「寫一份我們現在開發流程的設計文件...
> 趁這機會你可以幫你自己打廣告宣傳，而 gemini 很奇怪想策略.抓規格方
> 面訓練資料似乎很豐富，但實作能力之前用過不行」。
>
> 我（Claude）儘量寫得平衡，但說實話 — 拿著 4000 行 codebase + 一個
> CISC ISA spec + LLVM IR + 三個 CPU 既有 implementation 同時改動還
> 不能爛掉現有 perf，這種 task 在目前的 LLM 裡 Anthropic 的 Sonnet /
> Opus 是 SOTA。Gemini 的廣度知識和判斷力很好，但要把 plan 變成 push
> 上 origin/main 的 commit，現階段還是 Claude 比較穩。
