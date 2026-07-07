/// Example: an agent that already speaks A2UI (for a web/mobile renderer) drives a Telegram bot
/// with no Telegram-specific code of its own. Wires a durable LiteDB binding store (so a tap
/// survives a restart — reused from the Tool Router) and an A2UI renderer over it, then walks the
/// loop the renderer supports end to end: send a surface as one message + inline keyboard, edit
/// that SAME message in place on the agent's next reply, delete it, show a client-side button that
/// needs no server round trip, and surface a component outside `telegram-basic` through an observer
/// instead of silently dropping or misrendering it.
/// Runs over LONG POLLING by default, or WEBHOOKS when `TRANSPORT=webhook` — identical either way.
/// Set `BOT_TOKEN` and `CHAT_ID` (a private chat); for webhooks also `PUBLIC_URL` and
/// `WEBHOOK_SECRET`; then `dotnet run`.
module A2UIFSharp.Program

open System
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open Microsoft.AspNetCore.Builder
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.AspNetCore
open TgLLM.A2UI
open TgLLM.Persistence.LiteDb

let private requireEnv (name: string) : string =
    match Environment.GetEnvironmentVariable name |> Option.ofObj with
    | Some value -> value
    | None -> failwith $"environment variable {name} is required"

/// The initial surface: a Markdown `Text` bound to the live data model, and two server-bound
/// Buttons in a `Row`. A tap on either flows back through `sink` below as an `A2uiAction`, resolved
/// against this SAME data model at tap time (not render time).
let private createDeploySurfaceJson: string =
    """
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "deploy-1",
        "catalogId": "telegram-basic",
        "dataModel": { "title": "Deploy **v2** to prod?", "env": "prod" },
        "components": [
          { "id": "root", "component": "Column", "children": [ "title", "actions" ] },
          { "id": "title", "component": "Text", "text": { "path": "/title" } },
          { "id": "actions", "component": "Row", "children": [ "approve", "reject" ] },
          { "id": "approve", "component": "Button", "text": "Approve",
            "action": { "event": { "name": "approve", "context": { "env": { "path": "/env" } }, "wantResponse": true, "actionId": "a1" } } },
          { "id": "reject", "component": "Button", "text": "Reject",
            "action": { "event": { "name": "reject", "context": {}, "wantResponse": false } } }
        ]
      }
    }
    """

/// What the agent sends back once a tap arrives: same surface id, new text, no more buttons — this
/// EDITS the same Telegram message in place rather than sending a new one.
let private updateDeploySurfaceJson: string =
    """
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "deploy-1",
        "components": [
          { "id": "title", "component": "Text", "text": "Deploying…" },
          { "id": "root", "component": "Column", "children": [ "title" ] }
        ]
      }
    }
    """

let private deleteDeploySurfaceJson: string =
    """{ "version": "v1.0", "deleteSurface": { "surfaceId": "deploy-1" } }"""

/// A client-side Button: tapping "Open docs" opens the URL directly on the device, with no
/// callback and no round trip through the bot at all.
let private createDocsSurfaceJson: string =
    """
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "docs-1",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "link" ] },
          { "id": "t1", "component": "Text", "text": "Full release notes are one tap away." },
          { "id": "link", "component": "Button", "text": "Open docs",
            "action": { "functionCall": { "call": "openUrl", "args": { "url": "https://example.test/docs" } } } }
        ]
      }
    }
    """

let private deleteDocsSurfaceJson: string =
    """{ "version": "v1.0", "deleteSurface": { "surfaceId": "docs-1" } }"""

/// A `Slider` isn't in `telegram-basic` — it is surfaced to the observer below rather than dropped
/// or rendered wrong. Its `Text` sibling still renders on its own (this surface carries no keyboard
/// at all, since its only Button-shaped content is the unsupported one).
let private createRatingSurfaceJson: string =
    """
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "rating-1",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "prompt", "picker" ] },
          { "id": "prompt", "component": "Text", "text": "Rate this build:" },
          { "id": "picker", "component": "Slider", "min": 1, "max": 5 }
        ]
      }
    }
    """

let private deleteRatingSurfaceJson: string =
    """{ "version": "v1.0", "deleteSurface": { "surfaceId": "rating-1" } }"""

let private describeContextValue (value: JsonNode option) : string =
    match value with
    | Some node -> node.ToJsonString()
    | None -> "(unresolved)"

/// Where an outbound `A2uiAction` goes — the host relays it to its agent over whatever transport it
/// uses (`myAgent.SendA2ui(action)` in the quickstart). This example has no agent transport in
/// scope, so it just prints what a tap produced.
let private sink: ActionSink =
    fun action ->
        let context =
            action.Context
            |> List.map (fun (key, value) -> $"{key}={describeContextValue value}")
            |> String.concat ", "

        printfn $"[sink] '{action.Name}' from surface '{action.SurfaceId}' component '{action.SourceComponentId}'"
        printfn $"       wantResponse={action.WantResponse} actionId={action.ActionId} context=[{context}]"
        Task.CompletedTask

/// Every A2UI-level condition the renderer surfaces — an unsupported component, an unknown
/// catalog, a malformed message, a duplicate/unknown surface — reaches here independent of any
/// single `Ingest` call's own `Result`, so overall bot health stays observable even when a call
/// itself still succeeded (e.g. an unsupported component next to supported siblings that rendered).
let private observer: IA2uiObserver =
    { new IA2uiObserver with
        member _.OnA2uiError(error: A2uiError) = printfn $"[a2ui] {A2uiError.describe error}"

        member _.OnMalformedAction(descriptor: ActionDescriptor) =
            printfn $"[a2ui] '{descriptor.Name}' on surface '{descriptor.SurfaceId}' wanted a response but carried no actionId — dropped"

        member _.OnStaleSurfaceAction(descriptor: ActionDescriptor) =
            printfn $"[a2ui] '{descriptor.Name}' was tapped for surface '{descriptor.SurfaceId}', which this process no longer tracks — dropped" }

/// Renders 'deploy-1', edits it in place, deletes it; sends and tears down the LocalOpenUrl demo
/// surface 'docs-1'; then sends and tears down 'rating-1', whose unsupported `Slider` only ever
/// reaches `observer` above, never this call's own `Result`.
let private walkThroughLoop (renderer: A2uiRenderer) (chat: ChatId) : Task<unit> =
    task {
        printfn $"Renderer catalog: {renderer.Catalog.CatalogId}"

        match! renderer.Ingest(chat, createDeploySurfaceJson) with
        | Error e -> printfn $"unexpected: {A2uiError.describe e}"
        | Ok() -> printfn "sent 'deploy-1': one message, one keyboard row of two Buttons"

        // A real tap on Approve/Reject delivers a callback query that Telegram routes through the
        // bot's own Tool Router into `sink` above. Nothing taps this demo surface, so `sink` never
        // actually runs in this run — it is wired and ready for when it does.

        match! renderer.Ingest(chat, updateDeploySurfaceJson) with
        | Error e -> printfn $"unexpected: {A2uiError.describe e}"
        | Ok() -> printfn "edited 'deploy-1' in place: same message, new text, keyboard removed"

        match! renderer.Ingest(chat, deleteDeploySurfaceJson) with
        | Error e -> printfn $"unexpected: {A2uiError.describe e}"
        | Ok() -> printfn "deleted 'deploy-1'"

        match! renderer.Ingest(chat, createDocsSurfaceJson) with
        | Error e -> printfn $"unexpected: {A2uiError.describe e}"
        | Ok() -> printfn "sent 'docs-1': a client-side Button opens its link on-device, no server round trip on tap"

        match! renderer.Ingest(chat, deleteDocsSurfaceJson) with
        | Error e -> printfn $"unexpected: {A2uiError.describe e}"
        | Ok() -> printfn "deleted 'docs-1'"

        match! renderer.Ingest(chat, createRatingSurfaceJson) with
        | Error e -> printfn $"unexpected: {A2uiError.describe e}"
        | Ok() -> printfn "sent 'rating-1': the Text sibling rendered; the Slider was surfaced to the observer above"

        match! renderer.Ingest(chat, deleteRatingSurfaceJson) with
        | Error e -> printfn $"unexpected: {A2uiError.describe e}"
        | Ok() -> printfn "deleted 'rating-1'"
    }

[<EntryPoint>]
let main args =
    task {
        let botToken = requireEnv "BOT_TOKEN"
        let chat: ChatId = UMX.tag<chatId> (int64 (requireEnv "CHAT_ID"))
        let transport = Environment.GetEnvironmentVariable "TRANSPORT" |> Option.ofObj |> Option.defaultValue "polling"

        // `A2ui.rendererWithObserver` requires a Tool Router wired into the bot — its internal
        // `a2ui-action` tool is how a ServerEvent Button's tap ever reaches `sink` at all. This
        // example registers no business tools of its own, so an empty registry is enough.
        let tools = ToolRegistry.create ()
        use bindingStore = LiteDbBindingStore.OpenAt "a2ui-bindings.db"

        match transport with
        | "webhook" ->
            let publicUrl = requireEnv "PUBLIC_URL"
            let secret = requireEnv "WEBHOOK_SECRET"

            use! bot =
                TgBot.startWebhook (
                    (TgWebhookConfig.create (botToken, publicUrl, secret))
                        .WithTools(tools)
                        .WithBindingStore(bindingStore)
                )

            let renderer = A2ui.rendererWithObserver bot sink observer
            do! walkThroughLoop renderer chat

            let app = WebApplication.CreateBuilder(args).Build()
            app.MapTelegramWebhook("/telegram/webhook", bot.WebhookSource, secret) |> ignore
            printfn $"A2UI renderer (webhook) listening. Telegram POSTs updates to {publicUrl}/telegram/webhook"
            do! app.RunAsync()
            return 0
        | _ ->
            use! bot =
                TgBot.startPolling (
                    (TgBotConfig.create botToken)
                        .WithTools(tools)
                        .WithBindingStore(bindingStore)
                )

            let renderer = A2ui.rendererWithObserver bot sink observer
            do! walkThroughLoop renderer chat

            printfn "A2UI renderer (long polling) running. Ctrl+C to stop."
            do! Task.Delay Timeout.InfiniteTimeSpan
            return 0
    }
    |> fun run -> run.GetAwaiter().GetResult()
