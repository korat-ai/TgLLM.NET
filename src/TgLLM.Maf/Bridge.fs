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

module private DecisionTools =

    [<Literal>]
    let ApproveName = "maf-approve"

    [<Literal>]
    let RejectName = "maf-reject"

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
                Plan.rows [ [ Plan.toolWithArg validRender.ApproveLabel DecisionTools.ApproveName argJson
                              Plan.toolWithArg validRender.RejectLabel DecisionTools.RejectName argJson ] ]
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

    /// Processes one INITIAL turn's response (currently only a `StartRun` call reaches this — an
    /// incoming text message is a future entry point onto the same path): every detected approval
    /// becomes its own fresh message (a turn that raises several pending requests presents each as
    /// its own decision); with none pending, the agent's own text reply is sent as a plain message;
    /// with neither, the turn is empty and surfaced rather than sending nothing silently.
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
    let sendChainedApproval (chat: ChatId) (owner: OwnerScope) (messageId: MessageId) (detected: ApprovalDetection.DetectedApproval) : Task =
        task {
            match TurnProcessing.buildKeyboard formatter chat detected with
            | Error e -> observer.OnInvalidOutput(chat, e)
            | Ok(render, plan) ->
                match MessageText.create render.Body with
                | Error _ -> observer.OnInvalidOutput(chat, BodyInvalid "the validated render's body failed to re-parse as MessageText")
                | Ok bodyText ->
                    let! _ = bot.EditKeyboardPlan(chat, messageId, bodyText, plan)

                    pendingApprovals.Add
                        { Chat = chat
                          Request = detected.Request
                          Owner = owner
                          MessageId = messageId }
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

    /// The `maf-approve`/`maf-reject` tool handler: parses the tapped button's descriptor,
    /// consumes the matching pending entry (at-most-once — a miss is stale, surfaced and refused),
    /// resumes the agent with the decision, and edits the approval message to the outcome or the
    /// next chained request. A resume failure is surfaced and leaves no live buttons behind
    /// (`OnResumeFailed` + a failure-note edit); the rest of this chat's pending entries are
    /// abandoned and its conversation is dropped, since the agent's own continuation is now
    /// presumably dead.
    member private _.HandleDecision(approved: bool) (ctx: PressContext) : Task<unit> =
        task {
            match ApprovalDescriptor.tryParse ctx.Arg with
            | None -> observer.OnMalformedDecision(ctx.Arg |> Option.ofObj |> Option.defaultValue "<none>")
            | Some descriptor ->
                let chat = ApprovalDescriptor.chat descriptor

                do!
                    withChatLock chat (fun () ->
                        task {
                            match pendingApprovals.TryConsume(chat, descriptor.RequestId) with
                            | ValueNone -> observer.OnStaleDecision descriptor
                            | ValueSome pending ->
                                let! conversation = conversations.GetOrCreate(chat, (fun () -> agent.CreateSessionAsync cts.Token))

                                try
                                    let responseContent = pending.Request.CreateResponse approved
                                    let contents = ResizeArray<AIContent> [ responseContent :> AIContent ]
                                    let resumeMessage = ChatMessage(ChatRole.User, contents)
                                    let! response = agent.RunAsync(resumeMessage, conversation.Session, cancellationToken = cts.Token)

                                    match ApprovalDetection.detect chat response with
                                    | next :: _ -> do! sendChainedApproval chat pending.Owner pending.MessageId next
                                    | [] -> do! editOutcome chat ctx approved descriptor.Tool response
                                with ex ->
                                    observer.OnResumeFailed(descriptor, ex)
                                    do! ctx.EditTextAsync(TurnProcessing.failureNote descriptor.Tool)
                                    pendingApprovals.AbandonAllFor chat |> ignore
                                    conversations.Drop chat
                        })
        }

    /// The bot this bridge built — usable exactly like a hand-built `TgBot` for anything else the
    /// host wants to send.
    member _.Bot: TgBot = bot

    /// Start an agent turn in `chat` on the host's own initiative. Serialized on the chat's own
    /// lock (see `chatLocks`'s doc comment) with this bridge's other turns for the same chat.
    /// `owner` overrides the owner scope for approvals THIS run raises; omitted, `RunOwner.resolve`
    /// applies the bridge's configured default, then the private-chat-peer/`Anyone` fallback.
    member _.StartRun(chat: ChatId, prompt: string, ?owner: OwnerScope) : Task =
        let explicitOwner = owner |> Option.orElse (ValueOption.toOption defaultOwner)

        withChatLock chat (fun () ->
            task {
                let! conversation = conversations.GetOrCreate(chat, (fun () -> agent.CreateSessionAsync cts.Token))
                let resolvedOwner = RunOwner.resolve explicitOwner None chat
                let! response = agent.RunAsync(prompt, conversation.Session, cancellationToken = cts.Token)
                do! processInitialResponse chat resolvedOwner response
            })
        :> Task

    /// Registers the decision tools into `bot`'s Tool Router — called once, by `Maf.startPolling`/
    /// `startWebhook`'s shared build path, right after construction and before either returns.
    member internal this.RegisterTools(tools: ToolRegistry) : unit =
        tools.Register(
            DecisionTools.ApproveName,
            (fun ctx -> this.HandleDecision true ctx),
            description = "Approve the agent's pending tool call."
        )
        |> ignore

        tools.Register(
            DecisionTools.RejectName,
            (fun ctx -> this.HandleDecision false ctx),
            description = "Reject the agent's pending tool call."
        )
        |> ignore

    interface IAsyncDisposable with
        member _.DisposeAsync() : ValueTask =
            task {
                cts.Cancel()
                cts.Dispose()
                do! (bot :> IAsyncDisposable).DisposeAsync()
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
            match ToolName.create DecisionTools.ApproveName, ToolName.create DecisionTools.RejectName with
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
    let startPollingWith (options: MafBridgeOptions) (config: TgBotConfig) (agent: AIAgent) : Task<MafBridge> =
        task {
            let! bot = TgBot.startPolling config
            return BridgeBuild.build bot agent options
        }

    /// Zero-config variant of `startPollingWith`.
    let startPolling (config: TgBotConfig) (agent: AIAgent) : Task<MafBridge> =
        startPollingWith MafBridgeOptions.defaults config agent

    let startWebhookWith (options: MafBridgeOptions) (config: TgWebhookConfig) (agent: AIAgent) : Task<MafBridge> =
        task {
            let! bot = TgBot.startWebhook config
            return BridgeBuild.build bot agent options
        }

    /// Zero-config variant of `startWebhookWith`.
    let startWebhook (config: TgWebhookConfig) (agent: AIAgent) : Task<MafBridge> = startWebhookWith MafBridgeOptions.defaults config agent
