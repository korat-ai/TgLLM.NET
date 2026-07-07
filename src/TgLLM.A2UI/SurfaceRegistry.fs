namespace TgLLM.A2UI

open System.Collections.Concurrent
open System.Text.Json.Nodes
open TgLLM.Core

/// One open A2UI surface's Telegram identity and current component/data-model state, tracked so
/// a stream of `updateComponents`/`updateDataModel` for the SAME surface id coalesces into edits
/// of one message rather than a fresh send each time.
[<NoComparison; NoEquality>]
type LiveSurface =
    { SurfaceId: string
      Chat: ChatId
      /// `None` until the surface's first render actually reaches the wire — recorded afterward
      /// via `SurfaceRegistry.RecordMessageId`, never guessed ahead of time.
      MessageId: MessageId option
      CatalogId: string
      /// The adjacency-list tree, by id — a `Map` rather than the wire's `list` so a later
      /// `updateComponents` can replace individual nodes by id without walking the whole tree.
      Components: Map<string, Component>
      DataModel: JsonNode }

/// What applying one A2UI message implies for Telegram — carried out by the façade over the
/// reused Tool Router send/edit-in-place machinery. `SurfaceRegistry.Apply` only ever DECIDES
/// which of these applies; it performs no IO itself.
[<NoComparison; NoEquality>]
type RenderEffect =
    /// The surface's first render: no message exists for it yet.
    | SendNew of chat: ChatId * RenderedSurface
    /// A later render of an already-sent surface: replace `message`'s content in place.
    | EditExisting of message: MessageId * RenderedSurface
    /// `deleteSurface` on a surface that had reached the wire.
    | DeleteMessage of message: MessageId
    /// Buffered, not yet renderable (no `root` component in the tree yet), or `deleteSurface` on
    /// a surface that was never actually sent.
    | NoEffect

/// Coalesces the incoming A2UI message stream per surface and tracks each live surface's
/// Telegram identity + component/data-model state. Thread-safe: a `ConcurrentDictionary` keyed by
/// surface id, with each `Apply`/`RecordMessageId` call replacing its own surface's entry
/// atomically — concurrent calls for DIFFERENT surface ids never contend, and concurrent calls for
/// the SAME surface id serialize on that one dictionary slot.
[<Sealed>]
type SurfaceRegistry(catalog: Catalog) =
    let surfaces = ConcurrentDictionary<string, LiveSurface>()

    let hasRoot (components: Map<string, Component>) : bool = Map.containsKey "root" components

    let render (surface: LiveSurface) : Result<RenderedSurface, A2uiError> =
        let components = surface.Components |> Map.toList |> List.map snd
        Renderer.render catalog surface.SurfaceId surface.DataModel components

    /// The effect implied by `surface`'s CURRENT state: not yet renderable (no `root`) is
    /// `NoEffect`; renderable and never sent is `SendNew`; renderable and already sent is
    /// `EditExisting` against its recorded message.
    let effectFor (surface: LiveSurface) : Result<RenderEffect, A2uiError> =
        if not (hasRoot surface.Components) then
            Ok NoEffect
        else
            render surface
            |> Result.map (fun rendered ->
                match surface.MessageId with
                | Some messageId -> EditExisting(messageId, rendered)
                | None -> SendNew(surface.Chat, rendered))

    /// Narrows every incoming raw node to the `telegram-basic` model and replaces the matching
    /// entries (by id) in `existing` — a node id already present is overwritten in place, matching
    /// A2UI's own adjacency-list update semantics; nothing in `existing` outside the incoming ids
    /// is touched.
    let mergeComponents (raw: RawComponent list) (existing: Map<string, Component>) : Map<string, Component> =
        raw |> List.map Component.toTelegramBasic |> List.fold (fun components c -> Map.add c.Id c components) existing

    /// Applies one A2UI message against this registry's current state and decides what it implies
    /// for Telegram. Pure decision-making — performs no IO; the caller carries out the returned
    /// effect and, for a `SendNew`, calls `RecordMessageId` once the send reaches the wire.
    member _.Apply(chat: ChatId, msg: A2uiMessage) : Result<RenderEffect, A2uiError> =
        match msg with
        | CreateSurface(surfaceId, catalogId, rawComponents, dataModelOpt) ->
            if catalogId <> catalog.CatalogId then
                Error(UnknownCatalog catalogId)
            elif surfaces.ContainsKey surfaceId then
                Error(DuplicateSurface surfaceId)
            else
                let components = mergeComponents rawComponents Map.empty
                let dataModel = dataModelOpt |> Option.defaultWith (fun () -> JsonObject() :> JsonNode)

                let surface: LiveSurface =
                    { SurfaceId = surfaceId
                      Chat = chat
                      MessageId = None
                      CatalogId = catalogId
                      Components = components
                      DataModel = dataModel }

                surfaces[surfaceId] <- surface
                effectFor surface

        | UpdateComponents(surfaceId, rawComponents) ->
            match surfaces.TryGetValue surfaceId with
            | false, _ -> Error(UnknownSurface surfaceId)
            | true, surface ->
                let updated = { surface with Components = mergeComponents rawComponents surface.Components }
                surfaces[surfaceId] <- updated
                effectFor updated

        | UpdateDataModel(surfaceId, path, valueOpt) ->
            match surfaces.TryGetValue surfaceId with
            | false, _ -> Error(UnknownSurface surfaceId)
            | true, surface ->
                let updated = { surface with DataModel = JsonPointer.trySet surface.DataModel path valueOpt }
                surfaces[surfaceId] <- updated
                effectFor updated

        | DeleteSurface surfaceId ->
            match surfaces.TryRemove surfaceId with
            | false, _ -> Error(UnknownSurface surfaceId)
            | true, surface ->
                match surface.MessageId with
                | Some messageId -> Ok(DeleteMessage messageId)
                | None -> Ok NoEffect

    /// Records the message a surface's `SendNew` render landed on — called by the façade AFTER
    /// the send reaches the wire, never guessed ahead of it. A surface id no longer tracked (e.g.
    /// already deleted) is silently ignored: nothing downstream depends on this call having any
    /// effect once the surface it targets is gone.
    member _.RecordMessageId(surfaceId: string, messageId: MessageId) : unit =
        match surfaces.TryGetValue surfaceId with
        | true, surface -> surfaces[surfaceId] <- { surface with MessageId = Some messageId }
        | false, _ -> ()

    /// The live data model for `surfaceId`, if it is currently tracked — used by the
    /// `a2ui-action` tool to resolve a tapped button's context at press time (not render time).
    member _.TryGetDataModel(surfaceId: string) : JsonNode option =
        match surfaces.TryGetValue surfaceId with
        | true, surface -> Some surface.DataModel
        | false, _ -> None
