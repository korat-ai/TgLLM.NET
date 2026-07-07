/// Tests for the internal `a2ui-action` tool handler (`A2uiActionTool.create`): a tap resolves its
/// `ActionDescriptor`'s context against the surface's CURRENT data model (not the model at render
/// time) and delivers an `A2uiAction` to the sink; a `wantResponse` action carrying no `actionId` is
/// surfaced through `IA2uiActionObserver` instead of reaching the sink.
module TgLLM.A2UI.Tests.ActionTests

open System
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.A2UI

let private chat (id: int64) : ChatId = UMX.tag<chatId> id
let private messageId (id: int64) : MessageId = UMX.tag<messageId> id

let private dummyUser: EndUser =
    { Id = UMX.tag<userId> 900L
      FirstName = "Tester"
      Username = null }

let private label (text: string) : ButtonLabel =
    match ButtonLabel.create text with
    | Ok l -> l
    | Error e -> failwithf "test setup: expected a valid label, got %A" e

let private noopReply: string -> Task<MessageId> = fun _ -> Task.FromResult(messageId 1L)

let private jsonObject (json: string) : JsonNode =
    match JsonNode.Parse json with
    | null -> failwith "test setup: expected valid JSON"
    | node -> node

/// Renders a single `ServerEvent` Button through the real `Renderer.render` so the descriptor JSON
/// this test feeds `A2uiActionTool.create` is byte-for-byte what a genuine tap would carry, not a
/// hand-assembled approximation of it.
let private buttonArgJson (surfaceId: string) (name: string) (context: (string * string) list) (wantResponse: bool) (actionId: string option) : string =
    let button: Component =
        { Id = "ok"
          Node = Button(Literal "Go", ServerEvent(name, context, wantResponse, actionId)) }

    let root: Component = { Id = "root"; Node = Row [ "ok" ] }

    match Renderer.render Catalog.telegramBasic surfaceId (jsonObject "{}") [ root; button ] with
    | Error e -> failwithf "test setup: expected Ok, got %A" e
    | Ok surface ->
        match surface.Keyboard.Rows with
        | [ [ ToolButton(_, _, Some argJson) ] ] -> argJson
        | other -> failwithf "test setup: expected exactly one ToolButton, got %A" other

let private pressContext (arg: string) : PressContext =
    PressContext(label "Go", chat 1L, dummyUser, messageId 42L, CancellationToken.None, noopReply, arg = arg)

/// A live surface (`surfaceId`) tracked by `registry`, whose data model is `dataModelJson` — built
/// via a real `createSurface`, mirroring how `SurfaceRegistry` is populated in production.
let private liveSurface (registry: SurfaceRegistry) (chatId: int64) (surfaceId: string) (dataModelJson: string) : unit =
    let root: RawComponent =
        { Id = "root"
          ComponentType = "Text"
          Fields = JsonObject() }

    registry.Apply(chat chatId, CreateSurface(surfaceId, Catalog.telegramBasic.CatalogId, [ root ], Some(jsonObject dataModelJson)))
    |> ignore

let private recordingSink () : ActionSink * ResizeArray<A2uiAction> =
    let received = ResizeArray<A2uiAction>()
    (fun action -> received.Add action
                   Task.CompletedTask),
    received

let private recordingObserver () : IA2uiActionObserver * ResizeArray<ActionDescriptor> =
    let recorded = ResizeArray<ActionDescriptor>()

    { new IA2uiActionObserver with
        member _.OnMalformedAction(descriptor: ActionDescriptor) = recorded.Add descriptor },
    recorded

let private stringContextValue (context: (string * JsonNode option) list) (key: string) : string option =
    context
    |> List.tryFind (fun (k, _) -> k = key)
    |> Option.bind snd
    |> Option.bind (function
        | :? JsonValue as v -> Some(v.GetValue<string>())
        | _ -> None)

[<Tests>]
let actionTests =
    testList "A2uiActionTool" [

        testCaseAsync
            "tapping a ServerEvent Button resolves context against the surface's CURRENT data model and delivers an A2uiAction to the sink"
        <| async {
            do!
                task {
                    // Rendered against a throwaway empty data model — context resolution must NOT
                    // depend on whatever the surface looked like at render time.
                    let argJson = buttonArgJson "deploy-1" "approve" [ "env", "/env" ] true (Some "a1")

                    let registry = SurfaceRegistry(Catalog.telegramBasic)
                    liveSurface registry 1L "deploy-1" """{ "env": "prod" }"""

                    let sink, received = recordingSink ()
                    let observer, malformed = recordingObserver ()
                    let fixedNow = DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> fixedNow

                    let tool = A2uiActionTool.create registry sink clock observer
                    do! tool (pressContext argJson)

                    Expect.equal malformed.Count 0 "a well-formed action is never reported as malformed"
                    Expect.equal received.Count 1 "the sink received exactly one action"
                    let action = received[0]
                    Expect.equal action.Name "approve" "action name"
                    Expect.equal action.SurfaceId "deploy-1" "surface id"
                    Expect.equal action.SourceComponentId "ok" "the pressed component's own id"
                    Expect.equal action.Timestamp fixedNow "timestamp comes from the injected clock, not ambient time"
                    Expect.equal action.WantResponse true "wantResponse carried through"
                    Expect.equal action.ActionId (Some "a1") "actionId carried through"

                    Expect.equal
                        (stringContextValue action.Context "env")
                        (Some "prod")
                        "context resolves against the surface's CURRENT data model, not a render-time snapshot"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "resolving context against a surface no longer tracked is a silent no-op (nothing left to resolve against)"
        <| async {
            do!
                task {
                    let argJson = buttonArgJson "gone-surface" "approve" [] false None
                    let registry = SurfaceRegistry(Catalog.telegramBasic) // never populated
                    let sink, received = recordingSink ()
                    let observer, malformed = recordingObserver ()

                    let tool = A2uiActionTool.create registry sink (fun () -> DateTimeOffset.UnixEpoch) observer
                    do! tool (pressContext argJson)

                    Expect.equal received.Count 0 "no live surface means no data model to resolve against"
                    Expect.equal malformed.Count 0 "an untracked surface is not itself a malformed action"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a wantResponse action with no actionId is surfaced as malformed and never reaches the sink"
        <| async {
            do!
                task {
                    let argJson = buttonArgJson "deploy-1" "confirm" [] true None

                    let registry = SurfaceRegistry(Catalog.telegramBasic)
                    liveSurface registry 2L "deploy-1" "{}"

                    let sink, received = recordingSink ()
                    let observer, malformed = recordingObserver ()

                    let tool = A2uiActionTool.create registry sink (fun () -> DateTimeOffset.UnixEpoch) observer
                    do! tool (pressContext argJson)

                    Expect.equal received.Count 0 "a malformed action never reaches the sink"
                    Expect.equal malformed.Count 1 "the malformed condition was surfaced exactly once"
                    Expect.equal malformed[0].Name "confirm" "the surfaced descriptor is the one that was tapped"
                    Expect.equal malformed[0].SurfaceId "deploy-1" "the surfaced descriptor carries its surface id"
                    Expect.equal malformed[0].WantResponse true "wantResponse carried through to the surfaced descriptor"
                    Expect.equal malformed[0].ActionId None "the missing actionId is what made this malformed"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "wantResponse=false with no actionId is well-formed (no response was ever requested)"
        <| async {
            do!
                task {
                    let argJson = buttonArgJson "deploy-1" "dismiss" [] false None

                    let registry = SurfaceRegistry(Catalog.telegramBasic)
                    liveSurface registry 3L "deploy-1" "{}"

                    let sink, received = recordingSink ()
                    let observer, malformed = recordingObserver ()

                    let tool = A2uiActionTool.create registry sink (fun () -> DateTimeOffset.UnixEpoch) observer
                    do! tool (pressContext argJson)

                    Expect.equal malformed.Count 0 "no response was requested, so a missing actionId is not malformed"
                    Expect.equal received.Count 1 "the well-formed action still reaches the sink"
                }
                |> Async.AwaitTask
        }
    ]
