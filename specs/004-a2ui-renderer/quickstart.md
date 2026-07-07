# Quickstart: A2UI Renderer for Telegram

Design-time usage of slice 004: drive a Telegram bot from an agent that already speaks A2UI. (Shipped
user docs live in `docs/quickstart.md`, updated during implementation.)

## 1. Wire the renderer (F#)

```fsharp
// A durable binding store lets a tap survive a restart (reused from the Tool Router).
let store = LiteDbBindingStore.OpenAt "a2ui-bindings.db"
let bot = TgBot.startPolling (TgBotConfig.create(token).WithBindingStore(store))

// The sink is where a tap's A2UI `action` goes — the host relays it to its agent.
let sink : ActionSink = fun action -> task { do! myAgent.SendA2ui(action) }   // host's own agent transport

let renderer = A2ui.renderer bot sink
```

## 2. Ingest an A2UI surface

The agent emits standard A2UI. The host hands each message to the renderer for a target chat (A2UI has
no chat identity — the host supplies it):

```fsharp
// createSurface + updateComponents describing a Text and two Buttons in a Row → one Telegram message.
do! renderer.Ingest(chatId, """
  { "version":"v1.0", "createSurface": {
      "surfaceId":"deploy-1", "catalogId":"telegram-basic",
      "components":[
        { "id":"root", "component":"Column", "children":["title","actions"] },
        { "id":"title", "component":"Text", "text":{"path":"/title"} },
        { "id":"actions", "component":"Row", "children":["ok","no"] },
        { "id":"ok", "component":"Button", "text":"Approve",
          "action":{"event":{"name":"approve","context":{"env":{"path":"/env"}},"wantResponse":true,"actionId":"a1"}} },
        { "id":"no", "component":"Button", "text":"Reject",
          "action":{"event":{"name":"reject","context":{},"wantResponse":false}} } ],
      "dataModel":{ "title":"Deploy **v2** to prod?", "env":"prod" } } }
  """)
// → one message: body "Deploy v2 to prod?", one keyboard row [Approve][Reject].
```

## 3. The tap → action → re-render loop

```fsharp
// When a user taps "Approve", `sink` receives:
//   A2uiAction { Name="approve"; SurfaceId="deploy-1"; SourceComponentId="ok";
//                Timestamp=...; Context=[("env", "prod")]; WantResponse=true; ActionId=Some "a1" }
// The host relays it to the agent; the agent replies with an update for the SAME surface:

do! renderer.Ingest(chatId, """
  { "version":"v1.0", "updateComponents": {
      "surfaceId":"deploy-1",
      "components":[ { "id":"title", "component":"Text", "text":"Deploying…" },
                     { "id":"root", "component":"Column", "children":["title"] } ] } }
  """)
// → the SAME message is edited in place: body "Deploying…", keyboard removed.

// Later:  renderer.Ingest(chatId, """{ "version":"v1.0", "deleteSurface": { "surfaceId":"deploy-1" } }""")
// → the message is deleted.
```

## 4. C# — the same, idiomatically

```csharp
var renderer = A2uiRenderer.Create(agent, action => myAgent.SendA2uiAsync(action));
var result = await renderer.IngestAsync(chatId, createSurfaceJson);
// result surfaces an A2uiError (unknown catalog / unsupported component / malformed) without throwing.
```

## 5. Unsupported components are surfaced, not silent

```fsharp
// A surface containing a `Slider` (outside telegram-basic):
do! renderer.Ingest(chatId, sliderSurfaceJson)
// → the Slider is reported as an unsupported component (observable via the bot's observer/logger);
//   the supported siblings still render, and the bot keeps working. An unknown catalogId is rejected
//   with a surfaced error.
```

## What you get

- An agent that already emits A2UI (for a web/mobile renderer) drives a **Telegram** bot with no
  Telegram-specific code.
- Surfaces render as messages; taps flow back as A2UI `action` messages; agent replies re-render in place.
- Only what Telegram can honestly show renders; everything else is surfaced, never silently wrong.
