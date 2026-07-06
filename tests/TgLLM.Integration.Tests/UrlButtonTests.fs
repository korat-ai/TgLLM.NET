/// T029: US4 (URL buttons alongside tool buttons) exercised end-to-end. A plan mixing a URL button
/// and a tool button sends a keyboard whose URL button carries the url (no `callback_data`/
/// token/binding — research.md D3: a URL tap is handled entirely client-side, so there is nothing
/// for the library to route) while the tool button still routes on tap exactly like
/// `ToolRouterAcceptanceTests.fs`'s US1 scenario. Mirrors that file's structure.
module TgLLM.Integration.Tests.UrlButtonTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Integration.Tests.FakeBotApiServer

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private buttonAt (row: int) (col: int) (sendBody: JsonNode) : JsonNode =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col

let private hasField (key: string) (node: JsonNode) : bool = node.[key] |> Option.ofObj |> Option.isSome

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the tool to run"
        do! t
    }

let private config (server: FakeBotApiServer) (tools: ToolRegistry) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools

[<Tests>]
let urlButtonTests =
    testList
        "UrlButton"
        [

          testCaseAsync "a plan mixing a URL button and a tool button sends a wire-correct URL button; the tool button still routes"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let approveRan = TaskCompletionSource<string>()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register(
                                  "approve",
                                  (fun ctx -> task { approveRan.TrySetResult(ctx.Arg |> Option.ofObj |> Option.defaultValue "<no-arg>") |> ignore })
                              )

                      use! bot = TgBot.startPolling (config server tools)

                      let plan =
                          Plan.rows [ [ Plan.tool "Approve" "approve"; Plan.url "Docs" "https://example.test/docs" ] ]

                      match plan with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok p ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 801L, MessageText.unsafe "Deploy?", p)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get

                          // The URL button: carries `url`, NO `callback_data` (no token, no binding —
                          // there is nothing to route; research.md D3).
                          let urlButton = sentKeyboard |> buttonAt 0 1
                          Expect.equal (urlButton |> field "text" |> asString) "Docs" "the URL button's label reached the wire"
                          Expect.equal (urlButton |> field "url" |> asString) "https://example.test/docs" "the URL button's url reached the wire"
                          Expect.isFalse (urlButton |> hasField "callback_data") "a URL button carries no callback_data (no token/binding)"

                          // The tool button still routes on tap, exactly like the plain US1 scenario.
                          let approveButton = sentKeyboard |> buttonAt 0 0
                          Expect.isTrue (approveButton |> hasField "callback_data") "the tool button DOES carry callback_data (its token)"
                          let approveToken = approveButton |> field "callback_data" |> asString

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-approve" approveToken 801L 60 960L "Uri" ]
                          )

                          do! awaitOrTimeout 5000 (approveRan.Task :> Task)

                          Expect.equal approveRan.Task.Result "<no-arg>" "the tool button's press still routed to its bound tool"
                  }
                  |> Async.AwaitTask
          } ]
