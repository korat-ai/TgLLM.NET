/// Acceptance for the MAF bridge's ack timing and approval expiry: a slow (multi-second) resume
/// is still acked within the deferred-ack watchdog's spinner budget, since the resume runs INSIDE
/// the `maf-approve`/`maf-reject` tool itself — the SAME deferred-ack machinery every other Tool
/// Router tool already rides, with no code of its own; a tap on an EXPIRED decision keyboard never
/// reaches the bridge at all — it lands in the engine's own pre-existing stale/unknown-token path
/// (acked, no resume), exactly like any other expired `ToolBinding`.
module TgLLM.Integration.Tests.MafAckTimingTests

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
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

let private deliverTap (server: FakeBotApiServer) (updateId: int) (queryId: string) (token: string) (chat: int64) (messageId: int) (userId: int64) : unit =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.callbackQueryUpdate updateId queryId token chat messageId userId "Tester" ])

let private startBridgeWith (server: FakeBotApiServer) (agent: ScriptedAgent) (options: MafBridgeOptions) : Task<MafBridge> =
    let tools = ToolRegistry.create ()
    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
    Maf.startPollingWith options config agent

[<Tests>]
let mafAckTimingTests =
    testList "MafBridge ack timing & approval expiry" [

        testCaseAsync "a slow (multi-second) resume is still acked within the deferred-ack watchdog's spinner budget"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7401L

                    let agent =
                        ScriptedAgent [
                            PausesFor("req-1", "send_email", [])
                            Delayed(TimeSpan.FromSeconds 3.0, RepliesWith "finally done")
                        ]

                    use! bridge = startBridgeWith server agent MafBridgeOptions.defaults
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    let stopwatch = Diagnostics.Stopwatch.StartNew()
                    deliverTap server 1 "q-slow-approve" approveToken chat 1 chat

                    // The default watchdog budget (~2s, UpdateProcessor's own default) must ack
                    // well before the 3s resume itself completes — proving the ack came from the
                    // watchdog, not from the tool's own (much later) completion.
                    do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
                    stopwatch.Stop()

                    Expect.isLessThan
                        stopwatch.Elapsed.TotalMilliseconds
                        2900.0
                        "the tap was acked by the watchdog, well before the 3s resume itself finished"

                    Expect.isEmpty (server.RequestsFor "editMessageText") "the message is not yet edited — the resume is still running when the ack fires"

                    // The resume DOES eventually complete and edit the message once it finishes.
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)
                    let outcome = (server.RequestsFor "editMessageText").Head.Body |> Option.get |> field "text" |> asString
                    Expect.stringContains outcome "finally done" "the slow resume's own reply eventually reaches the edit"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a tap on an EXPIRED decision keyboard lands in the engine's own stale/unknown path — acked, never resumed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7402L

                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", []); RepliesWith "sent" ]

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            ApprovalExpiry = ValueSome(TimeSpan.FromMilliseconds 50.0) }

                    use! bridge = startBridgeWith server agent options
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    // Let the binding's short expiry lapse before tapping.
                    do! Task.Delay 300

                    deliverTap server 1 "q-expired" approveToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                    Expect.isEmpty (server.RequestsFor "editMessageText") "an expired tap never reaches maf-approve's own handler, so it never edits anything"
                    Expect.equal agent.RunCount 1 "only the initial StartRun turn ran — an expired tap never resumes the agent"
                }
                |> Async.AwaitTask
        }
    ]
