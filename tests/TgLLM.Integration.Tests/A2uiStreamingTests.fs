/// Acceptance for A2UI streaming: a burst of `createSurface`/`updateComponents`/`updateDataModel`
/// for the SAME surface coalesces into one Telegram message, edited in place thereafter, rather
/// than a fresh `sendMessage` per ingested A2UI message — and a surface with no `root` yet buffers
/// (`NoEffect`) until a later update supplies one, through the F# façade (`A2ui.renderer`).
module TgLLM.Integration.Tests.A2uiStreamingTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.A2UI
open TgLLM.Integration.Tests.FakeBotApiServer

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private noopSink: ActionSink = fun _ -> Task.CompletedTask

let private buildBot (server: FakeBotApiServer) : Task<TgBot> =
    TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(ToolRegistry.create ()))

let private createTextSurfaceJson (surfaceId: string) (text: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1" ] },
          { "id": "t1", "component": "Text", "text": "{{text}}" }
        ]
      }
    }
    """

let private updateTextJson (surfaceId: string) (text: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [
          { "id": "t1", "component": "Text", "text": "{{text}}" }
        ]
      }
    }
    """

/// A `createSurface` whose own component list never mentions an id `"root"` — nothing is visible
/// yet, exactly the mid-stream state A2UI's incremental delivery produces before the tree's root
/// arrives.
let private createRootlessSurfaceJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "t1", "component": "Text", "text": "buffered" }
        ]
      }
    }
    """

/// Supplies the missing `root`, referencing the `t1` node the earlier `createSurface` already
/// carried — `SurfaceRegistry.mergeComponents` overlays incoming ids onto the existing map, so
/// `t1` survives from the first message without being resent here.
let private addRootJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1" ] }
        ]
      }
    }
    """

let private createBoundSurfaceJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "dataModel": { "status": "pending" },
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1" ] },
          { "id": "t1", "component": "Text", "text": { "path": "/status" } }
        ]
      }
    }
    """

let private updateStatusJson (surfaceId: string) (status: string) : string =
    $$"""{ "version": "v1.0", "updateDataModel": { "surfaceId": "{{surfaceId}}", "path": "/status", "value": "{{status}}" } }"""

[<Tests>]
let a2uiStreamingTests =
    testList "A2uiStreaming" [

        testCaseAsync
            "createSurface + two updateComponents for the same surface send exactly ONE message, edited twice, reflecting the latest state"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server
                    let renderer = A2ui.renderer bot noopSink
                    let surfaceId = "streaming-coalesce"

                    match! renderer.Ingest(UMX.tag<chatId> 7001L, createTextSurfaceJson surfaceId "v1") with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    match! renderer.Ingest(UMX.tag<chatId> 7001L, updateTextJson surfaceId "v2") with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    match! renderer.Ingest(UMX.tag<chatId> 7001L, updateTextJson surfaceId "v3") with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    let sendRequests = server.RequestsFor "sendMessage"
                    Expect.equal (List.length sendRequests) 1 "the whole burst produces exactly one sendMessage, not three"
                    Expect.equal (sendRequests.Head.Body |> Option.get |> field "text" |> asString) "v1" "the first render carries the FIRST state"

                    let editRequests = server.RequestsFor "editMessageText"
                    Expect.equal (List.length editRequests) 2 "each subsequent updateComponents edits the SAME message in place"

                    let lastEditBody = (List.last editRequests).Body |> Option.get
                    Expect.equal (lastEditBody |> field "text" |> asString) "v3" "the LATEST update's state reaches the wire, not an intermediate one"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "a createSurface with no root buffers (no sendMessage); the message is sent only once a later updateComponents supplies the root"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server
                    let renderer = A2ui.renderer bot noopSink
                    let surfaceId = "streaming-buffered-root"

                    match! renderer.Ingest(UMX.tag<chatId> 7002L, createRootlessSurfaceJson surfaceId) with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    Expect.equal
                        (List.length (server.RequestsFor "sendMessage"))
                        0
                        "no root in the tree yet ⇒ nothing renders ⇒ no message is sent on createSurface"

                    match! renderer.Ingest(UMX.tag<chatId> 7002L, addRootJson surfaceId) with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    let sendRequests = server.RequestsFor "sendMessage"
                    Expect.equal (List.length sendRequests) 1 "the message is sent only once the root arrives, on the SECOND ingest"
                    Expect.equal (sendRequests.Head.Body |> Option.get |> field "text" |> asString) "buffered" "the earlier-buffered Text sibling is part of the first actual render"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an updateDataModel that changes a bound value edits the message text in place" <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server
                    let renderer = A2ui.renderer bot noopSink
                    let surfaceId = "streaming-data-model"
                    let chat = UMX.tag<chatId> 7003L

                    match! renderer.Ingest(chat, createBoundSurfaceJson surfaceId) with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    let sendRequests = server.RequestsFor "sendMessage"
                    Expect.equal (List.length sendRequests) 1 "the surface's first render sends exactly one message"
                    Expect.equal (sendRequests.Head.Body |> Option.get |> field "text" |> asString) "pending" "the bound Text resolves the initial data-model value"

                    match! renderer.Ingest(chat, updateStatusJson surfaceId "approved") with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() -> ()

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "updateDataModel edits the SAME message — no new sendMessage"

                    match server.RequestsFor "editMessageText" with
                    | [ request ] ->
                        let body = request.Body |> Option.get
                        Expect.equal (body |> field "message_id" |> fun n -> n.AsValue().GetValue<int64>()) 1L "the edit targets the originally sent message"
                        Expect.equal (body |> field "text" |> asString) "approved" "the new data-model value reaches the wire in place"
                    | other -> failwithf "expected exactly one editMessageText call, got %d" (List.length other)
                }
                |> Async.AwaitTask
        }
    ]
