/// Acceptance: a `createSurface`+`updateComponents` (Text bound via the data model + two Buttons
/// in a Row, and separately a Column of Buttons) sends exactly one Telegram message whose text is
/// the resolved MarkdownV2 body and whose inline keyboard layout matches the component tree — over
/// both long polling and webhooks, through the F# façade (`A2ui.renderer`).
module TgLLM.Integration.Tests.A2uiRenderTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.A2UI
open TgLLM.AspNetCore
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

let private noopSink: ActionSink = fun _ -> Task.CompletedTask

let private rowButtonsSurfaceJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "dataModel": { "title": "Deploy v2?" },
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "row1" ] },
          { "id": "t1", "component": "Text", "text": { "path": "/title" } },
          { "id": "row1", "component": "Row", "children": [ "b1", "b2" ] },
          { "id": "b1", "component": "Button", "text": "Approve", "action": { "event": { "name": "approve" } } },
          { "id": "b2", "component": "Button", "text": "Reject", "action": { "event": { "name": "reject" } } }
        ]
      }
    }
    """

/// A Text sibling alongside the buttons — a Bot API `sendMessage` requires non-empty text, so a
/// keyboard-only surface (no Text/Divider/Image anywhere in its tree) isn't a Telegram-representable
/// message at all; that edge case belongs to the catalog/unsupported-surfacing story, not this one.
let private columnButtonsSurfaceJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b1", "b2", "b3" ] },
          { "id": "t1", "component": "Text", "text": "Choose one:" },
          { "id": "b1", "component": "Button", "text": "One", "action": { "event": { "name": "one" } } },
          { "id": "b2", "component": "Button", "text": "Two", "action": { "event": { "name": "two" } } },
          { "id": "b3", "component": "Button", "text": "Three", "action": { "event": { "name": "three" } } }
        ]
      }
    }
    """

let private buildBot (server: FakeBotApiServer) (tools: ToolRegistry) : Task<TgBot> =
    TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools)

/// Runs the "Text + two Buttons in a Row" render scenario over LONG POLLING and returns the
/// captured `sendMessage` body.
let private runRowScenarioOverPolling () : Task<JsonNode> =
    task {
        use! server = FakeBotApiServer.start ()
        use! bot = buildBot server (ToolRegistry.create ())
        let renderer = A2ui.renderer bot noopSink

        match! renderer.Ingest(UMX.tag<chatId> 4001L, rowButtonsSurfaceJson "row-surface-polling") with
        | Error e -> return failtestf "expected Ok, got %A" e
        | Ok() -> return (server.RequestsFor "sendMessage").Head.Body |> Option.get
    }

/// Hosts the webhook-receiving endpoint on a loopback port (same pattern as
/// `ToolRouterBothTransportsTests.fs`) — meaningful here only insofar as `A2ui.renderer` must work
/// identically over a bot built with `startWebhook`, not because `Ingest` itself touches the
/// webhook ingress (a surface's initial render has no incoming Telegram update to route).
let private startWebhookHost (source: TgLLM.Webhooks.WebhookUpdateSource) (secret: string) : Task<WebApplication> =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        app.MapTelegramWebhook("/telegram/webhook", source, secret) |> ignore
        do! app.StartAsync()
        return app
    }

/// Runs the SAME "Text + two Buttons in a Row" scenario over WEBHOOKS and returns the captured
/// `sendMessage` body.
let private runRowScenarioOverWebhook () : Task<JsonNode> =
    task {
        use! server = FakeBotApiServer.start ()
        let secret = "s3cret-a2ui-render"

        let config =
            TgWebhookConfig
                .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
                .WithBaseUrl(server.BaseUrl)
                .WithTools(ToolRegistry.create ())

        use! bot = TgBot.startWebhook config
        use! _host = startWebhookHost bot.WebhookSource secret
        let renderer = A2ui.renderer bot noopSink

        match! renderer.Ingest(UMX.tag<chatId> 4002L, rowButtonsSurfaceJson "row-surface-webhook") with
        | Error e -> return failtestf "expected Ok, got %A" e
        | Ok() -> return (server.RequestsFor "sendMessage").Head.Body |> Option.get
    }

let private assertRowScenario (body: JsonNode) : unit =
    Expect.equal (body |> field "text" |> asString) "Deploy v2?" "the bound Text resolves as the message body"
    Expect.equal (body |> field "parse_mode" |> asString) "MarkdownV2" "the A2UI send path requests MarkdownV2"

    let keyboardRows = (body |> field "reply_markup" |> field "inline_keyboard").AsArray()
    Expect.equal keyboardRows.Count 1 "both buttons land in exactly one keyboard row"

    let row0 = keyboardRows |> Seq.head
    let row0Buttons = row0 |> Option.ofObj |> Option.get |> fun n -> n.AsArray()
    Expect.equal row0Buttons.Count 2 "the row carries both buttons"
    Expect.equal (row0 |> Option.ofObj |> Option.get |> at 0 |> field "text" |> asString) "Approve" "first button label"
    Expect.equal (row0 |> Option.ofObj |> Option.get |> at 1 |> field "text" |> asString) "Reject" "second button label"

[<Tests>]
let a2uiRenderTests =
    testList "A2uiRender" [

        testCaseAsync
            "createSurface+updateComponents (Text + two Buttons in a Row) sends one message with the resolved body and one keyboard row, over both transports"
        <| async {
            do!
                task {
                    let! polledBody = runRowScenarioOverPolling ()
                    let! webhookBody = runRowScenarioOverWebhook ()
                    assertRowScenario polledBody
                    assertRowScenario webhookBody
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a Column of Buttons sends stacked keyboard rows, one button each, in order" <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server (ToolRegistry.create ())
                    let renderer = A2ui.renderer bot noopSink

                    match! renderer.Ingest(UMX.tag<chatId> 4003L, columnButtonsSurfaceJson "col-surface") with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    let sendRequests = server.RequestsFor "sendMessage"
                    Expect.equal (List.length sendRequests) 1 "exactly one message is sent for the whole surface"

                    let body = sendRequests.Head.Body |> Option.get
                    Expect.equal (body |> field "text" |> asString) "Choose one:" "the Text sibling still renders as the body"

                    let keyboardRows = (body |> field "reply_markup" |> field "inline_keyboard").AsArray()
                    Expect.equal keyboardRows.Count 3 "three stacked rows, one per button"

                    for i, label in List.indexed [ "One"; "Two"; "Three" ] do
                        let row = keyboardRows[i] |> Option.ofObj |> Option.get
                        Expect.equal (row.AsArray().Count) 1 "one button per stacked row"
                        Expect.equal (row |> at 0 |> field "text" |> asString) label "row order matches the Column's child order"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "attaching an A2UI renderer to a bot with no Tool Router wired in fails fast" <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl))

                    Expect.throws
                        (fun () -> A2ui.renderer bot noopSink |> ignore)
                        "no ToolRegistry means a ServerEvent Button's tap could never resolve; A2ui.renderer must refuse to attach rather than build a renderer whose buttons silently no-op"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "attaching a SECOND A2UI renderer to the SAME bot fails fast rather than silently orphaning the first renderer's surfaces" <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server (ToolRegistry.create ())

                    A2ui.renderer bot noopSink |> ignore

                    Expect.throws
                        (fun () -> A2ui.renderer bot noopSink |> ignore)
                        "a second renderer over the SAME bot would re-register a2ui-action (add-or-replace), silently orphaning \
                         the first renderer's own SurfaceRegistry — every tap for a surface the first renderer already sent \
                         would route through the SECOND renderer's registry instead, which has never heard of that surface"
                }
                |> Async.AwaitTask
        }
    ]
