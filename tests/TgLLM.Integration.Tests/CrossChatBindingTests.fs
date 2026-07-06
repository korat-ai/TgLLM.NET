/// Regression test for review finding #2 (003-tool-router-extensions): Telegram `message_id` is
/// unique only PER CHAT, so `MessageBindingTracker` keying by bare `MessageId` let a tool's
/// `ctx.EditKeyboardAsync` in one chat delete ANOTHER chat's live bindings whenever both chats'
/// keyboard messages happened to share the same per-chat message_id (e.g. each chat's first-ever
/// sent message — now representable end-to-end since `FakeBotApiServer` assigns `message_id`
/// per-chat, not globally). Exercises the REAL production wiring (one `TgBot` instance serving two
/// chats, exactly like a real deployment) rather than `MessageBindingTracker` in isolation.
module TgLLM.Integration.Tests.CrossChatBindingTests

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
let private asInt64 (node: JsonNode) : int64 = node.AsValue().GetValue<int64>()

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody
    |> field "reply_markup"
    |> field "inline_keyboard"
    |> at row
    |> at col
    |> field "callback_data"
    |> asString

/// The `sendMessage` request targeting `chat`, oldest first — needed because this test, unlike
/// `EditInPlaceTests.fs`, sends keyboards to TWO chats from the SAME server/bot, so `.Head` alone
/// would be ambiguous.
let private sentKeyboardFor (chat: int64) (server: FakeBotApiServer) : JsonNode =
    server.RequestsFor "sendMessage"
    |> List.tryFind (fun r -> r.Body |> Option.map (fun b -> (b |> field "chat_id" |> asInt64) = chat) |> Option.defaultValue false)
    |> Option.map (fun r -> r.Body |> Option.get)
    |> Option.defaultWith (fun () -> failwithf "no sendMessage request found for chat %d" chat)

let private ackTextsFor (server: FakeBotApiServer) : string list =
    server.RequestsFor "answerCallbackQuery"
    |> List.choose (fun r -> r.Body |> Option.bind (fun b -> b.["text"] |> Option.ofObj) |> Option.map asString)

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then failtest "timed out waiting for the expected request"
    }

let private config (server: FakeBotApiServer) (tools: ToolRegistry) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools

[<Tests>]
let crossChatBindingTests =
    testList
        "MessageBindingTracker cross-chat isolation (review finding #2)"
        [

          testCaseAsync
              "editing chat A's keyboard does not remove chat B's live binding, even though both share the same per-chat message_id"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let chatA = 701L
                      let chatB = 702L

                      let tools =
                          ToolRegistry
                              .create()
                              .Register(
                                  "editor",
                                  fun ctx ->
                                      task {
                                          // A paginator/counter-shaped tool: re-renders its OWN keyboard,
                                          // which removes ITS chat's stale binding via the tracker.
                                          match Plan.rows [ [ Plan.tool "Next" "editor" ] ] with
                                          | Error e -> failtestf "replacement plan should be valid: %A" e
                                          | Ok replacement -> do! ctx.EditKeyboardAsync replacement
                                      }
                              )
                              .Register("watcher", (fun ctx -> task { ctx.Answer("chatB-tool-ran", alert = false) }))

                      use! bot = TgBot.startPolling (config server tools)

                      match Plan.rows [ [ Plan.tool "Page 1" "editor" ] ] with
                      | Error e -> failtestf "plan A should be valid: %A" e
                      | Ok planA ->
                          let! messageIdA = bot.SendKeyboardPlan(UMX.tag<chatId> chatA, MessageText.unsafe "A", planA)

                          match Plan.rows [ [ Plan.tool "Watch" "watcher" ] ] with
                          | Error e -> failtestf "plan B should be valid: %A" e
                          | Ok planB ->
                              let! messageIdB = bot.SendKeyboardPlan(UMX.tag<chatId> chatB, MessageText.unsafe "B", planB)

                              let sentA = sentKeyboardFor chatA server
                              let sentB = sentKeyboardFor chatB server

                              // Sanity: both chats' FIRST sent message lands on the SAME per-chat
                              // message_id — exactly the collision shape a bare-`MessageId` key conflates.
                              Expect.equal (UMX.untag messageIdA) 1L "chat A's keyboard is its first message"
                              Expect.equal (UMX.untag messageIdB) 1L "chat B's keyboard is ALSO its first message — same message_id as A"

                              let editorToken = callbackDataAt 0 0 sentA
                              let watcherToken = callbackDataAt 0 0 sentB

                              // Press chat A's button: "editor" re-plans its OWN keyboard via
                              // EditKeyboardAsync, which removes chat A's stale binding from the store.
                              server.EnqueueResult(
                                  "getUpdates",
                                  TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-a" editorToken chatA 1 910L "Ann" ]
                              )

                              do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageReplyMarkup" |> List.isEmpty |> not)

                              // Now press chat B's ORIGINAL button. Under the bug (tracker keyed by bare
                              // MessageId), chat A's edit above would already have deleted chat B's
                              // binding for message_id 1, and this press would silently fall back to the
                              // ack-first unknown-token path (a plain, textless ack) instead of running
                              // "watcher".
                              server.EnqueueResult(
                                  "getUpdates",
                                  TelegramJson.batch [ TelegramJson.callbackQueryUpdate 2 "q-b" watcherToken chatB 1 920L "Bob" ]
                              )

                              do! pollUntil 5000 (fun () -> ackTextsFor server |> List.contains "chatB-tool-ran")

                              Expect.contains
                                  (ackTextsFor server)
                                  "chatB-tool-ran"
                                  "chat B's binding is UNTOUCHED and still resolves after chat A's edit — no cross-chat leak"
                  }
                  |> Async.AwaitTask
          } ]
