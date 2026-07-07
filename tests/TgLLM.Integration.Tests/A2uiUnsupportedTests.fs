/// Acceptance: an unknown `catalogId`, a component outside `telegram-basic` alongside supported
/// siblings, a surface with no renderable content, and a malformed message are all surfaced through
/// an `IA2uiObserver` — never a crash, never a silently dropped or corrupted render — through the F#
/// façade (`A2ui.rendererWithObserver`).
module TgLLM.Integration.Tests.A2uiUnsupportedTests

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

let private recordingObserver () : IA2uiObserver * ResizeArray<A2uiError> =
    let errors = ResizeArray<A2uiError>()

    { new IA2uiObserver with
        member _.OnA2uiError(error: A2uiError) = errors.Add error
        member _.OnMalformedAction(_descriptor: ActionDescriptor) = () },
    errors

let private buildBot (server: FakeBotApiServer) : Task<TgBot> =
    TgBot.startPolling (
        (TgBotConfig.create "123456789:TEST-fake-token")
            .WithBaseUrl(server.BaseUrl)
            .WithTools(ToolRegistry.create ())
    )

let private unknownCatalogJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "some-rich-web-catalog",
        "components": [ { "id": "root", "component": "Text", "text": "unreachable" } ]
      }
    }
    """

/// A `Button` alongside a `TextField` — outside `telegram-basic` — under a `Text` sibling (a Bot API
/// `sendMessage` requires non-empty text regardless of whether a keyboard is present, so a bare
/// Button+TextField tree with nothing else would never reach the wire at all; that is exactly the
/// "no renderable content" case exercised separately below).
let private buttonAndTextFieldJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b1", "tf1" ] },
          { "id": "t1", "component": "Text", "text": "Pick one:" },
          { "id": "b1", "component": "Button", "text": "Go", "action": { "event": { "name": "go" } } },
          { "id": "tf1", "component": "TextField", "text": "unrenderable" }
        ]
      }
    }
    """

let private onlyUnsupportedJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [ { "id": "root", "component": "TextField", "text": "unrenderable" } ]
      }
    }
    """

let private malformedJson: string = """{ "not": "a2ui" }"""

let private validSurfaceJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [ { "id": "root", "component": "Text", "text": "hello" } ]
      }
    }
    """

[<Tests>]
let a2uiUnsupportedTests =
    testList "A2uiUnsupported" [

        testCaseAsync "createSurface with an unknown catalogId is surfaced and nothing is sent" <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server
                    let observer, errors = recordingObserver ()
                    let renderer = A2ui.rendererWithObserver bot noopSink observer

                    match! renderer.Ingest(UMX.tag<chatId> 6001L, unknownCatalogJson "unknown-catalog-surface") with
                    | Error(UnknownCatalog "some-rich-web-catalog") -> ()
                    | other -> failtestf "expected Error (UnknownCatalog _), got %A" other

                    Expect.equal (List.ofSeq errors) [ UnknownCatalog "some-rich-web-catalog" ] "the unknown catalog reached the observer"
                    Expect.equal (server.RequestsFor "sendMessage") [] "nothing was ever sent for a surface the renderer refused"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "an updateComponents/createSurface with a TextField alongside a Button surfaces the TextField while the Button still renders"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server
                    let observer, errors = recordingObserver ()
                    let renderer = A2ui.rendererWithObserver bot noopSink observer

                    match! renderer.Ingest(UMX.tag<chatId> 6002L, buttonAndTextFieldJson "button-and-textfield") with
                    | Ok() -> ()
                    | Error e -> failtestf "the Button/Text siblings should still render, got Error %A" e

                    Expect.equal
                        (List.ofSeq errors)
                        [ UnsupportedComponent("TextField", "tf1") ]
                        "the TextField is surfaced even though the overall call succeeded"

                    let sendRequests = server.RequestsFor "sendMessage"
                    let request = List.exactlyOne sendRequests
                    let body = request.Body |> Option.get
                    Expect.equal (body |> field "text" |> asString) "Pick one:" "the supported Text sibling still renders"

                    let keyboardRows = (body |> field "reply_markup" |> field "inline_keyboard").AsArray()
                    Expect.equal keyboardRows.Count 1 "the supported Button sibling still renders as one keyboard row"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a surface whose only content is unsupported is surfaced and nothing is sent (no empty-message spam)" <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server
                    let observer, errors = recordingObserver ()
                    let renderer = A2ui.rendererWithObserver bot noopSink observer

                    match! renderer.Ingest(UMX.tag<chatId> 6003L, onlyUnsupportedJson "only-unsupported") with
                    | Error(MalformedMessage _) -> ()
                    | other -> failtestf "expected Error (MalformedMessage _) — no renderable text, got %A" other

                    Expect.contains errors (UnsupportedComponent("TextField", "root")) "the unsupported component itself is surfaced"

                    Expect.isTrue
                        (errors |> Seq.exists (function
                            | MalformedMessage _ -> true
                            | _ -> false))
                        "the resulting 'nothing to send' condition is ALSO surfaced, separately from the per-component report"

                    Expect.equal (server.RequestsFor "sendMessage") [] "no empty/garbage message reaches the wire"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a malformed message is surfaced and a subsequent valid message still processes (the bot keeps working)" <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    use! bot = buildBot server
                    let observer, errors = recordingObserver ()
                    let renderer = A2ui.rendererWithObserver bot noopSink observer

                    match! renderer.Ingest(UMX.tag<chatId> 6004L, malformedJson) with
                    | Error(MalformedMessage _) -> ()
                    | other -> failtestf "expected Error (MalformedMessage _), got %A" other

                    Expect.isTrue
                        (errors |> Seq.exists (function
                            | MalformedMessage _ -> true
                            | _ -> false))
                        "the malformed message reached the observer"

                    match! renderer.Ingest(UMX.tag<chatId> 6004L, validSurfaceJson "recovers-after-malformed") with
                    | Ok() -> ()
                    | Error e -> failtestf "a later, well-formed message should still process, got Error %A" e

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the valid surface that followed still sent its message"
                }
                |> Async.AwaitTask
        }
    ]
