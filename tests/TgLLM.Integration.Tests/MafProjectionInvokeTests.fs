/// End-to-end acceptance for `MafTools.project`: a projected `AIFunction`'s registered handler is
/// actually reachable through a real tap — `AIFunctionArguments` built from the button's structured
/// arg, `AIFunction.InvokeAsync`, and the JSON-serialized result sent back as the reply — over
/// `FakeBotApiServer`, no `MafBridge`/agent involved at all (the tool projection is standalone).
module TgLLM.Integration.Tests.MafProjectionInvokeTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open Microsoft.Extensions.AI
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer

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

[<Tests>]
let mafProjectionInvokeTests =
    testList "MafTools.project — invokability" [

        testCaseAsync "a projected AIFunction's registered handler parses the tapped arg, invokes, and replies the JSON result"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 8001L

                    let add = System.Func<int, int, int>(fun a b -> a + b)
                    let addFn: AIFunction = AIFunctionFactory.Create(add, "add", "Adds two numbers", null)

                    let tools = ToolRegistry.create ()
                    let report = MafTools.project tools [ addFn ]
                    Expect.equal report.Registered [ "add" ] "the function registered under its own name"
                    Expect.isEmpty report.Problems "a well-formed declaration surfaces no problems"

                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
                    use! bot = TgBot.startPolling config

                    match Plan.rows [ [ Plan.toolWithArg "Add" "add" """{"a":3,"b":4}""" ] ] with
                    | Error e -> failtestf "plan should be valid: %A" e
                    | Ok plan ->
                        let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> chat, MessageText.unsafe "Run the tool?", plan)
                        let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                        let token = callbackDataAt 0 0 sent

                        do! deliverTap server 1 "q-add-1" token chat 1 chat
                        do! pollUntil 5000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                        let reply = (server.RequestsFor "sendMessage") |> List.item 1
                        let replyText = reply.Body |> Option.get |> field "text" |> asString
                        Expect.equal replyText "7" "the handler invoked the function and replied its JSON-serialized result"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a projected function with no arguments still invokes with an empty argument set on a null arg"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 8002L

                    let greet = System.Func<string>(fun () -> "hello")
                    let greetFn: AIFunction = AIFunctionFactory.Create(greet, "greet", "Greets", null)

                    let tools = ToolRegistry.create ()
                    MafTools.project tools [ greetFn ] |> ignore

                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
                    use! bot = TgBot.startPolling config

                    match Plan.rows [ [ Plan.tool "Greet" "greet" ] ] with
                    | Error e -> failtestf "plan should be valid: %A" e
                    | Ok plan ->
                        let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> chat, MessageText.unsafe "Run the tool?", plan)
                        let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                        let token = callbackDataAt 0 0 sent

                        do! deliverTap server 1 "q-greet-1" token chat 1 chat
                        do! pollUntil 5000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                        let reply = (server.RequestsFor "sendMessage") |> List.item 1
                        let replyText = reply.Body |> Option.get |> field "text" |> asString
                        Expect.stringContains replyText "hello" "a no-arg function still invokes and replies its result"
                }
                |> Async.AwaitTask
        }
    ]
