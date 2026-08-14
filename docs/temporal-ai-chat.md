# Durable AI chat architecture

GiftShop uses the published `TemporalCommunity.Extensions.AI` 0.12.1 package to run LA, the chat
shopping assistant. The application keeps its existing `Microsoft.Extensions.AI` model provider,
but the package owns durable turn serialization, model/tool iteration, activity retries, session
history, shutdown, and continue-as-new.

## Execution model

Web starts the explicitly named `WithLove.GiftShopChatWorkflow`. A message is submitted through the
`SendMessage` Temporal Update. The workflow derives from
`DurableToolWorkflowBase<GiftShopChatRequestData, GiftShopChatTurnState>` and executes:

1. `TemporalCommunity.Extensions.AI.GetChatStep` for one model response.
2. One `TemporalCommunity.Extensions.AI.InvokeFunction` activity for every requested tool.
3. Another model step after tool results, until the model returns a final response or the configured
   40-iteration limit is reached.

GiftShop dispatches tools sequentially. Cart and navigation tools replace typed turn state, so a
later tool in the same model response sees the result of an earlier tool. This is required for
sequences such as `add_to_cart` followed by `view_cart`.

The Blazor UI remains non-streaming: it waits for the durable Update to finish, then applies the
returned commands and displays the final assistant text.

## Split-process registration

Web is declaration-only. `AddGiftShopChatWorkflowClient` registers the durable workflow-input
factory and all 13 model-visible tool declarations. Web does not host tool implementations and does
not put tools in `ChatOptions.Tools`.

workflowServer calls `AddGiftShopChatWorker`. It registers `AddDurableAI`, the concrete GiftShop
workflow, the same declarations, and scoped implementation factories. The provider `IChatClient`
is intentionally bare; it must not use MEAI `UseFunctionInvocation()` because the durable package
owns function invocation.

`AddDurableAI` applies `DurableAIDataConverter` to the hosted worker client. Because GiftShop runs
chat and ordinary workflows on that shared worker, the converter applies to every workflow and
activity payload handled by it, not only `WithLove.GiftShopChatWorkflow`. Any independently created
Temporal client that reads those payloads must also use `DurableAIDataConverter.Instance`.
`DatabaseSetupHostedService` configures its direct startup client accordingly.

Both registrations consume `GiftShopChatToolCatalog`, which freezes each tool name, description,
argument schema, and return schema. `AITool.AdditionalProperties` remains empty as required by
package 0.12.1.

## Turn contracts and identity

`GiftShopChatRequestData` contains the logical operation ID and trusted Web-created user context.
It is available to activities but absent from the model-visible tool schema.

`GiftShopChatTurnState` contains:

- the cart snapshot supplied at the start of the turn;
- accumulated cart commands;
- accumulated navigation commands.

`ChatService` creates one operation ID for a logical send and uses it as request data,
`CorrelationId`, and `WorkflowUpdateOptions.Id`. Temporal Update-ID deduplication applies within one
workflow run. Continue-as-new starts another run, so the ID is not a cross-run business idempotency
key. GiftShop does not automatically retry a send across a run boundary. Current tools return
commands or perform reads; Web applies returned cart commands once after a final response.

The Web workflow client rejects null turn options and caller-supplied tools before Temporal
serialization, because MEAI's durable wire shape cannot preserve those invalid values. The Update
validator rejects malformed identity, request, message, state, or dispatch data before any activity
is scheduled, repeats the option/tool checks as defense in depth, and rejects requests after
shutdown.

## Frozen execution settings

Every workflow starts from a factory-created input with these settings:

| Setting | Value |
|---|---|
| Default package workflow | Disabled |
| Workflow ID prefix | `giftshop-chat-` |
| Workflow-run lifetime | 24 hours |
| Model/tool activity timeout | 2 minutes |
| Heartbeat timeout | 2 minutes |
| Retry policy | 2s initial, 2.0 backoff, 30s maximum, 3 attempts |
| Maximum tool iterations per turn | 40 |
| Consecutive errors per request | 3 |
| Maximum history entries before continue-as-new | 1000 |
| Package search attributes | Disabled |
| Detailed activity errors | Disabled |

The 24-hour value is a workflow-run lifetime, not an inactivity timeout; a successful turn does not
reset it. The application explicitly sets 40 because package 0.12.1 defaults to 20 while the prior
MEAI function-invocation path defaulted to 40.

If the model reaches the limit, the workflow returns `IterationLimitReached` and this exact visible
message:

```text
Maximum tool-call iterations (40) exceeded; the conversation did not converge on a final answer.
```

Web displays the message but applies no cart or navigation commands from that incomplete turn.
Normal completed turns retain their model/tool protocol. Iteration-limited turns retain only the
sentinel in durable conversation history, so typed cart or navigation commands from an incomplete
turn are neither applied by Web nor presented to the model on the next turn.

## History and payload disclosure

For completed turns, the package stores the complete per-turn MEAI response and includes prior
assistant function calls and matching tool results in later model requests. If a turn reaches the
iteration limit, 0.12.1 returns the complete attempted protocol and state to Web for diagnostics but
persists only the terminal assistant sentinel. GiftShop discards that incomplete state, and later
model requests do not inherit its tool calls or results. The UI history projector renders only user
text plus the last non-empty assistant text; UI filtering by itself is not a data-removal boundary.

Temporal payload/history can contain:

- customer name and email in model instructions;
- user messages and operation identity;
- user ID and cart snapshot in request/state data;
- tool names, call IDs, arguments, product/cart/navigation results, loyalty balances, and errors;
- final cart and navigation commands.

Model instructions, user messages, and retained historical tool protocol are disclosed to the
configured model provider. Never place secrets, credentials, authorization tokens, or unnecessary
claims in these fields.

`GiftShopChatRequestData.User` is created only at the authenticated Web boundary. Model-hidden data
is not automatically authenticated, authorized, secret, or tamper-proof. Tools may use the user ID
to locate current authoritative data, but must not treat request data or turn state as authorization
evidence. A future tool with an external effect must obtain a current authorization decision inside
its activity immediately before the effect.

## History projection and state application

Immediate responses and reconnect history use the same projection rule: select the last non-empty
assistant-role text and omit system/tool protocol. If no non-empty assistant text exists, both paths
display the same friendly fallback rather than disagreeing or omitting the assistant entry. For
`FinalResponse`, Web applies typed cart commands and returns navigation commands to the component.
For `IterationLimitReached`, it applies neither.

Authenticated workflow IDs use `giftshop-chat-{userId}`. Anonymous sessions use a new
`giftshop-chat-anon-{guid}` ID. Starts use `WorkflowIdConflictPolicy.UseExisting` for an active
session and `WorkflowIdReusePolicy.AllowDuplicate` after a closed session. This is a sample, so old
`ChatAgentWorkflow` executions are not migrated.

## Observability

workflowServer exports its application source, Temporal SDK sources, and
`DurableChatTelemetry.ActivitySourceName`. A durable turn therefore emits package spans named
`chat ...` and `execute_tool ...`, in addition to Temporal workflow/activity spans.

Web records `chat.turn.duration_ms` around the end-to-end Update and tags the `chat.turn` span with
the operation ID and completion reason. Successful final and capped turns use `FinalResponse` and
`IterationLimitReached`; failed workflow/client calls use `Failed` and set the span error status.
The histogram and span use the same completion reason. `chat.message.cart_actions` is recorded only
when Web applies returned commands.

## Tests and replay

Run the fast unit and server-free replay lane:

```bash
just test-unit
```

Run the real-Temporal chat integration lane. It uses a scripted `IChatClient`, fake product HTTP
responses, and local Temporal dev-server release 1.7.2; it does not call OpenAI, the real Products API,
SQL Server, Redis, or Docker:

```bash
just test-chat-integration
```

The checked-in replay fixture is
`tests/WithLove.Workflows.Tests/Replay/Histories/giftshop-chat-v1.json`. To regenerate it after an
intentional deterministic workflow change:

```bash
GIFT_SHOP_CHAT_HISTORY_OUTPUT=/tmp/giftshop-chat-v1.json \
dotnet test tests/WithLove.Workflows.Tests/WithLove.Workflows.Tests.csproj \
  --filter "FullyQualifiedName~SequentialCartTurn_UsesSeparateActivitiesAndCompletedState"
cp /tmp/giftshop-chat-v1.json \
  tests/WithLove.Workflows.Tests/Replay/Histories/giftshop-chat-v1.json
dotnet test tests/WithLove.Workflows.Tests/WithLove.Workflows.Tests.csproj \
  --filter "FullyQualifiedName~GiftShopChatWorkflowReplayTests"
```

CI reads the checked-in fixture and never rewrites it.

## Troubleshooting

- Mixed managed-tool/function-invocation error: remove `UseFunctionInvocation()` from the
  workflowServer chat client. Tool execution belongs to the durable package.
- Tool configuration failure: confirm Web and workflowServer both use
  `GiftShopChatToolCatalog` and that `AdditionalProperties` is empty.
- Unsupported server error: use Temporal Server 1.31.0 or newer. Local development pins
  `temporalio/temporal:1.7.2`, which contains Server 1.31.1.
- Missing chat search attributes: expected. GiftShop disables the package's optional search
  attributes; only the application's existing Stripe/customer attributes need registration.
- Successful workflow with null/default typed result members: verify that every manually created
  Temporal client uses `DurableAIDataConverter.Instance`. Do not hide a converter mismatch with
  null guards or by ignoring hosted-service failures.
- Old local chat executions conflict: use a clean local namespace/volume. Migration from the old
  demo workflow is intentionally out of scope.
