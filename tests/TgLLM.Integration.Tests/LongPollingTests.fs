/// Tests for `LongPollingUpdateSource` against the fake Bot API server. Verifies it deletes
/// any webhook before polling, yields mapped `AgentEvent`s, and advances the offset by
/// `update_id + 1` (confirm-by-offset) on the next poll.
module TgLLM.Integration.Tests.LongPollingTests

open System
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open Telegram.Bot
open TgLLM.Core
open TgLLM.BotApi
open TgLLM.Integration.Tests.FakeBotApiServer

let private makeClient (server: FakeBotApiServer) : ITelegramBotClient =
    TelegramBotClient(TelegramBotClientOptions("123456789:TEST-fake-token", server.BaseUrl)) :> ITelegramBotClient

/// Was any `getUpdates` request issued with this exact `offset` in its body?
let private polledWithOffset (server: FakeBotApiServer) (offset: int) : bool =
    server.RequestsFor "getUpdates"
    |> List.exists (fun r ->
        r.Body
        |> Option.bind (fun b -> Option.ofObj b.["offset"])
        |> Option.map (fun n -> n.GetValue<int>() = offset)
        |> Option.defaultValue false)

[<Tests>]
let longPollingTests =
    testList "LongPollingUpdateSource" [

        testCase "invalid timeout and backoff settings fail at construction" <| fun _ ->
            let client = Unchecked.defaultof<ITelegramBotClient>

            Expect.throwsT<ArgumentException>
                (fun () -> LongPollingUpdateSource(client, timeoutSeconds = -1) |> ignore)
                "a negative Telegram poll timeout is invalid"

            Expect.throwsT<ArgumentException>
                (fun () ->
                    LongPollingUpdateSource(
                        client,
                        initialBackoff = TimeSpan.FromSeconds 2.0,
                        maxBackoff = TimeSpan.FromSeconds 1.0
                    )
                    |> ignore)
                "maximum retry delay cannot be smaller than the initial delay"

        testCaseAsync "deletes webhook first, yields a mapped press, and advances offset"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()

                    let token = CallbackToken.generate ()

                    let update =
                        TelegramJson.callbackQueryUpdate 42 "q1" (CallbackToken.value token) 100L 7 500L "Alice"

                    server.EnqueueResult("getUpdates", TelegramJson.batch [ update ])

                    let source =
                        LongPollingUpdateSource(makeClient server, timeoutSeconds = 0) :> IUpdateSource

                    use cts = new CancellationTokenSource()
                    let enumerator = source.Updates(cts.Token).GetAsyncEnumerator(cts.Token)

                    let! hasFirst = enumerator.MoveNextAsync()
                    let first = if hasFirst then ValueSome enumerator.Current else ValueNone

                    // Bounded wait for the post-batch poll that must carry the advanced offset (43).
                    let mutable tries = 0

                    while not (polledWithOffset server 43) && tries < 100 do
                        do! Task.Delay 10
                        tries <- tries + 1

                    cts.Cancel()
                    do! enumerator.DisposeAsync()

                    Expect.isNonEmpty (server.RequestsFor "deleteWebhook") "webhook deleted before polling"

                    match first with
                    | ValueSome (ButtonPressed press) ->
                        Expect.equal press.Token token "yields the pressed button's token"
                        Expect.equal (UMX.untag press.Chat) 100L "carries the chat id"
                        Expect.equal (UMX.untag press.User.Id) 500L "carries the user id"
                    | _ -> failtest "expected exactly one ButtonPressed event"

                    Expect.isTrue (polledWithOffset server 43) "next poll used offset = update_id + 1"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "deleteWebhook is retried before the source reports ready"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    server.EnqueueError("deleteWebhook", 500, "Internal Server Error")

                    let source =
                        LongPollingUpdateSource(
                            makeClient server,
                            timeoutSeconds = 0,
                            initialBackoff = TimeSpan.FromMilliseconds 10.0,
                            maxBackoff = TimeSpan.FromMilliseconds 20.0
                        )

                    use cts = new CancellationTokenSource()
                    let enumerator = (source :> IUpdateSource).Updates(cts.Token).GetAsyncEnumerator(cts.Token)
                    do! source.Ready.WaitAsync(TimeSpan.FromSeconds 1.0)

                    Expect.equal (List.length (server.RequestsFor "deleteWebhook")) 2 "startup retried the transient delete failure"

                    cts.Cancel()
                    do! enumerator.DisposeAsync()
                }
                |> Async.AwaitTask
        }

        testCaseAsync "three deleteWebhook failures fault readiness and never start getUpdates"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()

                    for _ in 1..3 do
                        server.EnqueueError("deleteWebhook", 500, "Internal Server Error")

                    let source =
                        LongPollingUpdateSource(
                            makeClient server,
                            timeoutSeconds = 0,
                            initialBackoff = TimeSpan.FromMilliseconds 10.0,
                            maxBackoff = TimeSpan.FromMilliseconds 20.0
                        )

                    use cts = new CancellationTokenSource()
                    let enumerator = (source :> IUpdateSource).Updates(cts.Token).GetAsyncEnumerator(cts.Token)

                    let! failed =
                        task {
                            try
                                do! source.Ready.WaitAsync(TimeSpan.FromSeconds 1.0)
                                return false
                            with _ ->
                                return true
                        }

                    Expect.isTrue failed "readiness faults when webhook deletion cannot be established"
                    Expect.isEmpty (server.RequestsFor "getUpdates") "polling never starts while a webhook may still be active"
                    do! enumerator.DisposeAsync()
                }
                |> Async.AwaitTask
        }
    ]
