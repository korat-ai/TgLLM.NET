/// Acceptance for `Conversations.GetOrCreate`'s fault-eviction (`Bridge.fs`'s `StartRun`/
/// `HandleIncomingMessage` both route through it): a chat whose FIRST session-creation attempt
/// throws must not be permanently bricked — the classic `Lazy<Task<T>>` pitfall is that the
/// factory delegate itself returns a `Task` successfully (so `Lazy` caches that task reference for
/// good) even though the task it returned later transitions to Faulted, which would otherwise
/// replay the SAME captured exception on every later turn forever. `StartRun` itself never
/// re-throws a turn failure (it reports via `IMafObserver.OnTurnFailed` and swallows — see
/// `Bridge.fs`'s own doc comment on `StartRun`), so this test observes recovery through THAT
/// channel rather than a try/with around the call.
module TgLLM.Integration.Tests.MafConversationRecoveryTests

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

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then
            failtest "timed out waiting for the expected request"
    }

type private RecordingObserver() =
    let turnFailed = ResizeArray<ChatId * exn>()

    member _.TurnFailed: (ChatId * exn) list = List.ofSeq turnFailed

    interface IMafObserver with
        member _.OnStaleDecision(_descriptor) = ()
        member _.OnMalformedDecision(_raw) = ()
        member _.OnResumeFailed(_descriptor, _error) = ()
        member _.OnEmptyTurn(_chat) = ()
        member _.OnInvalidOutput(_chat, _error) = ()
        member _.OnProjectionProblem(_problem) = ()
        member _.OnTurnFailed(chat, error) = turnFailed.Add(chat, error)

[<Tests>]
let mafConversationRecoveryTests =
    testList "MafBridge conversation recovery after a session-creation failure" [

        testCaseAsync "an agent whose session creation throws ONCE then succeeds — the chat recovers on the NEXT turn, not bricked forever"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7601L

                    let agent = ScriptedAgent([ RepliesWith "recovered" ], failCreateSessionCount = 1)
                    let observer = RecordingObserver()

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Observer = ValueSome(observer :> IMafObserver) }

                    let tools = ToolRegistry.create ()
                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
                    use! bridge = Maf.startPollingWith options config agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "hi")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.TurnFailed))

                    Expect.equal observer.TurnFailed.Length 1 "the first turn's session creation failed, as scripted — this proves the repro is real"
                    Expect.isEmpty (server.RequestsFor "sendMessage") "nothing was sent for the failed first turn"

                    // The SECOND turn on the SAME chat must get a FRESH attempt at session
                    // creation, not the SAME cached, permanently-faulted one.
                    do! bridge.StartRun(UMX.tag<chatId> chat, "hi again")
                    do! pollUntil 5000 (fun () -> server.RequestsFor "sendMessage" |> List.isEmpty |> not)

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    Expect.equal (sent |> field "text" |> asString) "recovered" "the SECOND turn recovers and completes normally"
                    Expect.equal observer.TurnFailed.Length 1 "the recovered turn did NOT fail again"
                }
                |> Async.AwaitTask
        }
    ]
