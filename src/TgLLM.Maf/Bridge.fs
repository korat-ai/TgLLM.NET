namespace TgLLM.Maf

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Runtime.ExceptionServices
open FSharp.UMX
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

    interface IMafSessionObserver with
        member _.OnSessionRestoreFailed(chat: ChatId, failure: SessionFailure) =
            logger.LogWarning(
                "MAF: restoring the durable session for chat {Chat} failed: {Failure}",
                [| chat :> obj; $"%A{failure}" :> obj |]
            )

        member _.OnSessionPersistFailed(chat: ChatId, error: exn) =
            logger.LogError(error, "MAF: persisting the durable session for chat {Chat} failed", [| chat :> obj |])

    interface IMafStreamingObserver with
        member _.OnStreamFailed(chat: ChatId, liveMessage: MessageId, error: exn) =
            logger.LogError(
                error,
                "MAF: the reply stream in chat {Chat} failed after message {MessageId} was already shown",
                [| chat :> obj; liveMessage :> obj |]
            )

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

/// The state machine's own outcome for one streaming turn — what the finalize step needs to decide
/// the LAST message's own send/edit, mirroring `TurnProcessing`/`processInitialResponse`'s own
/// "detected approvals, or plain text" split. `LastSlice` is the LAST message's own kept text — NOT
/// `FinalResponse.Text`, which is the WHOLE turn's accumulated reply and may span several already
/// finalized earlier messages once a turn has spilled across more than one message.
[<NoComparison; NoEquality>]
type private StreamOutcome =
    { FinalResponse: AgentResponse
      Detected: ApprovalDetection.DetectedApproval list
      /// `None` only if nothing was ever sent for this turn.
      LastMessage: MessageId option
      /// The LAST message's own text, live-tracked by its own `ReplyCoalescer` — empty when nothing
      /// was ever sent live (the "no message was ever sent" finalize branch re-derives its own text
      /// straight from `FinalResponse.Text` instead).
      LastSlice: string
      /// Only meaningful when `LastMessage = Some _`: whether the LAST message's own coalescer has
      /// any text pending that its own last successful send/edit does NOT already reflect. `false`
      /// when the message's own last emit already shows `LastSlice` verbatim (e.g. a single
      /// content-bearing update sent once, with nothing further arriving) — the finalize step skips
      /// its own mandatory final-flush edit in that case, rather than re-sending byte-identical text
      /// Telegram would classify as a no-op edit anyway. `true` whenever a later delta arrived after
      /// the coalescer's own last mark (whether or not the ordinary cadence gate had cleared by
      /// then) — computed via `ReplyCoalescer.IsDue` at a synthetic far-future instant, which forces
      /// its own timing-gate check to always pass and leaves ONLY the content-changed comparison as
      /// the deciding factor.
      NeedsFinalFlush: bool
      /// Only meaningful when `LastMessage = None`: the text `finalizeStreamingTurn`'s own `[],
      /// None` branch splits and (re)sends as fresh messages. `FinalResponse.Text` (the WHOLE turn)
      /// whenever nothing was EVER sent, or the turn's only message was sent then vanished with no
      /// spill ever happening — re-sending the complete reply as a new message IS correct recovery
      /// in both cases, nothing else exists to duplicate. But once a turn HAS spilled (at least one
      /// earlier message already reached the wire and was finalized by a rollover) and the LAST
      /// live message then vanishes (`EditNotFound`) with no further update ever arriving to trigger
      /// a fresh resend, `FinalResponse.Text` would re-send the WHOLE turn — duplicating every
      /// already-finalized earlier message. In that one case this instead carries only the LAST
      /// message's own missing slice (`coalescer.RunningText`, unchanged since the vanish, since a
      /// further delta arriving after a vanish always triggers an immediate fresh send of its own —
      /// reaching finalize with `LastMessage = None` at all means none ever did).
      RecoveryText: string }

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

    /// Three-tier resolution, mirroring `BridgeBuild.resolveObserver`'s own primary-observer
    /// fallback chain: `observer` itself when it also implements `IMafSessionObserver` (true of
    /// `LoggingMafObserver`/`NoopMafObserver`, which implement both); else the bot's own logger,
    /// wrapped the same way the zero-config path wraps it for `IMafObserver`, so a host-supplied
    /// CUSTOM `IMafObserver` that does NOT also implement the durable-session channel still gets
    /// its restore/persist failures logged whenever a logger is wired, rather than every such host
    /// silently losing this channel; else a noop, same as before.
    let sessionObserver: IMafSessionObserver =
        match box observer with
        | :? IMafSessionObserver as so -> so
        | _ ->
            match bot.Logger with
            | Some logger -> LoggingMafObserver logger :> IMafSessionObserver
            | None -> NoopMafObserver() :> IMafSessionObserver

    /// Three-tier resolution, mirroring `sessionObserver`'s own fallback chain immediately above:
    /// `observer` itself when it also implements `IMafStreamingObserver`; else the bot's own logger,
    /// wrapped; else noop — so a host-supplied custom `IMafObserver` that does not also implement the
    /// streaming-loop channel still gets `OnStreamFailed` logged whenever a logger is wired, rather
    /// than every such host silently losing this channel.
    let streamingObserver: IMafStreamingObserver =
        match box observer with
        | :? IMafStreamingObserver as so -> so
        | _ ->
            match bot.Logger with
            | Some logger -> LoggingMafObserver logger :> IMafStreamingObserver
            | None -> NoopMafObserver() :> IMafStreamingObserver

    /// Rebuilds ONE pending approval from its persisted descriptor, with NO live in-memory state —
    /// the read counterpart to `toPersistedDto`. `ArgumentsJson` round-trips through `JsonElement`
    /// values (the same boxed-element shape a real `DeserializeSessionAsync` leaves
    /// `FunctionCallContent.Arguments` holding), never re-typed to the original CLR values — nothing
    /// this leaf does with a rehydrated request's arguments needs them typed any further than that.
    let rehydrate (chat: ChatId) (dto: PersistedApprovalDto) : PendingApproval =
        let argsDict =
            match dto.ArgumentsJson with
            | null -> Dictionary<string, obj | null>() :> IDictionary<string, obj | null>
            | json ->
                let d = Dictionary<string, obj | null>()

                match JsonSerializer.Deserialize<Dictionary<string, JsonElement>> json |> Option.ofObj with
                | Some parsed ->
                    for kv in parsed do
                        d[kv.Key] <- box kv.Value
                | None -> ()

                d :> IDictionary<string, obj | null>

        let call = FunctionCallContent(dto.CallId, dto.Tool, argsDict)
        let request = ToolApprovalRequestContent(dto.RequestId, call)
        let owner = if dto.OwnerUserId.HasValue then User(UMX.tag<userId> dto.OwnerUserId.Value) else Anyone

        { Chat = chat
          Request = request
          Owner = owner
          MessageId = UMX.tag<messageId> dto.MessageId
          ExpiresAt = dto.ExpiresAt |> Option.ofNullable }

    /// The session factory `conversations.GetOrCreate` runs (at most once per chat, per
    /// `Conversations`' own `Lazy` caching). With no store configured this is today's behavior,
    /// byte-identical — a fresh `CreateSessionAsync`, nothing more. With a store configured, the
    /// durable record is tried FIRST: a decode/validate failure, or a throw deserializing the session
    /// itself, both report via `IMafSessionObserver.OnSessionRestoreFailed` and fall back to a fresh
    /// session (the rejected/corrupt record is also removed from the store, so it is not retried
    /// forever on every later turn); a genuinely absent record is silently a fresh session, not a
    /// failure. A throw READING the store itself (`store.TryGet`) is DIFFERENT: it reports the SAME
    /// `OnSessionRestoreFailed(StoreUnavailable _)`, but then RE-RAISES rather than falling back to a
    /// fresh session — a `Lazy<Task<AgentSession>>`-cached fresh session would otherwise sit in
    /// `Conversations` for the rest of the process's life, and the NEXT end-of-turn persist would
    /// overwrite (destroy) the chat's still-intact durable record over what may have been a purely
    /// transient read blip. Re-raising instead faults the `Lazy` — `Conversations.GetOrCreate` evicts
    /// it on a fault (its own doc comment) — so the very NEXT call retries the read against the
    /// record, which is still there. `Approvals` is rehydrated into `pendingApprovals` ONLY once the
    /// session itself deserialized successfully — a session and its approvals are never
    /// half-restored: either both come back, or neither does.
    let restoreOrCreate (chat: ChatId) : unit -> ValueTask<AgentSession> =
        fun () ->
            match bot.SessionStore with
            | None -> agent.CreateSessionAsync cts.Token
            | Some store ->
                ValueTask<AgentSession>(
                    task {
                        let! attempt =
                            task {
                                try
                                    let! record = store.TryGet(chat, cts.Token)
                                    return Ok record
                                with ex ->
                                    return Error ex
                            }

                        match attempt with
                        | Error ex ->
                            sessionObserver.OnSessionRestoreFailed(chat, StoreUnavailable ex.Message)

                            // Re-raise rather than falling back to a fresh session — see this
                            // function's own doc comment. A fresh session here would be cached by
                            // `Conversations`' `Lazy<Task<AgentSession>>` for the rest of the
                            // process's life, and the NEXT end-of-turn persist would overwrite the
                            // chat's still-intact durable record over what may be a purely transient
                            // read blip. Re-raising instead faults the `Lazy`, which
                            // `Conversations.GetOrCreate` evicts on a fault (its own doc comment), so
                            // the NEXT call retries the read against the record, which is still there.
                            // `ExceptionDispatchInfo` rethrows preserving the original throw site's
                            // stack, so a host debugging a custom store sees where the read actually
                            // failed rather than this line.
                            ExceptionDispatchInfo.Capture(ex).Throw()
                            return failwith "unreachable: ExceptionDispatchInfo.Throw always rethrows"
                        | Ok ValueNone -> return! agent.CreateSessionAsync cts.Token
                        | Ok(ValueSome record) ->
                            match SessionEnvelope.decodeAndValidate SessionEnvelope.currentMafVersion record.Payload with
                            | Error failure ->
                                sessionObserver.OnSessionRestoreFailed(chat, failure)

                                // Guarded: a read-only backing store (`FileSessionStore.Remove`
                                // rewrites the whole file) can throw here — left unguarded, that
                                // throw would fault this factory the SAME way a genuine backend
                                // failure does, misclassifying the whole turn as `OnTurnFailed` with
                                // no reply ever sent, and (since the invalid record is never actually
                                // removed) every LATER turn on this chat re-faulting the identical
                                // way. The fresh session below is still created regardless — an
                                // un-removable invalid record is a problem for the NEXT restore
                                // attempt to rediscover, not a reason to fail THIS turn too.
                                try
                                    do! store.Remove(chat, cts.Token)
                                with removeEx ->
                                    sessionObserver.OnSessionPersistFailed(chat, removeEx)

                                return! agent.CreateSessionAsync cts.Token
                            | Ok env ->
                                // The approval rehydration runs INSIDE this SAME try, alongside
                                // `DeserializeSessionAsync` — `SessionEnvelope.validate` (above)
                                // already refuses a null `Approvals` array or an approval missing a
                                // usable `RequestId`/`CallId`/`Tool`, but it cannot cheaply detect
                                // every shape that still throws once `rehydrate` actually runs (a
                                // syntactically valid but non-object `ArgumentsJson`, e.g. `"[1,2,3]"`,
                                // fails `JsonSerializer.Deserialize<Dictionary<string,JsonElement>>`).
                                // `Array.map` is eager: if ANY entry throws, `rehydrated` is never
                                // bound and NOTHING below it runs, so a residual throw here is caught
                                // by the SAME `with ex ->` as a corrupt `SessionJson` — reported via
                                // `OnSessionRestoreFailed(CorruptRecord)`, the record removed, a fresh
                                // session used — rather than throwing PAST both of this function's own
                                // remove-on-failure gates and bricking the chat for good. Either both
                                // the session AND every approval come back, or neither does.
                                let! restored =
                                    task {
                                        try
                                            let element =
                                                use doc = JsonDocument.Parse env.SessionJson
                                                doc.RootElement.Clone()

                                            let! session = agent.DeserializeSessionAsync(element, cancellationToken = cts.Token)
                                            let rehydrated = env.Approvals |> Array.map (rehydrate chat) |> Array.toList

                                            for approval in rehydrated do
                                                pendingApprovals.Add approval

                                            return Ok session
                                        with ex ->
                                            return Error ex
                                    }

                                match restored with
                                | Ok session -> return session
                                | Error ex ->
                                    sessionObserver.OnSessionRestoreFailed(chat, CorruptRecord ex.Message)

                                    // Guarded the same way, and for the same reason, as the
                                    // validate-failure branch's own `store.Remove` above.
                                    try
                                        do! store.Remove(chat, cts.Token)
                                    with removeEx ->
                                        sessionObserver.OnSessionPersistFailed(chat, removeEx)

                                    return! agent.CreateSessionAsync cts.Token
                    }
                )

    /// The persisted counterpart to a live `PendingApproval` — the write side of `rehydrate`.
    /// `None` when `p.Request.ToolCall` is not a `FunctionCallContent` — `ToolApprovalRequestContent
    /// .ToolCall` is statically `ToolCallContent`, and the resolved 10.6.0 binaries ship several OTHER
    /// concrete subtypes (`McpServerToolCallContent`, `CodeInterpreterToolCallContent`, etc.) that
    /// `ApprovalDetection` already renders a usable prompt for via its own fallback branch — so a
    /// pending approval over one of those is perfectly live in-memory, but this leaf only knows how
    /// to PERSIST the `FunctionCallContent` shape (`CallId`/`Name`/`Arguments`) `rehydrate` expects
    /// back. Skipping it here (rather than hard-casting and throwing) is what lets `persistConversation`
    /// write the session and every OTHER, function-shaped sibling even with one of these pending —
    /// the skipped entry simply degrades to stale on the next restart, which is a far better outcome
    /// than losing the WHOLE record (session included) to one un-persistable approval.
    let toPersistedDto (p: PendingApproval) : PersistedApprovalDto option =
        match p.Request.ToolCall with
        | :? FunctionCallContent as call ->
            let argsJson: string | null =
                match call.Arguments |> Option.ofObj with
                | Some a -> JsonSerializer.Serialize a
                | None -> null

            Some
                { RequestId = p.Request.RequestId
                  CallId = call.CallId
                  Tool = call.Name
                  ArgumentsJson = argsJson
                  OwnerUserId =
                    match p.Owner with
                    | Anyone -> Nullable()
                    | User uid -> Nullable(UMX.untag uid)
                  MessageId = UMX.untag p.MessageId
                  ExpiresAt = p.ExpiresAt |> Option.toNullable }
        | _ -> None

    /// Writes this chat's WHOLE durable record and reports whether it succeeded. Never throws: a
    /// write failure self-reports via `IMafSessionObserver.OnSessionPersistFailed` and returns false.
    let tryPersistConversation (chat: ChatId) (session: AgentSession) : Task<bool> =
        task {
            match bot.SessionStore with
            | None -> return true
            | Some store ->
                try
                    let! element = agent.SerializeSessionAsync(session, cancellationToken = cts.Token)
                    let now = bot.Clock()

                    let approvals =
                        pendingApprovals.SnapshotFor chat
                        |> List.filter (fun p -> Expiry.isLive now p.ExpiresAt)
                        |> List.choose toPersistedDto
                        |> List.toArray

                    let envelope: ConversationEnvelopeDto =
                        { Format = SessionEnvelope.CurrentFormat
                          MafVersion = SessionEnvelope.currentMafVersion
                          MeaiVersion = SessionEnvelope.currentMeaiVersion
                          SessionJson = element.GetRawText()
                          Approvals = approvals }

                    do! store.Save(chat, { Payload = SessionEnvelope.encode envelope; LastActivityAt = now }, cts.Token)
                    return true
                with ex ->
                    sessionObserver.OnSessionPersistFailed(chat, ex)
                    return false
        }

    let persistConversation (chat: ChatId) (session: AgentSession) : Task =
        task {
            let! _ = tryPersistConversation chat session
            ()
        }
        :> Task

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
                      MessageId = messageId
                      ExpiresAt = expiry |> ValueOption.map (fun span -> bot.Clock() + span) |> ValueOption.toOption }
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
    ///
    /// The SENDS themselves (`bot.SendText`, and each `sendNewApproval` inside the multi-approval
    /// loop) are wrapped in their own try/with, reporting a failure via `OnInvalidOutput`/
    /// `DeliveryFailed` — the same split `HandleDecision` already uses between "the agent's own
    /// step" and "getting its result onto the wire" (see that member's own doc comment). Without
    /// this, a Telegram-side failure here — e.g. the SECOND message of a THREE-approval turn,
    /// after the first already reached the wire — would escape uncaught to `StartRun`'s/
    /// `HandleIncomingMessage`'s own outer try/with and be misreported as `OnTurnFailed`, whose
    /// own doc comment promises "no message was sent for this turn" — false here, since the FIRST
    /// approval's message (and its pending entry) already exist and stay live regardless of a
    /// LATER sibling's delivery failure. `OnTurnFailed` is reserved for the turn never reaching
    /// this function at all (`agent.RunAsync`/`CreateSessionAsync` throwing); once `response` is
    /// in hand, any failure putting it on the wire is `OnInvalidOutput`.
    let processInitialResponse (chat: ChatId) (owner: OwnerScope) (response: AgentResponse) : Task =
        task {
            match ApprovalDetection.detect chat response with
            | [] ->
                let text = response.Text

                if not (String.IsNullOrWhiteSpace text) then
                    match MessageText.create text with
                    | Ok _ ->
                        try
                            let! _ = bot.SendText(chat, text)
                            ()
                        with deliveryEx ->
                            observer.OnInvalidOutput(chat, DeliveryFailed $"failed to send this turn's reply text: %s{deliveryEx.Message}")
                    | Error(TextTooLong(length, max)) -> observer.OnInvalidOutput(chat, ReplyTooLong(length, max))
                    | Error e -> observer.OnInvalidOutput(chat, BodyInvalid $"%A{e}")
                else
                    observer.OnEmptyTurn chat
            | detected ->
                try
                    for d in detected do
                        do! sendNewApproval chat owner d
                with deliveryEx ->
                    observer.OnInvalidOutput(chat, DeliveryFailed $"failed to deliver one of this turn's approval messages: %s{deliveryEx.Message}")
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
    ///
    /// `bodyOverride`, when `Some`, replaces the rendered prompt's own `Body` as the text sent to
    /// the wire — the buttons/plan still come from the SAME render (`TurnProcessing.buildKeyboard`)
    /// either way. `None` (every call below this one) is byte-identical to before this parameter
    /// existed. Used by the streaming turn's own finalize step to keep a live message's already-
    /// shown narration in place while still adding the decision buttons, rather than replacing the
    /// narration with the ordinary tool-call prompt text — a standalone-function `let` binding
    /// cannot itself carry a true optional argument (only type members can, per the F# spec), so
    /// `sendChainedApproval` below stays the untouched, no-override entry point, and this is the
    /// one body of logic both it and the finalize step's own override call route through.
    /// The largest delay `guardedEmit` will ever actually wait out, in seconds, regardless of what
    /// `Retry-After` the server sends — see `guardedEmit`'s own doc comment for why this is clamped
    /// rather than honored verbatim.
    let guardedEmitMaxDelaySeconds = 5

    /// Runs `send` with a small, bounded, blocking retry on a 429 — used for emits that are
    /// MANDATORY (the turn cannot correctly finish without them, and no FURTHER stream update is
    /// coming to retry against, unlike an ordinary mid-stream coalesced edit routed through
    /// `ReplyCoalescer.NotifyRateLimited`): the end-of-stream final flush and a split's retiring
    /// edit (`runStreamingTurn`/`rollOver`, below), and `sendChainedApprovalCore`'s own
    /// `EditKeyboardPlan` call immediately below this — the kept-narration approval render for a
    /// streamed turn, and the ordinary chained-approval render `HandleDecision`'s own resume chain
    /// already used before streaming existed. `ex.Parameters` is read null-guarded — itself
    /// nullable, not just its `RetryAfter` property (`Telegram.Bot.Exceptions.ApiRequestException
    /// .Parameters : ResponseParameters`, a reference type; `ResponseParameters.RetryAfter :
    /// Nullable<int>`).
    ///
    /// The honored delay is CLAMPED to `guardedEmitMaxDelaySeconds`, never the raw server-supplied
    /// hint: Telegram.Bot 22.10.1 already auto-retries any 429 with `retry_after <= 60s` internally
    /// (its own vendor-level `RetryThreshold`), so the ONLY 429s an `ApiRequestException` ever
    /// carries this far already have `retry_after > 60s` — an un-clamped wait would block whichever
    /// caller is awaiting this call for well over a minute. This call site is shared by
    /// `sendChainedApprovalCore`'s own resume chain, which runs under `withChatLock` — a one-minute-
    /// plus block there would stall that WHOLE chat (every other turn/decision queued behind it),
    /// not just this one emit. On exhaustion (every attempt still 429, clamped wait included), or on
    /// any OTHER exception, the failure is returned as `Error` for the caller to report via
    /// `OnInvalidOutput` — a persistent, minutes-long rate limit surfaces as a reported failure
    /// rather than hanging the chat; never left to escape uncaught. `cts.Token` is passed to the
    /// delay so a graceful shutdown does not sit out a full clamped wait either.
    let guardedEmit (maxAttempts: int) (send: unit -> Task<'a>) : Task<Result<'a, exn>> =
        task {
            let mutable attempt = 0
            let mutable result: Result<'a, exn> option = None

            while result.IsNone do
                attempt <- attempt + 1

                try
                    let! r = send ()
                    result <- Some(Ok r)
                with
                | :? Telegram.Bot.Exceptions.ApiRequestException as ex when ex.ErrorCode = 429 && attempt < maxAttempts ->
                    let retryAfterSeconds =
                        ex.Parameters
                        |> Option.ofObj
                        |> Option.bind (fun p -> Option.ofNullable p.RetryAfter)
                        |> Option.defaultValue 1

                    let cappedDelaySeconds = min retryAfterSeconds guardedEmitMaxDelaySeconds
                    do! Task.Delay(TimeSpan.FromSeconds(float cappedDelaySeconds), cts.Token)
                | ex -> result <- Some(Error ex)

            return result.Value
        }

    let sendChainedApprovalCore
        (chat: ChatId)
        (owner: OwnerScope)
        (messageId: MessageId)
        (detected: ApprovalDetection.DetectedApproval)
        (bodyOverride: string option)
        : Task =
        task {
            match TurnProcessing.buildKeyboard formatter chat detected with
            | Error e -> observer.OnInvalidOutput(chat, e)
            | Ok(render, plan) ->
                let body = bodyOverride |> Option.defaultValue render.Body

                match MessageText.create body with
                | Error _ -> observer.OnInvalidOutput(chat, BodyInvalid "the approval message's own body (rendered, or a caller-supplied override) failed to re-parse as MessageText")
                | Ok bodyText ->
                    // Mandatory, not best-effort: no further stream update is ever coming to retry
                    // this render against, exactly like the end-of-stream final flush and a split's
                    // retiring edit — routed through the SAME bounded `guardedEmit` retry rather than
                    // surfacing a transient 429 straight to `OnInvalidOutput` with no retry at all
                    // (this call site is shared by BOTH the streaming finalize step's own
                    // kept-narration render and `HandleDecision`'s ordinary resume-chain render).
                    let! attempt = guardedEmit 3 (fun () -> bot.EditKeyboardPlan(chat, messageId, bodyText, plan))

                    match attempt with
                    | Error ex -> observer.OnInvalidOutput(chat, DeliveryFailed $"failed to render the approval: %s{ex.Message}")
                    | Ok outcome ->
                        match outcome with
                        | EditApplied
                        | EditNotModified ->
                            pendingApprovals.Add
                                { Chat = chat
                                  Request = detected.Request
                                  Owner = owner
                                  MessageId = messageId
                                  ExpiresAt = expiry |> ValueOption.map (fun span -> bot.Clock() + span) |> ValueOption.toOption }
                        | EditNotFound ->
                            observer.OnInvalidOutput(
                                chat,
                                DeliveryFailed "the approval message to chain the next request onto no longer exists — sending a fresh one"
                            )

                            do! sendNewApproval chat owner detected
        }
        :> Task

    let sendChainedApproval (chat: ChatId) (owner: OwnerScope) (messageId: MessageId) (detected: ApprovalDetection.DetectedApproval) : Task =
        sendChainedApprovalCore chat owner messageId detected None

    /// Drives ONE streaming turn: the hand-rolled `IAsyncEnumerator` loop over
    /// `agent.RunStreamingAsync` (mirrors `TgLLM.Core.UpdateProcessor.RunAsync`'s own loop over its
    /// `IAsyncEnumerable<AgentEvent>` source), a per-message `ReplyCoalescer` with
    /// `MessageSplitting`-driven rollover on overflow, and the initial-send-then-edit state machine —
    /// ending in a `StreamOutcome` the finalize step below consumes. Runs under the SAME per-chat
    /// lock (`withChatLock`, see the caller) as every other turn; closes over `bot`/`observer`/
    /// `streamingObserver`. `owner` is threaded through (unused by this story's own loop, which never
    /// renders an approval itself) purely for signature symmetry with `finalizeStreamingTurn`'s own
    /// `(chat, owner, ...)` shape — mirrors `processInitialResponse`'s `(chat, owner, response)`.
    let runStreamingTurn
        (chat: ChatId)
        (owner: OwnerScope)
        (coalesceInterval: TimeSpan)
        (run: unit -> IAsyncEnumerable<AgentResponseUpdate>)
        : Task<StreamOutcome option> =
        ignore owner

        // The mutable turn state, and the two rollover helpers closing over it, live OUTSIDE the
        // driving `task { }` below — nesting a `let rec` task-returning function's OWN definition
        // textually inside another task CE's body defeats that OUTER CE's static resumable-state-
        // machine compilation (FS3511); each of these compiles to its own independent state machine
        // instead, called (not inlined) from the driving loop below, exactly like `guardedEmit`
        // above already is.
        let maxLen = MessageText.MaxLength
        let mutable coalescer = ReplyCoalescer(bot.Clock, coalesceInterval)
        let collected = ResizeArray<AgentResponseUpdate>()
        let mutable currentMessage: MessageId option = None
        let mutable aborted = false
        /// `true` once this turn has retired at least one message via `rollOver` — the final
        /// classification's own signal (see `StreamOutcome.RecoveryText`'s doc comment) that a LATER
        /// vanish-with-no-live-message must recover only the missing tail, never the whole turn.
        /// Set unconditionally at the top of `rollOver`, before its own send/edit can fail: reaching
        /// `rollOver` at all already means `MessageSplitting.split` found genuine overflow for this
        /// turn, so the spill has functionally begun regardless of whether it goes on to succeed —
        /// and a failed roll aborts the whole turn (`aborted <- true`) before classification is ever
        /// reached, so this flag being set on that path has no visible effect either way.
        let mutable everSpilled = false

        /// Retires the OUTGOING message with a mandatory final edit of `keep` — or, if the very
        /// FIRST delta of the whole turn already overflows on its own (no message exists yet at
        /// all), sends `keep` as an ordinary new message instead of silently dropping it — then
        /// sends `newSlice` as the NEW current message immediately: no cadence gate, since a
        /// rolled-over message's own first content is never paced. Seeds a fresh coalescer for the
        /// new message, already marked emitted for `newSlice`. Returns `false` (aborting the roll)
        /// only if retiring/starting the outgoing message itself fails to reach the wire at all.
        let rollOver (keep: string) (newSlice: string) : Task<bool> =
            task {
                everSpilled <- true

                let! outgoingOk =
                    task {
                        match MessageText.create keep with
                        | Error _ ->
                            // Unreachable in practice: `keep` is a `MessageSplitting.split` chunk,
                            // always <= maxLen; only an all-whitespace chunk (trimmed to empty)
                            // could land here, which `split`'s own trimming only ever produces for
                            // pathological all-whitespace input.
                            return true
                        | Ok validatedKeep ->
                            match currentMessage with
                            | Some id ->
                                let! outcome = guardedEmit 3 (fun () -> bot.EditText(chat, id, validatedKeep))

                                match outcome with
                                | Error ex ->
                                    observer.OnInvalidOutput(chat, DeliveryFailed $"failed to finalize a spilled message: %s{ex.Message}")
                                | Ok EditNotFound ->
                                    // The message being retired vanished (the user deleted it) before its
                                    // own final keep-slice could be written — its tail is lost. Reported,
                                    // not silently swallowed, exactly as the mid-stream edit and the
                                    // end-of-stream final flush already report their own `EditNotFound`.
                                    observer.OnInvalidOutput(chat, DeliveryFailed "a spilled message to finalize no longer exists")
                                | Ok(EditApplied | EditNotModified) -> ()

                                return true
                            | None ->
                                // No message exists yet at all — the STREAM's own very first update
                                // already overflowed on its own. `keep` is this turn's first-ever
                                // content and must still reach the wire as an ordinary new send, not
                                // be silently dropped in favor of only `newSlice`. Mandatory, like
                                // every other send in this function — routed through `guardedEmit` so
                                // a transient 429 is bounded-retried rather than aborting the whole
                                // roll on the FIRST rate limit hit.
                                let! attempt = guardedEmit 3 (fun () -> bot.SendText(chat, validatedKeep))

                                match attempt with
                                | Ok id ->
                                    currentMessage <- Some id
                                    return true
                                | Error ex ->
                                    observer.OnInvalidOutput(chat, DeliveryFailed $"initial send failed: %s{ex.Message}")
                                    return false
                    }

                if not outgoingOk then
                    return false
                else
                    match MessageText.create newSlice with
                    | Error _ ->
                        observer.OnInvalidOutput(chat, DeliveryFailed "a continuation message's own overflow slice failed validation")
                        return false
                    | Ok validatedSlice ->
                        // Mandatory, not best-effort: no further stream update is coming to retry
                        // this send against — routed through `guardedEmit` for the same reason as
                        // every other mandatory emit in this file.
                        let! attempt = guardedEmit 3 (fun () -> bot.SendText(chat, validatedSlice))

                        match attempt with
                        | Ok newId ->
                            currentMessage <- Some newId
                            coalescer <- ReplyCoalescer(bot.Clock, coalesceInterval, seed = newSlice)
                            coalescer.MarkEmitted(bot.Clock())
                            return true
                        | Error ex ->
                            observer.OnInvalidOutput(chat, DeliveryFailed $"failed to send a continuation message: %s{ex.Message}")
                            return false
            }

        /// Rolls every chunk in `pending` forward in turn — all but the last become their OWN
        /// already-finalized message (the rare case one delta alone spans several message-lengths);
        /// the LAST seeds the new current coalescer.
        let rec rollAll (pending: string list) (outgoing: string) : Task<unit> =
            task {
                match pending with
                | [ last ] ->
                    let! ok = rollOver outgoing last
                    if not ok then aborted <- true
                | mid :: more ->
                    let! ok = rollOver outgoing mid

                    if ok then
                        do! rollAll more mid
                    else
                        aborted <- true
                | [] -> ()
            }

        task {
            try
                use enumerator = (run ()).GetAsyncEnumerator(cts.Token)
                let mutable moving = true

                while moving && not aborted do
                    let! hasNext = enumerator.MoveNextAsync()

                    if not hasNext then
                        moving <- false
                    else
                        let update = enumerator.Current
                        collected.Add update
                        coalescer.Append update.Text

                        match MessageSplitting.split maxLen coalescer.RunningText with
                        | [] -> () // `split` returns `[]` only for `""` — the running text is still empty
                        | [ _ ] ->
                            // No overflow — the ordinary single-message send/edit transition.
                            match currentMessage, coalescer.IsDue(bot.Clock()) with
                            | None, _ when not (String.IsNullOrWhiteSpace coalescer.RunningText) ->
                                match MessageText.create coalescer.RunningText with
                                | Error _ -> () // unreachable: already whitespace- and length-guarded above
                                | Ok validated ->
                                    // Mandatory: the turn's very first message. Routed through the
                                    // (clamped, `guardedEmit`) bounded retry rather than an ordinary
                                    // try/with, so a single transient 429 does not discard the whole
                                    // reply (and any pending approval riding on it) — matching how the
                                    // mid-stream EDIT just below already absorbs a 429 of its own.
                                    let! attempt = guardedEmit 3 (fun () -> bot.SendText(chat, validated))

                                    match attempt with
                                    | Ok id ->
                                        currentMessage <- Some id
                                        coalescer.MarkEmitted(bot.Clock())
                                    | Error ex ->
                                        observer.OnInvalidOutput(chat, DeliveryFailed $"initial send failed: %s{ex.Message}")
                                        aborted <- true
                            | Some id, true ->
                                match MessageText.create coalescer.RunningText with
                                | Error _ -> () // unreachable: already length-guarded above; text can't have gone whitespace-only once sent
                                | Ok validated ->
                                    try
                                        let! outcome = bot.EditText(chat, id, validated)

                                        match outcome with
                                        | EditApplied
                                        | EditNotModified -> coalescer.MarkEmitted(bot.Clock())
                                        | EditNotFound ->
                                            observer.OnInvalidOutput(chat, DeliveryFailed "the live message no longer exists")
                                            currentMessage <- None // a LATER split still rolls to a fresh send
                                    with
                                    | :? Telegram.Bot.Exceptions.ApiRequestException as ex when ex.ErrorCode = 429 ->
                                        let retryAfter =
                                            ex.Parameters
                                            |> Option.ofObj
                                            |> Option.bind (fun p -> Option.ofNullable p.RetryAfter)
                                            |> Option.map (float >> TimeSpan.FromSeconds >> ValueSome)
                                            |> Option.defaultValue ValueNone

                                        coalescer.NotifyRateLimited(bot.Clock(), retryAfter) // never blocks
                                    | ex -> observer.OnInvalidOutput(chat, DeliveryFailed $"edit failed: %s{ex.Message}")
                            | _ -> () // not due yet, or nothing to send yet
                        | keep :: rest -> do! rollAll rest keep
            with
            | :? OperationCanceledException when cts.IsCancellationRequested ->
                // A graceful shutdown (`MafBridge.DisposeAsync`'s own `cts.Cancel()`) cancels the
                // enumerator's own `MoveNextAsync` mid-stream — NOT a genuine stream failure. Neither
                // the failure-note edit (`TurnProcessing.failureNote` is a RESUME-path string,
                // misapplied to an ordinary restart) nor `OnStreamFailed` runs: an in-flight turn
                // that happens to be cancelled by a routine restart should never destroy the user's
                // own partial narration or be reported as an outage. The chat lock is still released
                // normally — `withChatLock`'s own `finally` (the caller of this function) runs
                // regardless of how this `task { }` completes. Guarded on `cts.IsCancellationRequested`
                // so this ONLY absorbs OUR OWN shutdown: an `OperationCanceledException` raised while
                // `cts` is still live is an agent-origin cancellation (e.g. a model backend's own
                // `HttpClient` timeout surfacing as `TaskCanceledException`) — a genuine failure that
                // must fall through to the `| ex ->` arm below and be reported, exactly as the
                // non-streaming path's own `OnTurnFailed` reports the identical failure.
                aborted <- true
            | ex ->
                match currentMessage with
                | None -> observer.OnTurnFailed(chat, ex)
                | Some id ->
                    let! _ = guardedEmit 1 (fun () -> bot.EditText(chat, id, MessageText.unsafe (TurnProcessing.failureNote "the reply")))
                    streamingObserver.OnStreamFailed(chat, id, ex)

                aborted <- true

            if aborted then
                return None
            elif collected.Count = 0 then
                observer.OnEmptyTurn chat
                return None
            else
                let finalResponse = AgentResponseExtensions.ToAgentResponse collected
                let detected = ApprovalDetection.detect chat finalResponse

                match currentMessage with
                | Some _ ->
                    // Bypasses the ordinary cadence gate on purpose (see `NeedsFinalFlush`'s own doc
                    // comment) — this asks ONLY "has anything changed since the last successful
                    // send/edit", not "would an ordinary tick be due right now".
                    let needsFinalFlush = coalescer.IsDue DateTimeOffset.MaxValue

                    return
                        Some
                            { FinalResponse = finalResponse
                              Detected = detected
                              LastMessage = currentMessage
                              LastSlice = coalescer.RunningText
                              NeedsFinalFlush = needsFinalFlush
                              RecoveryText = finalResponse.Text (* unused whenever LastMessage = Some _ *) }
                | None ->
                    // `currentMessage = None` here for one of three reasons — see
                    // `StreamOutcome.RecoveryText`'s own doc comment for the full reasoning:
                    //   (a) nothing ever touched the wire live (every tick was gated, one giant
                    //       update completed the stream before its own edit was ever due, or the
                    //       WHOLE turn was a bare approval with no narration text at all — a
                    //       `ToolApprovalRequestContent`-only update still counts as `collected` but
                    //       never appends anything to the coalescer);
                    //   (b) the turn's ONE message was sent, then vanished (`EditNotFound`), with no
                    //       spill ever happening and no further update arriving to trigger a fresh
                    //       resend;
                    //   (c) same as (b), but AFTER this turn had already spilled across more than one
                    //       message — the vanished message was the LAST of several, and at least one
                    //       EARLIER message is already finalized on the wire.
                    // (a) and (b) both want the complete reply re-derived from `finalResponse.Text` —
                    // nothing else exists yet to duplicate. Only (c) must recover just the missing
                    // tail (`coalescer.RunningText`, unchanged since the vanish) instead, or it would
                    // re-send every already-finalized earlier message a second time.
                    let recoveryText = if everSpilled then coalescer.RunningText else finalResponse.Text

                    // Gated on `everSpilled`, OR `detected` non-empty, OR non-blank text — not text
                    // alone: a bare approval has `detected` non-empty but `finalResponse.Text = ""`,
                    // and dropping THAT case here would silently discard the approval and leave the
                    // agent's session mid-turn forever (`finalizeStreamingTurn`'s own `first :: rest,
                    // None` arm already renders it via `sendNewApproval` once it gets here).
                    if everSpilled || not (List.isEmpty detected) || not (String.IsNullOrWhiteSpace finalResponse.Text) then
                        return
                            Some
                                { FinalResponse = finalResponse
                                  Detected = detected
                                  LastMessage = None
                                  LastSlice = ""
                                  NeedsFinalFlush = true
                                  RecoveryText = recoveryText }
                    else
                        observer.OnEmptyTurn chat
                        return None
        }

    /// The finalize step — shared by `StartRun`'s and `HandleIncomingMessage`'s streaming branches,
    /// mirroring `processInitialResponse`'s own role for the non-streaming path. NEVER THROWS: the
    /// whole body is wrapped in one outer try/with reporting via `OnInvalidOutput`, mirroring
    /// `processInitialResponse`'s own discipline — so the caller's `do! finalize; do!
    /// persistConversation` always reaches the persist call.
    let finalizeStreamingTurn (chat: ChatId) (owner: OwnerScope) (outcome: StreamOutcome) : Task =
        task {
            try
                match outcome.Detected, outcome.LastMessage with
                | [], Some id ->
                    // The mandatory final flush: guarantees the message shows the COMPLETE reply even
                    // if the very last delta arrived inside the coalescer's own interval window and
                    // never got its own periodic edit. Skipped when `NeedsFinalFlush = false` — the
                    // message's own last successful send/edit already shows `LastSlice` verbatim
                    // (e.g. a single content-bearing update sent once, nothing further arriving), so
                    // re-editing would only reproduce Telegram's own "message is not modified" no-op.
                    if outcome.NeedsFinalFlush then
                        match MessageText.create outcome.LastSlice with
                        | Error _ -> observer.OnInvalidOutput(chat, DeliveryFailed "the final message slice failed validation")
                        | Ok validated ->
                            let! r = guardedEmit 3 (fun () -> bot.EditText(chat, id, validated))

                            match r with
                            | Error ex -> observer.OnInvalidOutput(chat, DeliveryFailed $"final flush failed: %s{ex.Message}")
                            // `EditNotFound` is a SUCCESSFUL HTTP call (never an exception) that
                            // `guardedEmit` happily wraps in `Ok` — matching `Ok _ -> ()` here would
                            // silently drop the pending tail text with no report at all, the same
                            // class of gap `sendChainedApprovalCore`'s own `EditNotFound` handling
                            // (immediately above) already closes for a chained approval's render.
                            | Ok EditNotFound ->
                                observer.OnInvalidOutput(
                                    chat,
                                    DeliveryFailed "the final flush's own target message no longer exists — the tail text was not delivered"
                                )
                            | Ok(EditApplied | EditNotModified) -> ()
                | [], None ->
                    // No LIVE message exists (every tick was gated, the stream produced its whole
                    // reply before the coalescer's very first tick was ever due, or the last live
                    // message vanished with nothing further arriving to replace it) — split
                    // `outcome.RecoveryText` fresh and send each non-blank chunk as its own new
                    // message. Deliberately NOT `outcome.FinalResponse.Text` — see
                    // `StreamOutcome.RecoveryText`'s own doc comment: once a turn has already spilled
                    // across more than one message, `FinalResponse.Text` is the WHOLE turn and would
                    // duplicate every earlier, already-finalized message.
                    let chunks =
                        MessageSplitting.split MessageText.MaxLength outcome.RecoveryText
                        |> List.filter (fun chunk -> not (String.IsNullOrWhiteSpace chunk))

                    if List.isEmpty chunks then
                        observer.OnEmptyTurn chat
                    else
                        // Mandatory, like every other send in this file: no further stream update is
                        // ever coming to retry a chunk against — routed through `guardedEmit` so a
                        // transient 429 on, say, the FIRST of three chunks is bounded-retried rather
                        // than discarding the rest of the reply outright. On exhaustion (or any other
                        // failure), reported via `OnInvalidOutput` and the remaining chunks are
                        // skipped — mirrors this loop's own pre-`guardedEmit` behavior, where an
                        // unguarded send failure here escaped straight to this function's own outer
                        // try/with and stopped the loop the same way.
                        let mutable chunkFailed = false

                        for chunk in chunks do
                            if not chunkFailed then
                                match MessageText.create chunk with
                                | Error _ -> observer.OnInvalidOutput(chat, DeliveryFailed "a split reply chunk failed validation")
                                | Ok validated ->
                                    let! attempt = guardedEmit 3 (fun () -> bot.SendText(chat, validated))

                                    match attempt with
                                    | Ok _ -> ()
                                    | Error ex ->
                                        observer.OnInvalidOutput(chat, DeliveryFailed $"failed to send a split reply chunk: %s{ex.Message}")
                                        chunkFailed <- true
                | first :: rest, Some id ->
                    // A live message already exists: the FIRST detected approval repurposes it —
                    // its own already-shown text (`LastSlice`, this message's own slice once a turn
                    // has spilled, never the whole accumulated reply) is kept, and the decision
                    // buttons are added on top, via `sendChainedApprovalCore`'s body override. Every
                    // OTHER detected approval in the same turn (a further request the agent raised
                    // alongside the first) goes through the unchanged, no-override `sendNewApproval`
                    // — its own fresh message, exactly like a non-streaming turn's own further
                    // approvals.
                    do! sendChainedApprovalCore chat owner id first (Some outcome.LastSlice)

                    for d in rest do
                        do! sendNewApproval chat owner d
                | first :: rest, None ->
                    // No live message ever existed for this turn (every tick was gated, or the
                    // stream's very first content-bearing update already carried the approval) — the
                    // FIRST detected approval has no message to chain onto either, so it goes
                    // through `sendNewApproval` too, same as every other one.
                    do! sendNewApproval chat owner first

                    for d in rest do
                        do! sendNewApproval chat owner d
            with ex ->
                observer.OnInvalidOutput(chat, DeliveryFailed $"failed to deliver this streamed turn's outcome: %s{ex.Message}")
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
                            // With a store configured, restore this chat's durable record (session +
                            // still-pending approvals) BEFORE the peek below — a post-restart tap is
                            // the FIRST thing to touch this chat since the process came back up, so
                            // without this, `pendingApprovals` is still the fresh, empty, in-memory
                            // table `MafBridge`'s constructor left it in and every restart tap would
                            // land stale regardless of what was persisted. `conversations.GetOrCreate`
                            // is `Lazy`-cached (`Conversations.fs`), so the resume branch's OWN
                            // `GetOrCreate` call below reuses this SAME restored session rather than
                            // restoring twice. No store configured ⇒ this is skipped entirely — the
                            // no-store path never eagerly creates a session for what may be a stale
                            // tap, byte-identical to before this feature existed.
                            if bot.SessionStore.IsSome then
                                try
                                    let! _ = conversations.GetOrCreate(chat, restoreOrCreate chat)
                                    ()
                                with _ ->
                                    // Swallowed, deliberately: `restoreOrCreate` itself already
                                    // reports the specific `SessionFailure` via
                                    // `OnSessionRestoreFailed` before EVER re-raising (both its
                                    // decode/validate-failure branch, which removes the bad record
                                    // first, and its store-read-failure branch, which re-raises
                                    // WITHOUT falling back to a fresh session so a transient blip can
                                    // never silently overwrite an intact record — see its own doc
                                    // comment). Reporting AGAIN here would double-report the identical
                                    // failure for the SAME tap. Whatever threw, nothing was rehydrated
                                    // into `pendingApprovals` for this chat, so the peek below finds no
                                    // entry and reports the tap stale on its own — exactly the right
                                    // outcome for a tap that arrived while this chat's restore was
                                    // failing.
                                    ()

                            match pendingApprovals.TryGet(chat, descriptor.RequestId) with
                            | ValueNone -> observer.OnStaleDecision descriptor
                            | ValueSome peeked when not (Expiry.isLive (bot.Clock()) peeked.ExpiresAt) ->
                                // Checked BEFORE the owner scope, and separately from
                                // `pendingApprovals.TryConsume` below: a CHAINED approval's own
                                // replacement binding (`sendChainedApproval`'s `EditKeyboardPlan`) has
                                // no expiry of its own — the Tool Router's binding-level expiry check
                                // (which refuses an expired FRESH send's tap before it ever reaches
                                // here) never applies to it, so `PendingApproval.ExpiresAt` is the
                                // ONLY signal this leaf has left. Without this branch, an expired
                                // chained approval's tap falls through to a normal resume — the SAME
                                // decision that lands stale after a restart (the persist filter prunes
                                // it) instead RESUMES pre-restart, a correctness gap this branch closes
                                // by refusing it here too, matching `Expiry.isLive`'s own semantics.
                                // Consume the dead entry so it stops lingering in `pendingApprovals`
                                // until `AbandonAllFor`/process end — a repeat tap on the same
                                // (reusable, chained) binding reports exactly one `OnStaleDecision`
                                // either way, consumed here or not; this consume is in-memory table
                                // hygiene, not what keeps the observer stream from double-reporting.
                                pendingApprovals.TryConsume(chat, descriptor.RequestId) |> ignore
                                observer.OnStaleDecision descriptor
                            | ValueSome peeked when not (OwnerScope.isAllowed peeked.Owner (Some ctx.User.Id)) ->
                                ctx.Answer(OwnerScope.DefaultDeniedNotice)
                            | ValueSome _ ->
                                match pendingApprovals.TryConsume(chat, descriptor.RequestId) with
                                | ValueNone -> observer.OnStaleDecision descriptor
                                | ValueSome pending ->
                                    // `conversations.GetOrCreate` runs INSIDE this same try, alongside
                                    // `agent.RunAsync` — not before it: a `CreateSessionAsync` fault it
                                    // surfaces (this chat's cached session was dropped by an earlier
                                    // resume failure, and re-creating it now fails too) must classify the
                                    // SAME way an `agent.RunAsync` fault does, `OnResumeFailed`, not
                                    // escape uncaught to the Tool Router's own `OnHookFailed` — a
                                    // DIFFERENT, Core-level seam a host's `IMafObserver` may never be
                                    // listening on at all (mirrors `StartRun`/`HandleIncomingMessage`'s
                                    // own reasoning for wrapping their `GetOrCreate` call the same way).
                                    let! resumed =
                                        task {
                                            try
                                                let! conversation = conversations.GetOrCreate(chat, restoreOrCreate chat)
                                                // Persist the atomic in-memory consume before the agent can act on it.
                                                // A failed write leaves the request pending and never resumes the agent.
                                                let! durableClaimed = tryPersistConversation chat conversation.Session

                                                if durableClaimed then
                                                    let responseContent = pending.Request.CreateResponse approved
                                                    let contents = ResizeArray<AIContent> [ responseContent :> AIContent ]
                                                    let resumeMessage = ChatMessage(ChatRole.User, contents)
                                                    let! response = agent.RunAsync(resumeMessage, conversation.Session, cancellationToken = cts.Token)
                                                    return Ok(Some(response, conversation.Session))
                                                else
                                                    return Ok None
                                            with ex ->
                                                return Error ex
                                        }

                                    match resumed with
                                    | Ok None ->
                                        pendingApprovals.Add pending
                                        ctx.Answer("The decision could not be saved. Please try again.", alert = true)
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

                                        // The conversation is presumably dead — its durable record
                                        // (if any) is stale from this point on, so it is removed
                                        // alongside the in-memory drop above rather than left to
                                        // resurrect a dead continuation on the NEXT restart.
                                        match bot.SessionStore with
                                        | None -> ()
                                        | Some store ->
                                            try
                                                do! store.Remove(chat, cts.Token)
                                            with removeEx ->
                                                sessionObserver.OnSessionPersistFailed(chat, removeEx)
                                    | Ok(Some(response, session)) ->
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

                                        // Persisted AFTER the outcome/chained-approval sends above, so a
                                        // chained approval this very resume raised is itself captured in
                                        // the record this write produces — surviving a SECOND restart.
                                        do! persistConversation chat session
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
                    let! conversation = conversations.GetOrCreate(chat, restoreOrCreate chat)
                    let resolvedOwner = RunOwner.resolve explicitOwner None chat

                    match bot.Streaming with
                    | None ->
                        let! response = agent.RunAsync(prompt, conversation.Session, cancellationToken = cts.Token)
                        do! processInitialResponse chat resolvedOwner response
                        do! persistConversation chat conversation.Session
                    | Some interval ->
                        let! outcome =
                            runStreamingTurn chat resolvedOwner interval (fun () ->
                                agent.RunStreamingAsync(prompt, conversation.Session, cancellationToken = cts.Token))

                        match outcome with
                        | Some o ->
                            do! finalizeStreamingTurn chat resolvedOwner o
                            do! persistConversation chat conversation.Session
                        | None -> () // already reported (OnTurnFailed/OnStreamFailed/OnInvalidOutput/OnEmptyTurn) inside runStreamingTurn
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
                    let! conversation = conversations.GetOrCreate(message.Chat, restoreOrCreate message.Chat)
                    let resolvedOwner = RunOwner.resolve None (Some message.Sender.Id) message.Chat

                    match bot.Streaming with
                    | None ->
                        let! response = agent.RunAsync(message.Text, conversation.Session, cancellationToken = cts.Token)
                        do! processInitialResponse message.Chat resolvedOwner response
                        do! persistConversation message.Chat conversation.Session
                    | Some interval ->
                        let! outcome =
                            runStreamingTurn message.Chat resolvedOwner interval (fun () ->
                                agent.RunStreamingAsync(message.Text, conversation.Session, cancellationToken = cts.Token))

                        match outcome with
                        | Some o ->
                            do! finalizeStreamingTurn message.Chat resolvedOwner o
                            do! persistConversation message.Chat conversation.Session
                        | None -> () // already reported (OnTurnFailed/OnStreamFailed/OnInvalidOutput/OnEmptyTurn) inside runStreamingTurn
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
        match options.ApprovalExpiry with
        | ValueSome expiry when expiry <= TimeSpan.Zero ->
            invalidArg "options" "MAF approval expiry must be positive when configured"
        | _ -> ()

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
    /// A startup barrier keeps update ingestion paused until the bridge exists and its decision
    /// tools are registered. The config-time message handler waits on the same readiness task, so
    /// neither a polling backlog message nor a durable callback can observe a half-built bridge.
    /// If `build` throws, `buildOrDispose` cancels the paused bot before re-raising.
    let private buildOrDispose (bot: TgBot) (agent: AIAgent) (options: MafBridgeOptions) : Task<MafBridge> =
        task {
            try
                return BridgeBuild.build bot agent options
            with ex ->
                // The cleanup dispose itself is guarded so a fault IN IT (e.g. the bot's background
                // run loop or dispatcher drain throwing during teardown) can never mask `ex` — the
                // ORIGINAL build failure (no `.WithTools`, or a double-attach) is always the one a
                // caller sees; a disposal problem on top of an already-failed build has nothing more
                // useful to add.
                try
                    do! (bot :> IAsyncDisposable).DisposeAsync()
                with _ ->
                    ()

                return raise ex
        }

    let startPollingWith (options: MafBridgeOptions) (config: TgBotConfig) (agent: AIAgent) : Task<MafBridge> =
        task {
            let ready = TaskCompletionSource<MafBridge>(TaskCreationOptions.RunContinuationsAsynchronously)

            let handler: MessageHandler =
                fun message ct ->
                    task {
                        let! bridge = ready.Task.WaitAsync(ct)
                        do! bridge.HandleIncomingMessage message
                    }
                    :> Task

            let configured = config.WithOnMessage handler
            let configured = { configured with Common = configured.Common |> CommonConfig.withStartupBarrier ready.Task }
            let! bot = TgBot.startPolling configured

            try
                let! bridge = buildOrDispose bot agent options
                ready.SetResult bridge
                return bridge
            with ex ->
                ready.TrySetException ex |> ignore
                return raise ex
        }

    /// Zero-config variant of `startPollingWith`.
    let startPolling (config: TgBotConfig) (agent: AIAgent) : Task<MafBridge> =
        startPollingWith MafBridgeOptions.defaults config agent

    /// The webhook-transport counterpart to `startPollingWith` — same config-time `OnMessage`
    /// wiring, same reasoning (see that function's own doc comment).
    let startWebhookWith (options: MafBridgeOptions) (config: TgWebhookConfig) (agent: AIAgent) : Task<MafBridge> =
        task {
            let ready = TaskCompletionSource<MafBridge>(TaskCreationOptions.RunContinuationsAsynchronously)

            let handler: MessageHandler =
                fun message ct ->
                    task {
                        let! bridge = ready.Task.WaitAsync(ct)
                        do! bridge.HandleIncomingMessage message
                    }
                    :> Task

            let configured = config.WithOnMessage handler
            let configured = { configured with Common = configured.Common |> CommonConfig.withStartupBarrier ready.Task }
            let! bot = TgBot.startWebhook configured

            try
                let! bridge = buildOrDispose bot agent options
                ready.SetResult bridge
                return bridge
            with ex ->
                ready.TrySetException ex |> ignore
                return raise ex
        }

    /// Zero-config variant of `startWebhookWith`.
    let startWebhook (config: TgWebhookConfig) (agent: AIAgent) : Task<MafBridge> = startWebhookWith MafBridgeOptions.defaults config agent
