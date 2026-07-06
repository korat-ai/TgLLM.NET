/// Regression tests: `TgBot.SendKeyboardPlan` must fail fast when a plan has a tool button but no
/// Tool Router was ever wired in
/// (`TgBotConfig.WithTools`) — otherwise the button reaches the wire, gets tapped, and silently
/// no-ops forever (no `ToolDispatch` exists to ever resolve its binding; `wireBot` only builds one
/// when `common.Tools` is `Some`).
module TgLLM.Integration.Tests.SendKeyboardPlanGuardTests

open System
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Integration.Tests.FakeBotApiServer

let private config (server: FakeBotApiServer) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl)

[<Tests>]
let sendKeyboardPlanGuardTests =
    testList
        "TgBot.SendKeyboardPlan guards against a dead keyboard"
        [

          testCaseAsync "sending a plan with a tool button, with NO Tool Router wired in, fails fast"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      // No `.WithTools` call anywhere — this bot has no Tool Router.
                      use! bot = TgBot.startPolling (config server)

                      match Plan.rows [ [ Plan.tool "Approve" "approve" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          // The fail-fast check runs SYNCHRONOUSLY, before any Task is even
                          // returned (same convention as `PressContext.Answer`'s `invalidOp`
                          // elsewhere in this codebase) — so it must be caught with a plain
                          // try/with around the call itself, not via the awaited Task.
                          let mutable caught: exn option = None

                          try
                              bot.SendKeyboardPlan(UMX.tag<chatId> 1001L, MessageText.unsafe "Deploy?", plan) |> ignore
                          with ex ->
                              caught <- Some ex

                          match caught with
                          | Some(:? InvalidOperationException as ex) ->
                              Expect.stringContains ex.Message "WithTools" "the exception explains the missing Tool Router wiring"
                          | Some other -> failtestf "expected InvalidOperationException, got %A" other
                          | None -> failtest "expected SendKeyboardPlan to fail fast, but it did not throw"

                          Expect.isEmpty
                              (server.RequestsFor "sendMessage")
                              "the doomed keyboard never reached the wire — fail-fast happens BEFORE the send"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "sending a URL-only plan, with NO Tool Router wired in, still succeeds"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      use! bot = TgBot.startPolling (config server)

                      match Plan.rows [ [ Plan.url "Docs" "https://example.test/docs" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 1002L, MessageText.unsafe "Docs?", plan)

                          Expect.isNonEmpty
                              (server.RequestsFor "sendMessage")
                              "a URL-only plan never needs a Tool Router, so it is sent normally"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "sending a plan with a tool button, WITH a Tool Router wired in, succeeds (no false positive)"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let tools = ToolRegistry.create().Register("approve", (fun _ -> System.Threading.Tasks.Task.FromResult(())))
                      use! bot = TgBot.startPolling ((config server).WithTools tools)

                      match Plan.rows [ [ Plan.tool "Approve" "approve" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 1003L, MessageText.unsafe "Deploy?", plan)

                          Expect.isNonEmpty
                              (server.RequestsFor "sendMessage")
                              "a tool-button plan sends normally once a Tool Router is wired in"
                  }
                  |> Async.AwaitTask
          } ]
