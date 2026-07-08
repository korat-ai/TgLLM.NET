namespace TgLLM.Maf

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open TgLLM.Core
open TgLLM.FSharp

/// Bridges the leaf's observability seam to an `ILogger` — the MAF counterpart to
/// `LoggingHookObserver`/`LoggingA2uiObserver`.
type LoggingMafObserver(logger: ILogger) =
    interface IMafObserver with
        member _.OnStaleDecision(descriptor: ApprovalDescriptor) =
            logger.LogWarning(
                "MAF: a decision for request {RequestId} (tool {Tool}) in chat {Chat} is no longer pending \
                 (already decided, the run ended, or the process restarted) — acked, not resumed",
                [| descriptor.RequestId :> obj; descriptor.Tool :> obj; descriptor.Chat :> obj |]
            )

        member _.OnMalformedDecision(raw: string) =
            logger.LogWarning("MAF: a decision button carried a payload that did not parse: {Raw}", [| raw :> obj |])

        member _.OnResumeFailed(descriptor: ApprovalDescriptor, error: exn) =
            logger.LogError(
                error,
                "MAF: resuming the agent for request {RequestId} (tool {Tool}) in chat {Chat} failed",
                [| descriptor.RequestId :> obj; descriptor.Tool :> obj; descriptor.Chat :> obj |]
            )

        member _.OnEmptyTurn(chat: ChatId) =
            logger.LogWarning("MAF: a turn in chat {Chat} produced neither text nor a pending approval", [| chat :> obj |])

        member _.OnInvalidOutput(chat: ChatId, error: MafError) =
            logger.LogWarning("MAF: output for chat {Chat} failed send-side validation: {Error}", [| chat :> obj; (string error) :> obj |])

        member _.OnProjectionProblem(problem: ProjectionProblem) =
            logger.LogWarning("MAF: a tool declaration could not be projected: {Problem}", [| (string problem) :> obj |])

        member _.OnTurnFailed(chat: ChatId, error: exn) =
            logger.LogError(error, "MAF: a turn in chat {Chat} failed before it produced a reply or a pending approval", [| chat :> obj |])

/// The bridge's internal turn-processing helpers — pure with respect to MAF (no `agent.RunAsync`
/// call lives here), composing `ApprovalDetection`/`ApprovalRendering`/`ApprovalDescriptor` into
/// the sends the bridge issues. Kept separate from `MafBridge` itself so the type below stays a
/// thin state holder over these functions.
module private TurnProcessing =

    /// Builds the `[Approve][Reject]` plan for one detected approval, validating the render first.
    /// `Error` on an invalid render (over-limit body/label) or an unreachable plan-build failure —
    /// callers report it via `IMafObserver.OnInvalidOutput` rather than sending anything.
    let buildKeyboard
        (formatter: ApprovalFormatter)
        (chat: ChatId)
        (detected: ApprovalDetection.DetectedApproval)
        : Result<ApprovalRender * ToolKeyboard, MafError> =
        let render = formatter detected.Prompt

        match ApprovalRendering.validate render with
        | Error e -> Error e
        | Ok validRender ->
            let descriptor = ApprovalDescriptor.make chat detected.Request.RequestId detected.Prompt.Tool
            let argJson = ApprovalDescriptor.serialize descriptor

            match
                Plan.rows [ [ Plan.toolWithArg validRender.ApproveLabel ReservedToolNames.Approve argJson
                              Plan.toolWithArg validRender.RejectLabel ReservedToolNames.Reject argJson ] ]
            with
            | Ok plan -> Ok(validRender, plan)
            | Error e -> Error(BodyInvalid $"%A{e}")

    /// The outcome text shown once a decision resolves without raising a further approval — the
    /// agent's own reply text is appended when non-empty (quickstart's
    /// "✔ send_email approved — Email sent to alice@example.com." shape); a bare decision note
    /// otherwise.
    let outcomeText (approved: bool) (tool: string) (replyText: string) : string =
        let mark = if approved then "✔" else "✘" // ✔ / ✘
        let verb = if approved then "approved" else "rejected"
        let trimmed = if isNull (box replyText) then "" else replyText.Trim()

        if trimmed.Length > 0 then
            $"{mark} {tool} {verb} — {trimmed}"
        else
            $"{mark} {tool} {verb}."

    let failureNote (tool: string) : string = $"⚠ {tool} could not complete — the agent failed to resume."

/// Wires ONE agent to ONE bot: registers `maf-approve`/`maf-reject` into the bot's own Tool Router,
/// owns the per-chat `Conversations` and `PendingApprovals`, and exposes host-initiated runs.
/// Constructed only through `Maf.startPolling`/`startWebhook` (and their `…With` variants) — the
/// tools must be registered before either function returns, so no approval keyboard can exist
/// before its decision tools do.
[<Sealed>]
type MafBridge internal (bot: TgBot, agent: AIAgent, observer: IMafObserver, formatter: ApprovalFormatter, defaultOwner: OwnerScope voption, expiry: TimeSpan voption) =

    let conversations = Conversations()
    let pendingApprovals = PendingApprovals()
    let cts = new CancellationTokenSource()

    /// One lock per chat ever seen by this bridge, so a chat's turns (`StartRun` calls and decision
    /// resumes) run one at a time — the bridge's own equivalent of the engine's per-chat dispatcher
    /// lane, needed because `TgBot` exposes no seam to enqueue work onto that SAME lane from
    /// outside the Tool Router. This still delivers what matters (a chat's `AgentSession` is never
    /// touched concurrently); it is a DIFFERENT lock than the one button taps for
    /// `maf-approve`/`maf-reject` themselves run under (the engine's own per-chat channel), just one
    /// that serializes the SAME chat's turns against each other.
    let chatLocks = ConcurrentDictionary<ChatId, SemaphoreSlim>()

    let withChatLock (chat: ChatId) (body: unit -> Task<'a>) : Task<'a> =
        task {
            let gate = chatLocks.GetOrAdd(chat, (fun _ -> new SemaphoreSlim(1, 1)))
            do! gate.WaitAsync()

            try
                return! body ()
            finally
                gate.Release() |> ignore
        }

    /// Sends a brand-new approval message (the turn's OWN first send, or a further request from a
    /// turn that has no earlier approval message to edit) via `TgBot.SendKeyboardPlan` —
    /// owner-scoped, single-use, and optionally expiring, exactly like any other host-initiated
    /// keyboard this library sends.
    let sendNewApproval (chat: ChatId) (owner: OwnerScope) (detected: ApprovalDetection.DetectedApproval) : Task =
        task {
            match TurnProcessing.buildKeyboard formatter chat detected with
            | Error e -> observer.OnInvalidOutput(chat, e)
            | Ok(render, plan) ->
                let! messageId = bot.SendKeyboardPlan(chat, render.Body, plan, owner = owner, singleUse = true, ?expiresIn = ValueOption.toOption expiry)

                pendingApprovals.Add
                    { Chat = chat
                      Request = detected.Request
                      Owner = owner
                      MessageId = messageId }
        }
        :> Task

    /// Processes one INITIAL turn's response (a `StartRun` call, or an incoming text message):
    /// every detected approval becomes its own fresh message (a turn that raises several pending
    /// requests presents each as its own decision); with none pending, the agent's own text reply
    /// is sent as a plain message; with neither, the turn is empty and surfaced rather than
    /// sending nothing silently.
    ///
    /// Deliberate, disclosed choice: when `response` carries BOTH a preamble (`response.Text`,
    /// e.g. "Let me check that for you...") AND at least one detected approval, ONLY the approval
    /// message(s) are sent — `response.Text` is dropped, never sent as a SEPARATE plain message
    /// first. Kept this way rather than sending both: a preamble-then-approval pair would put the
    /// approval prompt itself in a SECOND message, one edit-in-place target behind the text one
    /// (`sendNewApproval`'s own message, not the preamble's), for a benefit (a little extra
    /// narration) that does not obviously outweigh the extra message noise. Revisit if a host
    /// actually needs the preamble surfaced — the fix is local (prepend it to the render's own
    /// `Body` via a formatter, or send it before `sendNewApproval` for the FIRST detected approval
    /// only), not an engine change.
    let processInitialResponse (chat: ChatId) (owner: OwnerScope) (response: AgentResponse) : Task =
        task {
            match ApprovalDetection.detect chat response with
            | [] ->
                let text = response.Text

                if not (String.IsNullOrWhiteSpace text) then
                    match MessageText.create text with
                    | Ok _ ->
                        let! _ = bot.SendText(chat, text)
                        ()
                    | Error(TextTooLong(length, max)) -> observer.OnInvalidOutput(chat, ReplyTooLong(length, max))
                    | Error e -> observer.OnInvalidOutput(chat, BodyInvalid $"%A{e}")
                else
                    observer.OnEmptyTurn chat
            | detected -> for d in detected do
                              do! sendNewApproval chat owner d
        }
        :> Task

    /// Replaces the SAME approval message's body + keyboard with the turn's NEXT pending request
    /// (a chained approval — the agent immediately raised another one), via the bot-level
    /// `EditKeyboardPlan` — the mechanism that can replace text and keyboard together in one edit
    /// (`PressContext.EditKeyboardAsync` only
    /// replaces the keyboard, per `UpdateProcessor.makeEditKeyboardAction`'s
    /// `EditMessageReplyMarkup` call — not what a fresh prompt body needs).
    ///
    /// Known limitation, disclosed: `TgBot.EditKeyboardPlan` (unlike `SendKeyboardPlan`) accepts no
    /// owner/single-use/expiry parameters — its replacement bindings default to `Anyone`/reusable,
    /// the SAME limitation every OTHER tool's self-re-rendered keyboard already has in this engine
    /// (`TgBot.fs`'s own doc comment: "a tool's own re-rendered keyboard carries none of these
    /// send-time options"). Only the FIRST approval in a turn (`sendNewApproval`, a fresh send) is
    /// fully owner-scoped and single-use; a chained further request inherits this pre-existing gap.
    ///
    /// `EditKeyboardPlan` never throws for a vanished message — it classifies the outcome instead
    /// (`TgBot.EditKeyboardPlan`'s own doc comment). `EditApplied`/`EditNotModified` both proceed
    /// normally (a pending entry against `messageId`, exactly as before); `EditNotFound` (the user
    /// deleted the message between the earlier decision and this chained request) is surfaced and
    /// FALLS BACK to `sendNewApproval` for a fresh, owner-scoped message — never records a pending
    /// entry against a message with no buttons anywhere, which would hang the turn invisibly (no
    /// way for anyone to ever decide it).
    let sendChainedApproval (chat: ChatId) (owner: OwnerScope) (messageId: MessageId) (detected: ApprovalDetection.DetectedApproval) : Task =
        task {
            match TurnProcessing.buildKeyboard formatter chat detected with
            | Error e -> observer.OnInvalidOutput(chat, e)
            | Ok(render, plan) ->
                match MessageText.create render.Body with
                | Error _ -> observer.OnInvalidOutput(chat, BodyInvalid "the validated render's body failed to re-parse as MessageText")
                | Ok bodyText ->
                    let! outcome = bot.EditKeyboardPlan(chat, messageId, bodyText, plan)

                    match outcome with
                    | EditApplied
                    | EditNotModified ->
                        pendingApprovals.Add
                            { Chat = chat
                              Request = detected.Request
                              Owner = owner
                              MessageId = messageId }
                    | EditNotFound ->
                        observer.OnInvalidOutput(
                            chat,
                            DeliveryFailed "the approval message to chain the next request onto no longer exists — sending a fresh one"
                        )

                        do! sendNewApproval chat owner detected
        }
        :> Task

    /// Edits the approval message to its final outcome text — no further approval this turn.
    let editOutcome (chat: ChatId) (ctx: PressContext) (approved: bool) (tool: string) (response: AgentResponse) : Task =
        task {
            let text = TurnProcessing.outcomeText approved tool response.Text

            match MessageText.create text with
            | Ok _ -> do! ctx.EditTextAsync text
            | Error(TextTooLong(length, max)) ->
                observer.OnInvalidOutput(chat, ReplyTooLong(length, max))
                do! ctx.EditTextAsync(TurnProcessing.outcomeText approved tool "")
            | Error e ->
                observer.OnInvalidOutput(chat, BodyInvalid $"%A{e}")
                do! ctx.EditTextAsync(TurnProcessing.outcomeText approved tool "")
        }
        :> Task

    /// Reports every entry `PendingApprovals.AbandonAllFor` drained for `chat` — a failed run's own
    /// leftover sibling decisions — via `IMafObserver.OnStaleDecision`, one call per entry, so none
    /// is silently dropped: whatever remains on THEIR messages' buttons will now only ever hit the
    /// stale path if tapped, and the host/observer should know that ahead of time rather than
    /// discover it only when (or if) a user taps one.
    let reportAbandoned (chat: ChatId) : unit =
        for abandoned in pendingApprovals.AbandonAllFor chat do
            observer.OnStaleDecision(ApprovalDescriptor.make chat abandoned.Request.RequestId (ApprovalDetection.toolName abandoned.Request))

    /// The `maf-approve`/`maf-reject` tool handler: parses the tapped button's descriptor, checks
    /// the presser against the pending entry's OWN owner scope (needed here, in the leaf, because
    /// a CHAINED approval's replacement binding is `Anyone`/reusable — `TgBot.EditKeyboardPlan`
    /// accepts no owner parameter, per `sendChainedApproval`'s own doc comment — so the engine's
    /// binding-level owner check alone would let a non-owner resume a chained decision; this check
    /// closes that gap without needing `EditKeyboardPlan` itself to grow owner-scoping), consumes
    /// the matching pending entry (at-most-once — a miss is stale, surfaced and refused), resumes
    /// the agent with the decision, and edits the approval message to the outcome or the next
    /// chained request(s). A resume failure is surfaced and leaves no live buttons behind
    /// (`OnResumeFailed` + a failure-note edit); the rest of this chat's pending entries are
    /// abandoned (and reported — see `reportAbandoned`) and its conversation is dropped, since the
    /// agent's own continuation is now presumably dead. A failure delivering the OUTCOME itself
    /// (the resume succeeded, but editing the message to show it — or to chain the next request —
    /// failed) is a DIFFERENT, milder condition: surfaced via `OnInvalidOutput`, never treated as a
    /// resume failure, and never abandons this chat's other pending entries or its conversation —
    /// the agent's own turn is fine; only getting ITS result onto the wire failed.
    member private _.HandleDecision(approved: bool) (ctx: PressContext) : Task<unit> =
        task {
            match ApprovalDescriptor.tryParse ctx.Arg with
            | None -> observer.OnMalformedDecision(ctx.Arg |> Option.ofObj |> Option.defaultValue "<none>")
            | Some descriptor ->
                let chat = ApprovalDescriptor.chat descriptor

                do!
                    withChatLock chat (fun () ->
                        task {
                            match pendingApprovals.TryGet(chat, descriptor.RequestId) with
                            | ValueNone -> observer.OnStaleDecision descriptor
                            | ValueSome peeked when not (OwnerScope.isAllowed peeked.Owner (Some ctx.User.Id)) ->
                                ctx.Answer(OwnerScope.DefaultDeniedNotice)
                            | ValueSome _ ->
                                match pendingApprovals.TryConsume(chat, descriptor.RequestId) with
                                | ValueNone -> observer.OnStaleDecision descriptor
                                | ValueSome pending ->
                                    let! conversation = conversations.GetOrCreate(chat, (fun () -> agent.CreateSessionAsync cts.Token))

                                    let! resumed =
                                        task {
                                            try
                                                let responseContent = pending.Request.CreateResponse approved
                                                let contents = ResizeArray<AIContent> [ responseContent :> AIContent ]
                                                let resumeMessage = ChatMessage(ChatRole.User, contents)
                                                let! response = agent.RunAsync(resumeMessage, conversation.Session, cancellationToken = cts.Token)
                                                return Ok response
                                            with ex ->
                                                return Error ex
                                        }

                                    match resumed with
                                    | Error ex ->
                                        observer.OnResumeFailed(descriptor, ex)

                                        try
                                            do! ctx.EditTextAsync(TurnProcessing.failureNote descriptor.Tool)
                                        with editEx ->
                                            observer.OnInvalidOutput(
                                                chat,
                                                DeliveryFailed $"failed to edit the approval message to its failure note: %s{editEx.Message}"
                                            )

                                        reportAbandoned chat
                                        conversations.Drop chat
                                    | Ok response ->
                                        // The resume itself already succeeded by this point — a
                                        // failure delivering ITS outcome (the edit-in-place, or a
                                        // chained/further approval's own send) is a Telegram-side
                                        // problem, not an agent-side one; caught HERE, separately
                                        // from the resume's own try/with above, so it is reported
                                        // as `OnInvalidOutput`/`DeliveryFailed` — never mistaken
                                        // for `OnResumeFailed`, and never abandons this chat's
                                        // other pending entries or drops its conversation.
                                        try
                                            match ApprovalDetection.detect chat response with
                                            | [] -> do! editOutcome chat ctx approved descriptor.Tool response
                                            | first :: rest ->
                                                do! sendChainedApproval chat pending.Owner pending.MessageId first

                                                for further in rest do
                                                    do! sendNewApproval chat pending.Owner further
                                        with deliveryEx ->
                                            observer.OnInvalidOutput(
                                                chat,
                                                DeliveryFailed $"failed to deliver the resumed turn's outcome: %s{deliveryEx.Message}"
                                            )
                        })
        }

    /// The bot this bridge built — usable exactly like a hand-built `TgBot` for anything else the
    /// host wants to send.
    member _.Bot: TgBot = bot

    /// Start an agent turn in `chat` on the host's own initiative. Serialized on the chat's own
    /// lock (see `chatLocks`'s doc comment) with this bridge's other turns for the same chat.
    /// `owner` overrides the owner scope for approvals THIS run raises; omitted, `RunOwner.resolve`
    /// applies the bridge's configured default, then the private-chat-peer/`Anyone` fallback.
    ///
    /// The WHOLE turn — session creation, `agent.RunAsync`, and processing its response — is one
    /// unit for failure-reporting purposes: an unexpected throw anywhere in it (most commonly a
    /// network/backend error out of `CreateSessionAsync`/`RunAsync`) is caught and reported via
    /// `IMafObserver.OnTurnFailed` rather than propagating to the caller — no reply/approval was
    /// sent for this turn, but the chat's own lock is still released normally (the `finally` in
    /// `withChatLock`), so the NEXT turn on this chat is unaffected.
    member _.StartRun(chat: ChatId, prompt: string, ?owner: OwnerScope) : Task =
        let explicitOwner = owner |> Option.orElse (ValueOption.toOption defaultOwner)

        withChatLock chat (fun () ->
            task {
                try
                    let! conversation = conversations.GetOrCreate(chat, (fun () -> agent.CreateSessionAsync cts.Token))
                    let resolvedOwner = RunOwner.resolve explicitOwner None chat
                    let! response = agent.RunAsync(prompt, conversation.Session, cancellationToken = cts.Token)
                    do! processInitialResponse chat resolvedOwner response
                with ex ->
                    observer.OnTurnFailed(chat, ex)
            })
        :> Task

    /// The bridge's text-turn handler — wired as the Core seam's `MessageHandler` by
    /// `Maf.startPolling`/`startWebhook`'s shared build path (`config.WithOnMessage`, config-time,
    /// before the bot starts consuming updates). An incoming user text message is a
    /// MESSAGE-INITIATED turn: `RunOwner.resolve` defaults any approval it raises to
    /// `User message.Sender.Id`, the message's own sender — never the bridge's configured
    /// `defaultOwner` (that default is for HOST-initiated runs only, `StartRun`'s own fallback).
    /// Serialized on the SAME per-chat lock as `StartRun`/`HandleDecision` — this bridge's
    /// `AgentSession` is a single-writer resource, so a text turn, a host-initiated run, and a
    /// decision resume for one chat can never touch it concurrently, regardless of which one a
    /// host or user triggers first.
    ///
    /// Same failure discipline as `StartRun` (see its own doc comment): the whole turn is caught
    /// and reported via `IMafObserver.OnTurnFailed`, never left to propagate — an uncaught throw
    /// here would otherwise only ever reach Core's OWN `IMessageObserver.OnMessageFailed`
    /// (`UpdateProcessor.buildMessageWork`'s existing try/with around this very handler), a
    /// DIFFERENT observability channel a host using a CUSTOM `IMafObserver` might never be
    /// listening on at all.
    member internal _.HandleIncomingMessage(message: IncomingMessage) : Task =
        withChatLock message.Chat (fun () ->
            task {
                try
                    let! conversation = conversations.GetOrCreate(message.Chat, (fun () -> agent.CreateSessionAsync cts.Token))
                    let resolvedOwner = RunOwner.resolve None (Some message.Sender.Id) message.Chat
                    let! response = agent.RunAsync(message.Text, conversation.Session, cancellationToken = cts.Token)
                    do! processInitialResponse message.Chat resolvedOwner response
                with ex ->
                    observer.OnTurnFailed(message.Chat, ex)
            })
        :> Task

    /// Registers the decision tools into `bot`'s Tool Router — called once, by `Maf.startPolling`/
    /// `startWebhook`'s shared build path, right after construction and before either returns.
    member internal this.RegisterTools(tools: ToolRegistry) : unit =
        tools.Register(
            ReservedToolNames.Approve,
            (fun ctx -> this.HandleDecision true ctx),
            description = "Approve the agent's pending tool call."
        )
        |> ignore

        tools.Register(
            ReservedToolNames.Reject,
            (fun ctx -> this.HandleDecision false ctx),
            description = "Reject the agent's pending tool call."
        )
        |> ignore

    interface IAsyncDisposable with
        /// Cancels (but does NOT dispose) `cts` — a queued `withChatLock` turn that has not yet
        /// reached the point of reading `cts.Token` (e.g. a `StartRun`/`HandleIncomingMessage`
        /// call still waiting on another in-flight turn's SAME chat gate) must still be able to
        /// read a valid (if already-cancelled) token when it finally runs, rather than hit
        /// `ObjectDisposedException` off this method's own observer-less path. A cancelled,
        /// undisposed `CancellationTokenSource` is a small, well-understood, accepted leak — the
        /// alternative (disposing it here) risks handing a token read to a task this disposal
        /// itself cannot see or wait for.
        ///
        /// Only IDLE per-chat semaphores (`CurrentCount = 1` — nobody currently holds them) are
        /// disposed: a gate an in-flight `withChatLock` body is STILL RUNNING under has
        /// `CurrentCount = 0` and is deliberately left alone here, because `withChatLock`'s own
        /// `finally gate.Release()` (Bridge.fs, right above) would otherwise throw
        /// `ObjectDisposedException` the instant that in-flight turn finishes — turning a graceful
        /// disposal into a crash for a turn this disposal has no way to wait for. This is a
        /// best-effort cleanup, not a guarantee every semaphore is ever disposed (a chat whose
        /// gate is BUSY at the moment of disposal simply keeps its semaphore, the same accepted
        /// class of leak as the `cts` above); it still reclaims the common case (every chat with
        /// no turn in flight right now).
        member _.DisposeAsync() : ValueTask =
            task {
                cts.Cancel()
                do! (bot :> IAsyncDisposable).DisposeAsync()

                for gate in chatLocks.Values do
                    if gate.CurrentCount = 1 then
                        gate.Dispose()
            }
            |> ValueTask

/// Builds a `MafBridge` over an already-configured bot — the shared path `Maf.startPolling`/
/// `startWebhook` both funnel through after building `bot` itself.
module private BridgeBuild =

    let private resolveObserver (options: MafBridgeOptions) (bot: TgBot) : IMafObserver =
        match options.Observer with
        | ValueSome o -> o
        | ValueNone ->
            match bot.Logger with
            | Some logger -> LoggingMafObserver logger :> IMafObserver
            | None -> NoopMafObserver() :> IMafObserver

    let private resolveFormatter (options: MafBridgeOptions) : ApprovalFormatter =
        match options.Formatter with
        | ValueSome f -> f
        | ValueNone -> ApprovalRendering.defaultRender

    /// Requires `bot` to already have a Tool Router wired in (`TgBotConfig.WithTools`/
    /// `TgWebhookConfig.WithTools`) — same precondition `A2ui.renderer` enforces, and for the same
    /// reason: without one, a rendered approval's own buttons would reach the wire, get tapped, and
    /// silently no-op forever. Also requires `maf-approve`/`maf-reject` not already registered —
    /// the double-attach guard: a second attach would silently replace the first bridge's tools
    /// (`IToolRegistry.Register` is add-or-replace by design), orphaning every approval it already
    /// sent, mirroring `A2ui.renderer`'s own guard for the identical class of mistake.
    let build (bot: TgBot) (agent: AIAgent) (options: MafBridgeOptions) : MafBridge =
        match bot.Tools with
        | None ->
            invalidOp
                "Maf.startPolling/startWebhook requires a Tool Router wired into the bot config \
                 (call .WithTools first) so its internal maf-approve/maf-reject tools can route \
                 decision taps — without one, every tap would silently no-op forever, since no \
                 ToolDispatch could ever resolve its binding."
        | Some tools ->
            match ToolName.create ReservedToolNames.Approve, ToolName.create ReservedToolNames.Reject with
            | Error e, _
            | _, Error e -> invalidOp $"unreachable: a literal MAF decision tool name failed validation (%A{e})"
            | Ok approveName, Ok rejectName ->
                match tools.Registry.TryResolve approveName, tools.Registry.TryResolve rejectName with
                | ValueNone, ValueNone ->
                    let observer = resolveObserver options bot
                    let formatter = resolveFormatter options
                    let bridge = new MafBridge(bot, agent, observer, formatter, options.DefaultOwner, options.ApprovalExpiry)
                    bridge.RegisterTools tools
                    bridge
                | _ ->
                    invalidOp
                        "Maf.startPolling/startWebhook was already called for this bot — a second call would \
                         re-register maf-approve/maf-reject (IToolRegistry.Register is add-or-replace) and \
                         silently orphan the first bridge's own pending approvals. Reuse the FIRST MafBridge \
                         this bot already has instead of building a second one."

/// Wraps bot startup with a MAF agent, registering the decision tools before either function
/// returns, so no approval keyboard can ever precede its own tools.
module Maf =

    /// Builds `bot` from `config` (requires `.WithTools`), registers `maf-approve`/`maf-reject`
    /// into its Tool Router, and returns the live bridge.
    ///
    /// The Core seam's `OnMessage` is config-time (`CommonConfig.OnMessage`'s own doc comment: a
    /// bot starts consuming updates the instant it starts, so a late-bound handler would race the
    /// first message) — but the handler needs to call INTO the `MafBridge` this very function is
    /// still building, which doesn't exist until AFTER the bot does (`MafBridge`'s constructor
    /// takes `bot: TgBot`). Resolved with a mutable local the closure below captures by reference:
    /// the handler is wired into `config` (and therefore live from the bot's very first ingested
    /// update) before `TgBot.startPolling` is ever called, while the cell it reads is only
    /// populated once the bridge itself exists, a few synchronous calls later — the same "narrow,
    /// practically-instant, not literally atomic" window `BridgeBuild.build`'s own tool
    /// registration already accepts for `maf-approve`/`maf-reject` (both land well before the pump
    /// loop could plausibly have a real update to hand off). A message that somehow arrived in
    /// that window is a no-op (`Task.CompletedTask`), never a crash.
    /// `bot` is already RUNNING (ingesting updates in the background) by the time `BridgeBuild.build`
    /// runs — the config-time `OnMessage` wiring above needs the bot to exist before the bridge can,
    /// and `TgBot.startPolling`/`startWebhook` starts ingestion as part of that same call, so there
    /// is no earlier point to build the bridge at. If `build` then throws (no `.WithTools`, or a
    /// double-attach onto a bot that already has a bridge), `bot` would otherwise be leaked —
    /// long-polling (or listening for a webhook) forever with nothing left to stop it. Caught here
    /// and disposed before the exception is re-raised, so a failed build never outlives its own call.
    let private buildOrDispose (bot: TgBot) (agent: AIAgent) (options: MafBridgeOptions) : Task<MafBridge> =
        task {
            try
                return BridgeBuild.build bot agent options
            with ex ->
                do! (bot :> IAsyncDisposable).DisposeAsync()
                return raise ex
        }

    let startPollingWith (options: MafBridgeOptions) (config: TgBotConfig) (agent: AIAgent) : Task<MafBridge> =
        task {
            let mutable bridgeCell: MafBridge option = None

            let handler: MessageHandler =
                fun message _ct ->
                    match bridgeCell with
                    | Some bridge -> bridge.HandleIncomingMessage message
                    | None -> Task.CompletedTask

            let! bot = TgBot.startPolling (config.WithOnMessage handler)
            let! bridge = buildOrDispose bot agent options
            bridgeCell <- Some bridge
            return bridge
        }

    /// Zero-config variant of `startPollingWith`.
    let startPolling (config: TgBotConfig) (agent: AIAgent) : Task<MafBridge> =
        startPollingWith MafBridgeOptions.defaults config agent

    /// The webhook-transport counterpart to `startPollingWith` — same config-time `OnMessage`
    /// wiring, same reasoning (see that function's own doc comment).
    let startWebhookWith (options: MafBridgeOptions) (config: TgWebhookConfig) (agent: AIAgent) : Task<MafBridge> =
        task {
            let mutable bridgeCell: MafBridge option = None

            let handler: MessageHandler =
                fun message _ct ->
                    match bridgeCell with
                    | Some bridge -> bridge.HandleIncomingMessage message
                    | None -> Task.CompletedTask

            let! bot = TgBot.startWebhook (config.WithOnMessage handler)
            let! bridge = buildOrDispose bot agent options
            bridgeCell <- Some bridge
            return bridge
        }

    /// Zero-config variant of `startWebhookWith`.
    let startWebhook (config: TgWebhookConfig) (agent: AIAgent) : Task<MafBridge> = startWebhookWith MafBridgeOptions.defaults config agent
