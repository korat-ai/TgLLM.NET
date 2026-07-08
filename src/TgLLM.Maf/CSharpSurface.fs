/// The leaf's C#-idiomatic surface: a sealed class with static async factories, BCL delegates
/// (`Func`/`Action`), plain DTOs, and nullable value types — no `FSharpFunc`/`FSharpOption`/
/// `FSharpValueOption` anywhere on this surface (the Principle II idiom-leak canary applies to the
/// leaf exactly as it does to `TgLLM.CSharp`). Method overloading stands in for C#'s optional-
/// parameter defaults throughout, rather than `[<Optional; DefaultParameterValue>]` attributes —
/// simpler to keep correct, and just as clean a call site.
namespace TgLLM.Maf

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp

/// C#-facing mirror of `ApprovalPrompt` — plain BCL types only.
[<Sealed>]
type ApprovalPromptInfo(tool: string, arguments: IReadOnlyList<KeyValuePair<string, string>>, chatId: int64) =
    member _.Tool: string = tool
    member _.Arguments: IReadOnlyList<KeyValuePair<string, string>> = arguments
    member _.ChatId: int64 = chatId

/// C#-facing mirror of `ApprovalRender`. The one-argument constructor fills in the fixed
/// "Approve"/"Reject" labels — the zero-config shape a formatter that only rewrites the body needs.
[<Sealed>]
type ApprovalRenderInfo(body: string, approveLabel: string, rejectLabel: string) =
    new(body: string) = ApprovalRenderInfo(body, "Approve", "Reject")
    member _.Body: string = body
    member _.ApproveLabel: string = approveLabel
    member _.RejectLabel: string = rejectLabel

/// One condition `IMafObserver` surfaced, flattened to a stable string tag (`Kind`) a C# host can
/// switch on without touching an F# union, plus a human-readable `Description` for logging —
/// mirrors the A2UI C# façade's `A2uiErrorInfo` split.
[<Sealed>]
type MafSurfacedEvent(kind: string, description: string) =
    member _.Kind: string = kind
    member _.Description: string = description

/// Bridges a C#-supplied `Action<MafSurfacedEvent>` (or none) into `IMafObserver`, translating
/// every F# type (`ApprovalDescriptor`, `MafError`, `ProjectionProblem`, `exn`) at this ONE
/// boundary. Internal: its own members carry F# types dictated by the interface it implements, so
/// it must never be walked by the idiom-leak canary — mirrors `CSharpA2uiObserverBridge`'s role.
type internal CSharpMafObserverBridge(onSurfaced: Action<MafSurfacedEvent> | null) =

    let report (kind: string) (description: string) : unit =
        match Option.ofObj onSurfaced with
        | None -> ()
        | Some action -> action.Invoke(MafSurfacedEvent(kind, description))

    interface IMafObserver with
        member _.OnStaleDecision(descriptor: ApprovalDescriptor) =
            report "StaleDecision" $"request {descriptor.RequestId} (tool {descriptor.Tool}) in chat {descriptor.Chat} is no longer pending"

        member _.OnMalformedDecision(raw: string) =
            report "MalformedDecision" $"a decision payload did not parse: {raw}"

        member _.OnResumeFailed(descriptor: ApprovalDescriptor, error: exn) =
            report "ResumeFailed" $"resuming request {descriptor.RequestId} (tool {descriptor.Tool}) failed: {error.Message}"

        member _.OnEmptyTurn(chat: ChatId) =
            report "EmptyTurn" $"a turn in chat {UMX.untag chat} produced neither text nor an approval"

        member _.OnInvalidOutput(chat: ChatId, error: MafError) =
            report "InvalidOutput" $"output for chat {UMX.untag chat} failed validation: %A{error}"

        member _.OnProjectionProblem(problem: ProjectionProblem) =
            report "ProjectionProblem" $"%A{problem}"

/// Settings for `MafTelegramBridge.StartPollingAsync`/`StartWebhookAsync` — every member is a
/// plain, mutable, C#-safe property; omitting all of them (`MafBridgeSettings()`) is the
/// zero-config path.
[<Sealed>]
type MafBridgeSettings() =
    member val Formatter: Func<ApprovalPromptInfo, ApprovalRenderInfo> | null = null with get, set
    member val DefaultOwner: Nullable<OwnerScope> = Nullable() with get, set
    member val ApprovalExpiry: Nullable<TimeSpan> = Nullable() with get, set
    member val OnSurfaced: Action<MafSurfacedEvent> | null = null with get, set

module private CSharpBridgeSupport =

    let toFormatter (formatter: Func<ApprovalPromptInfo, ApprovalRenderInfo> | null) : ApprovalFormatter voption =
        match Option.ofObj formatter with
        | None -> ValueNone
        | Some f ->
            ValueSome(fun (prompt: ApprovalPrompt) ->
                let arguments =
                    prompt.Arguments
                    |> List.map (fun (name, value) -> KeyValuePair(name, value))
                    |> ResizeArray
                    :> IReadOnlyList<KeyValuePair<string, string>>

                let info = ApprovalPromptInfo(prompt.Tool, arguments, UMX.untag prompt.Chat)
                let render = f.Invoke info
                { Body = render.Body
                  ApproveLabel = render.ApproveLabel
                  RejectLabel = render.RejectLabel })

    let toOptions (settings: MafBridgeSettings | null) : MafBridgeOptions =
        match Option.ofObj settings with
        | None -> MafBridgeOptions.defaults
        | Some s ->
            { Formatter = toFormatter s.Formatter
              Observer = ValueSome(CSharpMafObserverBridge s.OnSurfaced :> IMafObserver)
              DefaultOwner = (if s.DefaultOwner.HasValue then ValueSome s.DefaultOwner.Value else ValueNone)
              ApprovalExpiry = (if s.ApprovalExpiry.HasValue then ValueSome s.ApprovalExpiry.Value else ValueNone) }

/// The C#-idiomatic entry point: wraps the F# `MafBridge` (`Maf.startPolling`/`startWebhook`)
/// behind static async factories and a plain instance surface.
[<Sealed>]
type MafTelegramBridge internal (inner: MafBridge) =

    /// The bot this bridge built — usable exactly like a hand-built `TgBot`.
    member _.Bot: TgBot = inner.Bot

    member this.StartRunAsync(chatId: int64, prompt: string) : Task =
        this.StartRunAsync(chatId, prompt, Nullable(), CancellationToken.None)

    member this.StartRunAsync(chatId: int64, prompt: string, owner: Nullable<OwnerScope>) : Task =
        this.StartRunAsync(chatId, prompt, owner, CancellationToken.None)

    /// Starts an agent turn in `chatId` on the host's own initiative — the C#-facing counterpart
    /// to `MafBridge.StartRun`. `ct` cancels the WAIT for this call to complete, same convention as
    /// `TelegramAgent.SendKeyboardAsync`'s own `ct` — it does not abort an already-started Telegram
    /// send or agent turn.
    member _.StartRunAsync(chatId: int64, prompt: string, owner: Nullable<OwnerScope>, ct: CancellationToken) : Task =
        let chat: ChatId = UMX.tag chatId
        let fsOwner = if owner.HasValue then Some owner.Value else None
        (inner.StartRun(chat, prompt, ?owner = fsOwner)).WaitAsync(ct)

    static member StartPollingAsync(config: TgBotConfig, agent: AIAgent) : Task<MafTelegramBridge> =
        MafTelegramBridge.StartPollingAsync(config, agent, MafBridgeSettings())

    /// Builds the bot from `config` (requires `.WithTools`), registers `maf-approve`/`maf-reject`
    /// into its Tool Router, and returns the live bridge — the C#-facing counterpart to
    /// `Maf.startPollingWith`.
    static member StartPollingAsync(config: TgBotConfig, agent: AIAgent, settings: MafBridgeSettings) : Task<MafTelegramBridge> =
        task {
            let! bridge = Maf.startPollingWith (CSharpBridgeSupport.toOptions settings) config agent
            return MafTelegramBridge bridge
        }

    static member StartWebhookAsync(config: TgWebhookConfig, agent: AIAgent) : Task<MafTelegramBridge> =
        MafTelegramBridge.StartWebhookAsync(config, agent, MafBridgeSettings())

    /// The C#-facing counterpart to `Maf.startWebhookWith`.
    static member StartWebhookAsync(config: TgWebhookConfig, agent: AIAgent, settings: MafBridgeSettings) : Task<MafTelegramBridge> =
        task {
            let! bridge = Maf.startWebhookWith (CSharpBridgeSupport.toOptions settings) config agent
            return MafTelegramBridge bridge
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() : ValueTask = (inner :> IAsyncDisposable).DisposeAsync()
