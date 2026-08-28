# Code Review — `721b64f` "feat: adopt Temporal durable AI chat"

**Reviewed:** 2026-08-26 · **Branch:** `temporal-ai` (one commit ahead of `main`) · **Scope:** 39 files, +3,179 / −722

**Reviewers:** Zeus (code quality) · Athena (architecture) · Dionysus (AI/ML) · Artemis (test quality) · Hephaestus (Temporal correctness)

> **This document has been through a validation pass.** Every finding below was re-examined with a mandate to *falsify* it. Findings were refuted outright, materially rescoped, or newly surfaced — including a **P0 that changes the deploy plan**. Findings carry an explicit evidence grade; do not act on a Grade C item without confirming it first.

> **Post-publication correction pass (2026-08-26).** An external review of *this document* was verified and accepted. Four corrections were applied after publication:
>
> 1. **#29 refuted and removed** from P0 — the "load-bearing line" is a harmless no-op. Moved to the Refuted table.
> 2. **#1 and #2 re-attributed** — both are **pre-existing**, not regressions from `721b64f`. They remain worth fixing but must not block this commit.
> 3. **#8 arithmetic corrected** — `345 B × 500 = 168.5 KiB`, not 269 KiB. The claimed 256 KiB warn-threshold breach was **not** supported and has been withdrawn.
> 4. **#3 prescription revised** — the original fix would have introduced the same class of bug it was fixing. Downgraded from P0 to fail-fast hardening.
>
> See **Evidence reproducibility** at the end before citing a Grade A claim.

## Evidence grades

| Grade | Meaning |
|---|---|
| **A** | Demonstrated by execution or direct observation. Proof was produced. |
| **B** | Proven from decompiled / IL source, but not executed. |
| **C** | Inferred from reading code. **Not confirmed.** Treat as a hypothesis. |

**Verdict: mergeable.** Determinism and replay safety **PASS**. Every Temporal client construction site carries the correct converter today. No regressions to prior hardening. The core durable-chat design is sound; all findings are hardening gaps, wiring traps, or operational procedure.

After the correction pass, the section below no longer contains a merge blocker introduced by this commit:

| # | Original | Now | Why |
|---|---|---|---|
| **1** | P0 blocker | **P0 bug, pre-existing** | `ChatFab.razor` is not in the diff |
| **2** | P0 blocker | **P0 bug, pre-existing** | cart services not in the diff; parent already had unrestricted `quantity` |
| **3** | P0 | **P1 fail-fast hardening** | latent, not live; original prescription was itself unsafe |
| **28** | P0 | **P0 — stands** | the only genuine deploy-plan blocker, and it is operational |
| **29** | P0 | **REFUTED — removed** | the "load-bearing" line is a no-op |

---

## P0 — Highest severity (see the correction table above for merge-blocking status)

> ⚠️ **Read before triaging #1 and #2: both are PRE-EXISTING. Neither is a regression from `721b64f`, and neither should block this commit.**
>
> Verified against the diff. `721b64f` touched exactly **five** files under `src/WithLove.Web/`: `Program.cs`, `Services/ChatService.cs`, `Services/GiftShopChatWorkflowClient.cs`, `Services/Instrumentation.cs`, and `WithLove.Web.csproj`.
>
> - **#1** — `ChatFab.razor` is **not in the diff**. The unguarded call sites predate the commit.
> - **#2** — neither `FusionCacheCartService.cs` nor `InMemoryCartService.cs` is **in the diff**, and the parent commit's `ChatAgentActivities.cs` already declared `int quantity = 1` with no `[Range]`. The commit ported an existing unrestricted parameter; it did not introduce one.
>
> What the commit *did* change is exposure surface (the tool count went from 8 to 13) and, for #1, the number of distinct paths that can reach the crash. Fix both — but schedule them as their own work, and do not describe them as caused by this commit.

### 1. Unhandled exception terminates the Blazor circuit · **A** (throw) / **B** (teardown)
`ChatFab.razor:202,224` are bare `await ChatService.SendMessageAsync(...)`; the file's only try/catch is at 237/241 in a different method. The throw is proven by a passing test (`ChatServiceTests.SendMessage_WorkflowFailureRecordsFailedTelemetryAndErrorStatus`). Teardown chain proven from decompiled ASP.NET Core 10.0.5: no `IErrorBoundary` → `RemoteRenderer.HandleException` → `CircuitRegistry` → `TerminateAsync`. **No `ErrorBoundary` exists anywhere in `src/` and `DetailedErrors` is unset**, so the user gets the generic *"this circuit will be terminated"* bar with no diagnostics.

`GiftShopChatResponseProjector.AssistantFallback` already exists but is unreachable from this path — the fix is routing, not new code.

**Three independent paths reach this crash:** the Temporal failure above; the `ItemCount` overflow in #2; and tool-schema drift (#9), which surfaces to the customer as a bare `Workflow update failed`.

**Owner:** Aphrodite

### 2. Model-supplied `quantity` unguarded on the add path · **A**
> ⚠️ The original finding claimed `UpdateQuantityAsync` had the same gap. **That was wrong.** Both update paths guard the lower bound correctly (`quantity <= 0` removes the item — `FusionCacheCartService.cs:122`, `InMemoryCartService.cs:54`). Leave those alone.

`GiftShopChatToolCatalog` declares `int quantity = 1` with no `[Range]` (lines 128, 185); `AddItemAsync` does `existing.Quantity += item.Quantity`, unvalidated and **unchecked**. Demonstrated by 8 passing probe tests:

- Negative subtotal reachable: `quantity: -5` → `Subtotal = -124.95`
- Silent `int` overflow: `int.MaxValue` then `1` → `Quantity = -2147483648`, `Subtotal = -53,665,616,363.52`
- **`ItemCount => _items.Sum(i => i.Quantity)` uses checked `Enumerable.Sum<int>` and throws `OverflowException`.** `ItemCount` is read on every page by `CartBadge.razor:9,12` and **outside any try block** at `Checkout.razor:348` — a second unhandled-exception circuit kill, reachable from model input.
- Upper bound unguarded on **both** add and update paths.

**Fix locations (the original prescription aimed partly at the wrong site):**
- `GiftShopChatToolCatalog.cs:128` **and** `:185` — `[Range(1, 99)]` on declaration *and* implementation together (drift between them is caught by the fingerprint validator — see #9)
- `FusionCacheCartService.AddItemAsync:95`, `InMemoryCartService.AddItemAsync:31` — validate on entry, make the merge saturating not wrapping
- `UpdateQuantityAsync` — **upper bound only**
- `ICartService.ItemCount` — `Sum(i => (long)i.Quantity)` or clamp, so a bad cart cannot throw during render

*Stripe sub-claim downgraded to unverified:* `item.Quantity` reaches `SessionLineItemOptions.Quantity` unclamped (`Checkout.razor:352-356`), but live Stripe was not exercised. Stripe documents `quantity ≤ 999999`, so the likely outcome is a request error surfacing as checkout failure. **Stripe's own validation is the only backstop and its behaviour here is untested.**

**Owner:** Hephaestus

### 3. Web's converter is applied as a side effect, and the failure is silent · **A** · *downgraded P0 → P1*
> ⚠️ Rescoped. The original named the wrong leg. **Re-scope before handing to a fixer or the fix will target the wrong path.**

Web never sets `DataConverter` — the only explicit assignment in `src/` is `DatabaseSetupHostedService.cs:114`. Web acquires one as a side effect of `AddGiftShopChatWorkflowClient()`. The plugin applies it **only if the converter is still `DataConverter.Default`**, and otherwise logs and skips with no throw. Proven twice: from IL, and executed through the real DI path — adding a `PayloadCodec` or any custom converter yields `SKIPPED`, no exception.

**The blast radius is asymmetric.** Web→worker *arguments* survive (DurableAI reads case-insensitively), so `CheckoutOrderInput` from `StripeEventHandler.cs:36` is the **safe** leg. What corrupts is **worker→Web results** — the shared `ITemporalClient` used by `TemporalLoyaltyService` (`GetLoyaltyProfile`, `GetTransactionHistory`, `ReservePointsAsync`). 10 of 10 real types silently all-defaulted:

```
LoyaltyProfile { Balance = 1250, Tier = Gold }   →  { Balance = 0, Tier = Bronze }
ReservationResult { DiscountAmount = 5.00 }      →  { DiscountAmount = 0 }
CheckoutSessionInfo { AmountTotal = 12500 }      →  { AmountTotal = 0 }
```

> ⚠️ **Prescription revised, and severity lowered to P1 fail-fast hardening.** The original fix — assign `opts.DataConverter = DurableAIDataConverter.Instance` unconditionally in `AddTemporalClient` — is **wrong**. An unconditional assignment silently discards any `PayloadCodec` the app had configured (encryption, compression), which is the *same class of silent-data bug* the finding is about, just pointed the other way. The finding's real content is "a silent skip is bad", and answering it with a silent overwrite does not fix that.
>
> Note also that **the defect is latent, not live**: today Web sets no custom converter and no codec, so the plugin applies cleanly and every construction site carries the correct converter. What is missing is a guard against a *future* change making it stop.

**Corrected fix — do one of these, not the unconditional assignment:**

1. **Compose, don't overwrite.** If a codec is ever required, build the converter explicitly:
   ```csharp
   opts.DataConverter = DurableAIDataConverter.Instance with { PayloadCodec = myCodec };
   ```
   (`DataConverter` is a record, so `with` is available and preserves the AI payload converter.)
2. **Fail startup on an incompatible converter.** Assert at composition time that the resolved
   `DataConverter` is either `DataConverter.Default` (so the plugin will apply) or already carries
   `DurableAIDataConverter`'s payload converter — and **throw** if it is neither. A wiring mistake
   should stop the process, not produce defaulted business data.

Either way, add a test that asserts the resolved client's converter, so the guarantee is checked rather than assumed.

**Severity:** downgraded **P0 → P1 (fail-fast hardening)**. **Owner:** Hephaestus

### 28. NEW — the converter swap is a one-way door · **A**
*Found by Athena, independently corroborated by Artemis via a different route.*

Everyone checked the forward direction and reported the good news. **Nobody checked rollback.** Reverting `721b64f` after deploy silently corrupts in-flight state: the new converter writes camelCase + string enums; the old converter has `PropertyNameCaseInsensitive = false`, so **21 of 23 fields bind to default**. A rolled-back `StripeCheckoutOrderWorkflow` resumes with `CheckoutSessionId = null`; `LoyaltyAccountWorkflow` with `Balance = 0`. No exception, no log.

**Fix:** Ares needs a rollback procedure — either drain in-flight `StripeCheckoutOrderWorkflow` / `LoyaltyAccountWorkflow` / `CustomerOnboardingWorkflow` before reverting, or ship any rollback build with `DataConverter` pinned to `DurableAIDataConverter.Instance`.

**Corollary for #23:** any converter test must assert **`durableAI → default`**. The safe direction passes and gives false confidence — which is exactly what the current test does.

**Owner:** Ares (procedure) + Hephaestus (pinned rollback build)

---

## P1 — Before deploy

### 6. CI proves only 360 of 385 tests · **A**
Exact counts via per-project `--list-tests`: **385 total / 333 Unit / 27 chat-integration / 360 CI-covered / 25 orphaned.** `.github/workflows/` contains only `build.yml` — one job, two test steps, no other coverage. The 25 orphans (`DatabaseVerification` 6, `HealthCheck` 3, `Pagination` 4, `ResponseHeader` 4, `Search` 4, `SearchCacheInvalidation` 4) carry `Category=Integration` without `Feature=Chat` and are **excluded rather than skipped**, so nothing reports the gap. They pass when run (25/25, 39 s). **Owner:** Ares

### 7. Temporal CLI re-downloaded on every CI run · **A**
> ⚠️ "Uncached" was wrong. The CLI **is** cached at `$TMPDIR/temporal-v1.7.2` (549 MiB, version-keyed) and survives local runs — proven by a warm re-run showing identical inode and size.

`GiftShopChatTemporalFixture.cs:27` sets `DownloadVersion` with no `DownloadDirectory`, so it lands in the OS temp dir. Locally that caches fine. **In CI every job gets a fresh ephemeral runner and `build.yml` has no cache step**, so it re-fetches every run — a single network dependency that can red all 27 chat integration tests at once, as it did once during review. Frequency: at most once per test-assembly run. **Owner:** Ares

### 8. `MaxEntryCount = 1000` produces oversized continue-as-new payloads · **B** (mechanism) / **A** (behaviour)
> ⚠️ Two claims in the original were **wrong and are deleted**: "effectively dead config" and "full transcripts carry across CAN unchanged". A reviewer's earlier correction was itself refuted here.

`DefaultBoundedTrim` is called **unconditionally inside the CAN branch** on either trigger — it has no trigger parameter and cannot be trigger-gated. Body: `Math.Max(1, maxEntryCount / 2)`, `TakeLast`. Executed: `501→500, 999→500, 1000→500, 2000→500, 400→400`. So `MaxEntryCount` is **load-bearing** — it is the trim divisor on every CAN — and carried history is `min(count, MaxEntryCount/2)`.

> ⚠️ **Arithmetic corrected, and one claim withdrawn.** The published figure was wrong: `345 B × 500 = 172,500 B = **168.5 KiB**`, not 269 KiB. The stated "~12×" headroom to 2 MiB is itself consistent with 168.5 KiB (`2 MiB / 168.5 KiB ≈ 12.4`), not with 269 KiB (`≈ 7.8×`) — the two numbers in the original sentence contradicted each other, which is how the error was caught.
>
> **168.5 KiB does not cross Temporal's 256 KiB warn threshold.** That claim was unsupported and is **withdrawn**. There is no measured threshold breach in this app at `MaxEntryCount = 1000`.

**The 2 MB risk is arithmetic-only for this app:** measured 345 B/entry against real recorded history → 500 entries = **168.5 KiB**, roughly **12× under** the 2 MiB payload limit and **below** the 256 KiB warn threshold.

**Fix (now a sizing recommendation, not a defect remedy): consider lowering `MaxEntryCount` to 100–200** (carried drops to 50–100 entries). The reason is headroom and predictable CAN payloads as per-entry size grows — not a demonstrated limit breach. **Explicitly rule out `DefaultHistoryReducerKey`** — it makes the payload problem *worse*, since full untrimmed history becomes an activity input payload in addition to the CAN input. **Owner:** Hephaestus

### 10. Turn latency is unbounded and cart deltas are lost permanently · **A**
Forced to reproduce with a passing test: the workflow committed a turn carrying one `Add` action, the client received `DeadlineExceeded`, and `AddItemAsync`/`RemoveItemAsync`/`ClearAsync` **MustNotHaveHappened**. A subsequent `LoadHistoryAsync()` also applied nothing — it repopulates `Messages` only. **There is no reconciliation path; the cart delta is lost permanently, not just for that turn.**

Structural preconditions proven: the chat path sets only `ActivityTimeout` + `HeartbeatTimeout` (`GiftShopChatRegistrationExtensions.cs:49-50`) with **no `ScheduleToCloseTimeout`** — in contrast to `DatabaseSetupWorkflow.cs:18,35,52,69`, which does. No `CancellationToken` on `ChatService.SendMessageAsync`, none passed to `ExecuteUpdateAsync` — in contrast to `TemporalLoyaltyService.cs:30`, which does pass `RpcOptions`.

*Arithmetic corrected:* given #4's measured ≤26 s/step, worst case is **~17 min**, not "40 × ~6 min". **Owner:** Hephaestus

### 11. No `MaxOutputTokens` anywhere · **A**
Exhaustive grep across `.cs/.razor/.json/.props/.md` → the only hit is this review doc. `Temperature` → zero hits repo-wide. Corroborated dynamically: decoding a real recorded `GetChatStep` payload gives `options keys: ['instructions']` — `maxOutputTokens` absent. The package sets no default.

Exposure is multiplied: up to 40 unbounded model calls per turn, and `gpt-5-nano` is a reasoning model whose reasoning tokens bill as output. The same law's *"Monitor token usage in logs"* rule is also unmet — grep for `UsageDetails|InputTokenCount|OutputTokenCount|.Usage` in `src/` returns nothing, though `DurableSessionResponse.Usage` is populated and available.

*Wording correction:* the AI Integration Patterns law is **Status: Draft** — say "violates the draft law," not "the team's law." The "don't set `Temperature` on a reasoning model" caveat is **Grade C** — not verified against the API. **Owner:** Dionysus

### 12. Embedding-model drift degrades search silently · **B**
> ⚠️ The original cited `ProductsAPI:72` — a mis-cite; that line is a SQL command timeout.

Correct locations: `WorkflowServer/Program.cs:72` (`text-embedding-3-small`), `:76` (`gpt-5-nano`), `ProductsAPI/Program.cs:109` (`text-embedding-3-small`).

**The claim needed splitting — the two cases behave oppositely.** A *dimension* change is **not** silent: the column is `vector(1536)` with no `Dimensions` option requested, so a differently-sized vector fails loudly at `SaveChangesAsync`. Only a **same-dimension semantic** change is silent, and that path is source-proven: `EmbedAllProductsAsync` filters `p.Embedding == null`, so product vectors freeze at first backfill and are never refreshed, while `GenerateQueryEmbeddingAsync` re-embeds every query. Index-time and query-time models drift apart. Nothing stamps model identity on the row. Degradation surfaces only as the `maxCosineDistance = 0.8f` filter quietly returning worse results — no exception, no log, no metric.

*Fix correction:* "pin the model" is actionable only for the chat model; OpenAI publishes no dated snapshot for `text-embedding-3-small` (Grade C). The actionable embedding fix is a model-identity column beside the vector, a re-embed trigger on change, and a search-quality canary. **Owner:** Dionysus

### 30. NEW — client retry silently doubles cart quantity · **C**
The mirror image of #10. `ChatService.ApplyCartActionsAsync` carries no dedupe marker and `FusionCacheCartService.cs:95` does `existing.Quantity += item.Quantity`, so a retry where the first attempt *did* land doubles the quantity. Sits between #2 and #14, and is currently untested. **Not yet demonstrated.** **Owner:** Hephaestus

---

## P2 — Judgement calls (no code change strictly required)

### 13. Customer email is persisted into workflow history · **A** — *P3 tidy-up, not a blocker*
Validated by running real chat turns against a Temporal dev server and decoding every `HistoryEvent` proto. The probe email appears **8 times across 6 of 39 events** over two turns.

**Two independent carriers, not one:**
1. `requestData.user.email` — a structured field, verbatim
2. `chatOptions.instructions` — the fully rendered ~12 KB system prompt, **re-persisted into every `GetChatStep` activity input**

**Frequency is worse than "every turn":** once per Update, once per model step, once per tool call. Under the 40-iteration cap, one turn writes it **~82 times**.

**This invalidates the originally proposed fix.** Moving `BuildInstructions` server-side removes only carrier (2); carrier (1) is populated separately at `ChatService.cs:120`. **The cheapest correct fix is to drop `Email` from `UserContext` entirely — it has no server-side consumer.** `BuildInstructions` is its only reader; `view_loyalty_points` uses `UserId` only.

`GetHistory()` does **not** return it, which strengthens rather than weakens the finding: the durable record is invisible through every application read path — precisely the case the design doc's "UI filtering is not a data-removal boundary" warning covers.

**Proportionality — read this before escalating.** WithLove is a demo with seeded data. There is no real compliance exposure here, and namespace retention is not worth configuring for a sample app. The original framing of this as a "compliance decision" was an overreach.

The residual reason to fix it is that WithLove is a **sample people copy**, and "concatenate user PII into a system prompt persisted ~82 times per turn" is a pattern worth not teaching. The fix is one field on one record and it **also closes the prompt-injection vector in #16** — same concatenation, same fix. Do it because it is nearly free and kills two findings.

**No WebUI impact.** `UserContext` (`ChatContracts.cs:4`) is the chat DTO; its `Email` is read only by `BuildInstructions`. The WebUI's email comes from ASP.NET Identity (`Login.razor`, `ShopUser`), `CheckoutModel.BillingEmail`, and `OrderConfirmation.razor:311` (`session.CustomerDetails?.Email`, from Stripe). Nothing in the UI reads `UserContext`.

### 14. `WorkflowUpdateOptions.Id` is decorative — delete it · **A**
The GUID is minted per call at `ChatService.cs:99` and never persisted or read back. Proven: three byte-identical sends in one session produced three distinct IDs (`distinct = 3 of 3`). Temporal's Update dedup therefore has nothing to deduplicate — not just across runs as the design doc implies, but **within a run too**.

**No double-charge is possible today**, confirmed by exhaustive enumeration of all 13 tools: 5 outbound HTTP `GetAsync` calls plus 1 Temporal `QueryAsync`; no `DbContext`, `ICartService`, `Stripe`, `IFusionCache` or Redis access. Cart tools mutate only turn state.

*Wording correction:* "no tool has an external effect" is literally false — six outbound read calls, and `search_products` transitively bills an OpenAI embedding up to 40× per turn, ×3 on retry. Restate as **"no tool performs an external *mutating* side effect."**

**Action: delete `WorkflowUpdateOptions.Id`.** An idempotency key guards against duplicate *side effects*, and there are none to guard — so "implement a real idempotency key" is not a real option here, and an earlier draft of this review was wrong to offer it as one. The option currently reads as a safety mechanism, and the design doc's discussion of its cross-run limitations implies a protection that never existed even within a run. Removing it is honest; keeping it is misleading. Revisit only if a tool with an external mutating effect is ever added.

---

## P2 — Hardening

| # | Finding | Grade |
|---|---|---|
| **4** | 2-min activity ceiling. ⚠️ **Realism claim REFUTED — demoted from P1.** 12 real `gpt-5-nano` calls in production configuration measured **4.02–26.11 s**, worst case a 39th iteration at 24,134 input tokens. Against a 120 s timeout that is **4.6× headroom**. Heartbeating *is* implemented (per stream chunk) and heartbeats correctly do not extend `StartToCloseTimeout` — that structural point stands. Raising `ActivityTimeout` is **defence against provider tail latency, not a live defect**. Caveat: single-client samples from one machine | **A** (measurement) / **B** (structural) |
| **5** | Orphaned `chat-*` zombie workflows. ⚠️ **Existence UNVERIFIABLE — demoted to a pre-flight check.** Every reachable namespace was queried: Temporal Cloud key **expired 20 days ago**, the Azure RG does not exist, the `temporal-data` docker volume does not exist, and the only local `temporal.db` files belong to other repos — **zero** `chat-%` or `giftshop-chat-%` rows. Mechanism reproduced live: `pendingTask.attempt` 6→7→8 over 2.5 min, `status=RUNNING`, cause *"Workflow type ChatAgentWorkflow is not registered"*. ⚠️ **"Consuming poller slots" is REFUTED** — retries back off exponentially and append no history; the real cost is a permanently non-terminal execution plus log/metric noise. Old/new IDs do not collide (confirmed) | **A** (mechanism) / **UNVERIFIABLE** (existence) |
| **9** | Tool schema drift is invisible to compiler and startup. Induced real drift: **build succeeded, worker startup succeeded, workflow start succeeded**; only `add_to_cart` failed, non-retryable — *"Durable tool implementation 'add_to_cart' does not match frozen declaration"* — surfacing to the customer as a bare `Workflow update failed` (feeds #1). ⚠️ **Prescribed fix REFUTED: the test already exists.** `EveryCatalogDeclaration_MatchesItsWorkerImplementationSchema` (`GiftShopChatWorkflowIntegrationTests.cs:350`) was added by this very commit and executes all 13 tools. Real action: keep it in lockstep with `CreateDeclarations()`. A `JsonSchema` comparison is **not** a substitute (`DurableToolInvocationContext<,>` has an internal-only ctor → `CS1729`). **New:** the fingerprint covers Name + JsonSchema + ReturnJsonSchema only — **`Description` drift is caught nowhere** | **A** |
| **15** | `UserId` → cross-customer loyalty query. ⚠️ **Downgraded to defence-in-depth — not exploitable today.** `RequestData` is verifiably absent from the model-facing `GetChatStep` payload (`requestData: null`), and `UserId` is set server-side from `ClaimTypes.NameIdentifier`. Real gap: `ValidateSendMessage` checks ten other properties but never asserts `UserId` against the workflow ID, which already encodes the owner. Two-line fix | **A** |
| **16** | Prompt injection via `ShopUser.FullName`. **Upgraded to A** — executing test shows an injected payload reaching `ChatOptions.Instructions` verbatim, no escaping or newline stripping. Sharper than first stated: a newline terminates the intended sentence, so injected text lands *before* the trailing context — **the injection controls its own framing**. Validation is `[Required]` + `[StringLength(100)]` only; no regex, allowlist, or sanitizer. Shares a vector with #13 | **A** |
| **17** | Anonymous session leak. Confirmed: 3 simulated circuits → 3 distinct `giftshop-chat-anon-{guid}` IDs; authenticated → 1 stable ID. ⚠️ **Rescoped:** leak is per circuit *in which the chat panel is opened*, not per circuit. `AnonymousCartSession` confirmed viable as a stable seed (cookie-backed `wl-cart-id`, 30 d, `[PersistentState]`) | **A** |
| **18** | Authenticated users lose chat history. ⚠️ **Root cause corrected — the fix changes.** TTL behaviour confirmed (run-lifetime; a turn at t=12.5 s did not move a 20 s deadline; timeout completes the run; CAN *does* reset the clock). But history loss is **not** the TTL — it is call ordering at `ChatFab.razor:162-164`, where `EnsureWorkflowStartedAsync` (`IdReusePolicy=AllowDuplicate`) starts a fresh empty run **before** `LoadHistoryAsync` queries it. Swapping the order salvages only one final read; the real fix is re-hydrating `CarriedHistory` or a genuine idle timeout | **A** |
| **19** | Mid-turn cart divergence. ⚠️ **Rescoped:** `clear_cart` **did** wipe a mid-turn user addition (real clobber, demonstrated). `add_to_cart` is additive — no clobber, but quantity drift (user set 5, model intended 2, result 6). `WorkingCart`'s own XML doc acknowledges divergence and next-turn self-correction, so "no reconciliation" was too strong | **A** |
| **20** | `SendQuickAction` missing the `IsThinking` guard. ⚠️ **Rescoped:** the buttons sit inside `@if (Messages.Count == 0 && !IsThinking)`, so exploitation needs a double-click race — not wide open | **A** |
| **21** | Catch-all reports infrastructure failure as tool success. Demonstrated: with the loyalty query broken, the activity **completed on attempt 1** — `ActivityTaskFailed: 0`, none of the 3 retries fired — and the model was handed *"try again in a moment"*, advice that will never work. Temporal sees a healthy workflow. Broader than first stated: five `!IsSuccessStatusCode` guards (lines 20, 31, 43, 55, 71) never inspect the status code, so a ProductsAPI 500 is indistinguishable from a legitimate 404. ⚠️ **Cancellation half WITHDRAWN** — the method takes no token, the wrapper is parameterless, the context token is never read; there is nothing for cancellation to swallow. The package's own catch-all rethrows | **A** |
| **22** | 8 unguarded `GetProperty("id"/"name"/"price")` calls, no try/catch in `GiftShopChatToolCatalog`. Consequence (throws inside activity, burns 3 retries) proven from decompiled source | **A** (presence) / **B** (consequence) |
| **23** | Converter test coverage gap. ⚠️ **Risk re-diagnosed.** The coverage gap is real and exhaustive — only a synthetic stand-in is covered; `StripeCheckoutOrderWorkflow`, `LoyaltyAccountWorkflow`, `CustomerOnboardingWorkflow`, `DatabaseSetupWorkflow` payloads are never round-tripped and `Embedding == null` is uncovered. **But every real type round-trips correctly under a uniform converter — the converter is not the defect.** The live risk is the asymmetry in #3/#28 | **A** |

---

## P3 — Documentation & test debt

### 24. `CLAUDE.md` is materially wrong · **A**
Updated by this very commit, and wrong in four places — each verified:

| CLAUDE.md | Actual |
|---|---|
| 8 tools | **13** (4 navigation + `view_loyalty_points`) |
| `SendMessageAsync(DurableSessionRequest)` | `DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState>` |
| `GetHistory()` returns display-projected history | Returns **raw entries including tool protocol** |
| Models live in `src/WithLove.Shared/` | **No such project** (7 projects, no Shared) |

Row 3 directly contradicts the design doc's PII warning. `CLAUDE.md` auto-loads as project instructions, so it misleads every future session. **Cheapest high-leverage fix on this list.**

### 25. `docs/temporal-ai-chat.md` gaps · **A**
⚠️ Rescoped — the doc **does** document converter blast radius at lines 37–41. Genuinely undocumented: the **Web-side mechanism** (`AddDurableChatWorkflowInputFactory` → `RegisterWorkflowInputServices` → `RegisterClientDataConverterServices`) and the **silent-skip rule**. Should also record the verified good news that old PascalCase history round-trips under the new converter (30/30, thanks to `PropertyNameCaseInsensitive=true` + `JsonStringEnumConverter(allowIntegerValues)`), **and the rollback asymmetry in #28** — the same property that makes forward-compat safe is what makes rollback unsafe.

### 26. Test debt · **A**
- **No tool-failure/retry test anywhere.** The harness has no failure seam — `StartAsync(environment, chatClient, transformInput)` hardcodes the products handler. Zero retry/`ActivityFailure` assertions exist.
- **Duplicate Update ID untested** — all IDs unique.
- **Replay history is narrow** — 40 events, 1 Update, `GetChatStep→InvokeFunction×2→GetChatStep`, the only file in `Histories/`. Misses multi-iteration, CAN, and iteration-limit paths.
- **`AssertRegressionFixture` is weaker than the file it guards — demonstrated.** A 40-event history thinned to **6 events still passes**, and a regenerated 34-event history from a strictly weaker scenario passes both the fixture assertion *and* the replayer.
- **Assertions execute inside activities.** ⚠️ Corrected mechanism — not "timeouts" but exhausted retries plus `MaximumConsecutiveErrorsPerRequest`: one wrong assertion costs **24.1 s and 12 real model calls**, with the real message buried 4 levels deep.
- `Task.Delay(100)` as a negative proof (line 444).

### 27. Minors · **A** unless noted
Confirmed: update validator is *stricter* than the client tool check (`Tools is not null` vs `Tools is { Count: > 0 }`) — though the doc says "defense in depth", never "identical", so that sub-claim is rescoped; misleading null-`Options` rejection message, plus the test that locks it in; `DurableMixedPatternValidator` never fires for this registration style (**B**); stray `.Build()` (**B**); `NavigationActions` accumulated but only `[^1]` applied; `InternalsVisibleTo` layering inversion.

⚠️ **Frozen `DurableExecutionOptions`** rescoped: 24 h is a **lower** bound, not upper — every frozen option carries across CAN via `input with { ... }`, so a session that CANs more often than every 24 h **never** picks up new options.

---

## Refuted — do not action

| Claim | Why it failed |
|---|---|
| **Startup-frozen `X-WITHLOVE-API-VERSION`** | `AddHttpClient`'s configure delegate runs **once per `CreateClient`** (probe: 0 invocations at DI build; 1/2/3 after three calls), and the tool service calls `CreateClient` per invocation. `DateTime.Today` is always current. **Dropped entirely.** |
| **"`UpdateQuantityAsync` has the same gap" (#2)** | Both update paths guard `quantity <= 0`. Refuted. |
| **"`MaxEntryCount` is dead config" / "transcripts carry unchanged" (#8)** | `DefaultBoundedTrim` runs unconditionally in the CAN branch. Both claims deleted. |
| **"Add a schema-conformance test" (#9)** | The test already exists and was added by this commit. |
| **Cancellation swallowed by catch-all (#21)** | No token is bound or read; nothing to swallow. Withdrawn. |
| **"Consuming poller slots" (#5)** | Retries back off and append no history. Withdrawn. |
| **"A step exceeding 2 minutes is realistic" (#4)** | Measured 4–26 s; 4.6× headroom. Refuted by measurement. |
| **"Uncached CLI download" (#7)** | Version-keyed cache in `$TMPDIR` survives local runs. CI-only problem. |
| **"`opts.ClientOptions ??= new()` is load-bearing" (#29)** | **REFUTED from decompiled Temporalio.Extensions.Hosting 1.17.0.** The overload actually in use — `AddHostedTemporalWorker(clientTargetHost, clientNamespace, taskQueue, deploymentOptions)` — already does `options.ClientOptions = new TemporalClientConnectOptions(clientTargetHost) { Namespace = clientNamespace }` inside its own `ConfigureOptions` delegate, which is registered **before** the application's delegate and therefore runs first. By the time `WorkflowServer/Program.cs` runs `opts.ClientOptions ??= new()`, `ClientOptions` is already non-null, so the null-coalescing assignment is a **harmless no-op**. Deleting it changes nothing; `DurableAIWorkerClientConfigurator` (an `IPostConfigureOptions`, so it runs after all `IConfigureOptions`) still sees a non-null `ClientOptions` and still attaches the plugin. Finding deleted — do not add a guard or a "load-bearing" comment. |

---

## What this commit got right

- **Durability granularity.** Previously one activity ran the entire function-invocation loop under a single 2-min timeout, so a mid-loop crash replayed every model call — up to **3× the OpenAI spend**. Each model step and tool call is now independently retried.
- **Nothing lost in the port.** System prompt byte-identical; all 13 tools and summarizers preserved.
- **The 40-iteration limit and its "apply no commands" rule are enforced and rigorously tested** — 40 `GetChatStep` + 40 `InvokeFunction`, sentinel text, no cart mutation, clean next turn.
- **The replay guard is real** — mutating an activity type produced a genuine `WorkflowNondeterminismException [TMPRL1100]`.
- **A tool-conformance test shipped with the commit** and does catch real drift (#9).
- **Forward-compatible wire format** — old PascalCase history round-trips (30/30), so in-flight non-chat workflows survive the deploy. *(Rollback does not — see #28.)*
- **Strong suite.** 385 passed / 0 failed / 0 skipped locally. The previously-recorded "29 skipped Phase 4–5 integration tests" blocker is resolved.

---

## Evidence reproducibility

**Read this before citing a Grade A claim in a commit message, an issue, or a decision record.**

Most Grade A evidence in this document was produced by **throwaway probes — standalone console apps, ad-hoc test files, and scratch scripts — that were deleted after the finding was written.** Concretely, claims such as:

- "8 passing probe tests" (#2)
- "10 of 10 real types silently all-defaulted" (#3)
- "21 of 23 fields bind to default" (#28)
- "old PascalCase history round-trips 30/30" (#28, *What this commit got right*)
- "12 real `gpt-5-nano` calls measured 4.02–26.11 s" (#4)
- "the probe email appears 8 times across 6 of 39 events" (#13)
- "three byte-identical sends produced three distinct IDs" (#14)

…are **not reproducible from a fresh checkout of this repository.** Nothing in `tests/` reproduces them. Grade A here means "someone executed something and observed this", **not** "a third party can re-run it and confirm".

This is a real weakness in a document whose whole premise is falsifiability, and it is stated plainly rather than buried: an unreproducible measurement is one refactor away from being folklore. It is also why the arithmetic error in #8 survived publication — a number that no committed test recomputes cannot fail.

**Remediation in progress:** Artemis is converting the load-bearing claims into committed regression tests. Until a claim has a test behind it, treat its grade as **A (unwitnessed)** — good enough to prioritise work, not good enough to close a question or to cite as settled.

The one exception in this document is the **converter asymmetry in #28**, which is reproducible in about twenty lines against the published `TemporalCommunity.Extensions.AI` 0.12.1 package with no repository state at all: serialize a record under `DurableAIJsonUtilities.DefaultOptions` (`PropertyNameCaseInsensitive = True`, camelCase policy, `JsonStringEnumConverter` present), then deserialize under a stock `JsonSerializerOptions` (`PropertyNameCaseInsensitive = False`) and observe every property come back defaulted. The mechanism is written up in `docs/temporal-ai-chat.md`.

---

## Validation methodology

The first pass produced 27 findings from code reading, decompilation, and IL analysis. A second pass assigned every finding back to its author with a mandate to falsify it. Outcome: **2 refuted outright, 13 materially rescoped, 6 new findings, 1 mis-citation corrected** — including a P0 that changes the deploy plan and which no first-pass reviewer had looked for.

A **third pass**, run by an external reviewer against the published document rather than against the code, refuted one more finding (#29), re-attributed two P0s as pre-existing (#1, #2), corrected an arithmetic error and withdrew the claim resting on it (#8), and rejected a prescribed fix as unsafe (#3). Running totals: **3 refuted outright, 2 re-attributed, 1 fix prescription rejected.**

Three lessons worth carrying forward:

1. **Agreement between reviewers is not independent confirmation** when they share a method. The "three-way convergence" on #2 was three people reading the same code; the scope was still wrong.
2. **Verify the call site, not just the API surface.** Package XML docs correctly documented that `ValidateImplementation` and `DefaultBoundedTrim` exist, but not where they are invoked from — only IL call-graph analysis showed both fire far later than the docs imply.
3. **Cross-check findings against each other, not only against code.** #18 and #27 were mutually inconsistent in the first draft and neither author noticed.
4. **Check the arithmetic, not just the claim.** #8's own two numbers disagreed — "269 KiB" and "~12× to 2 MiB" cannot both be true. Nobody caught it for two passes because both passes were re-reading code, and the error was not in the code.
5. **Separate "is this a defect?" from "was it introduced here?"** #1 and #2 are real bugs and were correctly found, but neither came from this commit. A review that conflates severity with attribution blocks the wrong change.
6. **A prescribed fix deserves the same falsification mandate as the finding.** #3's original fix would have introduced the exact failure mode the finding described. The finding survived two falsification passes; its remedy had never been through one.
