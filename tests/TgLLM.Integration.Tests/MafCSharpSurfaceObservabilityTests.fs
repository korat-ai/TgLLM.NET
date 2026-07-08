/// Acceptance for the C#-facing `MafTelegramBridge`/`MafBridgeSettings` surface's own observer
/// resolution: a C# host that wires a LOGGER but never sets `MafBridgeSettings.OnSurfaced` must
/// still reach `BridgeBuild.resolveObserver`'s bot-logger fallback — exactly the zero-config
/// behavior F#'s own `Maf.startPolling` already gives (`LoggingMafObserver`, `Bridge.fs`), not a
/// permanently-silent `CSharpMafObserverBridge` wrapping a `null` action.
module TgLLM.Integration.Tests.MafCSharpSurfaceObservabilityTests

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Expecto
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "callback_data" |> asString

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then
            failtest "timed out waiting for the expected request"
    }

let private deliverTap (server: FakeBotApiServer) (updateId: int) (queryId: string) (token: string) (chat: int64) (messageId: int) (userId: int64) : Task =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.callbackQueryUpdate updateId queryId token chat messageId userId "Tester" ])
    Task.CompletedTask

type private NoopScope() =
    interface IDisposable with
        member _.Dispose() : unit = ()

/// A minimal `ILogger` fake that records `LogWarning` calls — mirrors
/// `EditInPlaceTests.fs`'s own `RecordingLogger`, trimmed to just what this file needs.
type private RecordingLogger() =
    let warnings = ResizeArray<string>()
    member _.Warnings: string list = List.ofSeq warnings

    interface ILogger with
        member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable = new NoopScope()
        member _.IsEnabled(_logLevel: LogLevel) : bool = true

        member _.Log<'TState>
            (
                logLevel: LogLevel,
                _eventId: EventId,
                state: 'TState,
                error: exn | null,
                formatter: Func<'TState, exn | null, string>
            ) : unit =
            match logLevel with
            | LogLevel.Warning -> warnings.Add(formatter.Invoke(state, error))
            | _ -> ()

[<Tests>]
let mafCSharpSurfaceObservabilityTests =
    testList "MafBridge C# surface — logger fallback" [

        testCaseAsync "a C# bridge WITH a logger and no OnSurfaced still reaches the bot's own logger fallback on a stale decision"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9601L
                    let logger = RecordingLogger()

                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", []); RepliesWith "sent" ]
                    let tools = ToolRegistry.create ()

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(tools)
                            .WithLogger(logger)

                    // Zero-config settings — `OnSurfaced` is left `null`, exactly the shape a C#
                    // host reaches by constructing a bare `MafBridgeSettings()`.
                    let settings = MafBridgeSettings()
                    use! bridge = MafTelegramBridge.StartPollingAsync(config, agent, settings)

                    do! bridge.StartRunAsync(chat, "Email alice.")
                    do! pollUntil 5000 (fun () -> server.RequestsFor "sendMessage" |> List.isEmpty |> not)

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent
                    let rejectToken = callbackDataAt 0 1 sent

                    do! deliverTap server 1 "q-approve-first" approveToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    // The SIBLING (Reject) button races in after req-1 was already decided — this
                    // is a well-known `OnStaleDecision` trigger (`MafBridgeRefusalTests.fs`'s own
                    // sibling-race test); here the point is WHERE it surfaces: the bot's own
                    // logger, since no `OnSurfaced` was ever wired.
                    do! deliverTap server 2 "q-reject-race" rejectToken chat 1 chat
                    do! pollUntil 5000 (fun () -> logger.Warnings |> List.exists (fun w -> w.Contains "req-1"))

                    Expect.isTrue
                        (logger.Warnings |> List.exists (fun w -> w.Contains "req-1"))
                        "the stale sibling decision reached the bot's own logger fallback, not a permanently-silent observer"
                }
                |> Async.AwaitTask
        }
    ]
