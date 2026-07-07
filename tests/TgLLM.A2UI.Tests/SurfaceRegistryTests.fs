/// Tests for `SurfaceRegistry.Apply`: the pure decision of what an A2UI message implies for
/// Telegram (`SendNew`/`EditExisting`/`DeleteMessage`/`NoEffect`), plus surface-lifecycle
/// bookkeeping (create-once, unknown-surface rejection).
module TgLLM.A2UI.Tests.SurfaceRegistryTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.A2UI

let private chat (id: int64) : ChatId = UMX.tag<chatId> id

/// `JsonValue.Create` is annotated as possibly returning `null`, but never does for a non-null
/// input — narrowed here once so the rest of this file stays in non-nullable `JsonNode` territory.
let private jsonOf (s: string) : JsonNode =
    match JsonValue.Create s with
    | null -> failwith "JsonValue.Create of a non-null string literal is never null"
    | v -> v

/// `JsonValue.Create` on a value type has no null case at all (unlike the `string` overload
/// above) — no narrowing needed.
let private jsonOfInt (n: int) : JsonNode = JsonValue.Create n

let private rawComponent (id: string) (componentType: string) (extra: (string * JsonNode) list) : RawComponent =
    let fields = JsonObject()
    fields["id"] <- jsonOf id
    fields["component"] <- jsonOf componentType

    for key, value in extra do
        fields[key] <- value

    { Id = id; ComponentType = componentType; Fields = fields }

let private rootTextComponent (text: string) : RawComponent =
    rawComponent "root" "Text" [ "text", jsonOf text ]

let private createSurfaceMsg (surfaceId: string) (components: RawComponent list) : A2uiMessage =
    CreateSurface(surfaceId, Catalog.telegramBasic.CatalogId, components, None)

[<Tests>]
let surfaceRegistryTests =
    testList "SurfaceRegistry" [

        testCase "createSurface with a root component yields SendNew, carrying the caller's chat" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let chat1 = chat 1L

            match registry.Apply(chat1, createSurfaceMsg "s1" [ rootTextComponent "hello" ]) with
            | Ok(SendNew(sentChat, rendered)) ->
                Expect.equal sentChat chat1 "the surface's own chat is carried through"
                Expect.equal rendered.Text "hello" "the root Text renders as the body"
            | other -> failtestf "expected Ok (SendNew _), got %A" other

        testCase "createSurface with no root component yields NoEffect" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let orphan = rawComponent "not-root" "Text" [ "text", jsonOf "unreachable" ]

            match registry.Apply(chat 2L, createSurfaceMsg "s2" [ orphan ]) with
            | Ok NoEffect -> ()
            | other -> failtestf "expected Ok NoEffect, got %A" other

        testCase "a second createSurface for a live surface id is rejected as DuplicateSurface" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let chat1 = chat 3L
            let msg = createSurfaceMsg "s3" [ rootTextComponent "hello" ]

            registry.Apply(chat1, msg) |> ignore

            match registry.Apply(chat1, msg) with
            | Error(DuplicateSurface "s3") -> ()
            | other -> failtestf "expected Error (DuplicateSurface \"s3\"), got %A" other

        testCase "createSurface with an unknown catalogId is rejected as UnknownCatalog" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let msg = CreateSurface("s4", "some-other-catalog", [ rootTextComponent "hello" ], None)

            match registry.Apply(chat 4L, msg) with
            | Error(UnknownCatalog "some-other-catalog") -> ()
            | other -> failtestf "expected Error (UnknownCatalog _), got %A" other

        testCase "updateComponents for an unknown surface is rejected as UnknownSurface" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)

            match registry.Apply(chat 5L, UpdateComponents("never-created", [ rootTextComponent "hi" ])) with
            | Error(UnknownSurface "never-created") -> ()
            | other -> failtestf "expected Error (UnknownSurface _), got %A" other

        testCase "updateDataModel for an unknown surface is rejected as UnknownSurface" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)

            match registry.Apply(chat 6L, UpdateDataModel("never-created", "/x", Some(jsonOfInt 1))) with
            | Error(UnknownSurface "never-created") -> ()
            | other -> failtestf "expected Error (UnknownSurface _), got %A" other

        testCase "deleteSurface for an unknown surface is rejected as UnknownSurface" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)

            match registry.Apply(chat 7L, DeleteSurface "never-created") with
            | Error(UnknownSurface "never-created") -> ()
            | other -> failtestf "expected Error (UnknownSurface _), got %A" other

        testCase "RecordMessageId then a later updateComponents on the same surface yields EditExisting" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let chat1 = chat 8L

            match registry.Apply(chat1, createSurfaceMsg "s8" [ rootTextComponent "v1" ]) with
            | Ok(SendNew _) -> ()
            | other -> failtestf "test setup: expected Ok (SendNew _), got %A" other

            let messageId = UMX.tag<messageId> 100L
            registry.RecordMessageId("s8", messageId)

            match registry.Apply(chat1, UpdateComponents("s8", [ rootTextComponent "v2" ])) with
            | Ok(EditExisting(editedId, rendered)) ->
                Expect.equal editedId messageId "the edit targets the recorded message id"
                Expect.equal rendered.Text "v2" "the updated tree re-renders"
            | other -> failtestf "expected Ok (EditExisting _), got %A" other

        testCase "updateDataModel on a live, already-sent surface re-renders the bound text and edits in place" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let chat1 = chat 9L
            let dataModel = JsonNode.Parse """{ "greeting": "hi" }""" |> Option.ofObj |> Option.get
            let bound = rawComponent "root" "Text" [ "text", JsonObject(Seq.singleton (System.Collections.Generic.KeyValuePair("path", jsonOf "/greeting"))) ]

            match CreateSurface("s9", Catalog.telegramBasic.CatalogId, [ bound ], Some dataModel) |> fun msg -> registry.Apply(chat1, msg) with
            | Ok(SendNew(_, rendered)) -> Expect.equal rendered.Text "hi" "test setup: the bound text resolves initially"
            | other -> failtestf "test setup: expected Ok (SendNew _), got %A" other

            let messageId = UMX.tag<messageId> 101L
            registry.RecordMessageId("s9", messageId)

            match registry.Apply(chat1, UpdateDataModel("s9", "/greeting", Some(jsonOf "hello"))) with
            | Ok(EditExisting(editedId, rendered)) ->
                Expect.equal editedId messageId "the edit targets the recorded message id"
                Expect.equal rendered.Text "hello" "the bound text reflects the updated data model"
            | other -> failtestf "expected Ok (EditExisting _), got %A" other

        testCase "deleteSurface on a live surface with a recorded message id yields DeleteMessage" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let chat1 = chat 10L
            registry.Apply(chat1, createSurfaceMsg "s10" [ rootTextComponent "v1" ]) |> ignore
            let messageId = UMX.tag<messageId> 200L
            registry.RecordMessageId("s10", messageId)

            match registry.Apply(chat1, DeleteSurface "s10") with
            | Ok(DeleteMessage deletedId) -> Expect.equal deletedId messageId "the recorded message id is targeted"
            | other -> failtestf "expected Ok (DeleteMessage _), got %A" other

        testCase "deleteSurface on a live surface that was never sent (no root yet) yields NoEffect" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let chat1 = chat 11L
            let orphan = rawComponent "not-root" "Text" [ "text", jsonOf "unreachable" ]
            registry.Apply(chat1, createSurfaceMsg "s11" [ orphan ]) |> ignore

            match registry.Apply(chat1, DeleteSurface "s11") with
            | Ok NoEffect -> ()
            | other -> failtestf "expected Ok NoEffect, got %A" other

        testCase "RecordMessageId for a surface id that isn't tracked is silently ignored" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            // No exception, no observable effect — nothing to assert beyond "this doesn't throw".
            registry.RecordMessageId("never-created", UMX.tag<messageId> 1L)

        testCase "TryGetDataModel returns None for a surface id that isn't tracked" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            Expect.equal (registry.TryGetDataModel "never-created") None "an untracked surface has no data model"

        testCase "TryGetDataModel returns the live surface's current data model" <| fun _ ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)
            let dataModel = JsonNode.Parse """{ "env": "prod" }""" |> Option.ofObj |> Option.get
            let msg = CreateSurface("s12", Catalog.telegramBasic.CatalogId, [ rootTextComponent "hi" ], Some dataModel)
            registry.Apply(chat 12L, msg) |> ignore

            match registry.TryGetDataModel "s12" with
            | Some model -> Expect.isTrue (JsonNode.DeepEquals(model, dataModel)) "the tracked data model matches what createSurface supplied"
            | None -> failtest "expected Some data model"

        testCaseAsync "many concurrent createSurface calls for the SAME surface id yield exactly one SendNew, the rest DuplicateSurface" (
            async {
                do!
                    task {
                        let registry = SurfaceRegistry(Catalog.telegramBasic)
                        let chat1 = chat 13L
                        let msg = createSurfaceMsg "race-surface" [ rootTextComponent "hello" ]
                        let concurrency = 64

                        // A shared start gate maximizes overlap: every task blocks on the SAME
                        // uncompleted task until all `concurrency` of them are queued up, then all
                        // race `Apply` for the SAME surface id at once, rather than trickling in one
                        // at a time (which a check-then-act race can slip through unnoticed).
                        let startGate = TaskCompletionSource()

                        let raceOnce () : Task<Result<RenderEffect, A2uiError>> =
                            task {
                                do! startGate.Task
                                return registry.Apply(chat1, msg)
                            }

                        let raced = Array.init concurrency (fun _ -> Task.Run<Result<RenderEffect, A2uiError>> raceOnce)

                        startGate.SetResult()
                        let! results = Task.WhenAll raced

                        let sendNewCount =
                            results |> Array.filter (function Ok(SendNew _) -> true | _ -> false) |> Array.length

                        let duplicateCount =
                            results
                            |> Array.filter (function
                                | Error(DuplicateSurface "race-surface") -> true
                                | _ -> false)
                            |> Array.length

                        Expect.equal sendNewCount 1 "exactly one concurrent create wins and produces the surface's own first send"
                        Expect.equal duplicateCount (concurrency - 1) "every other concurrent create for the same id is rejected as a duplicate, never a second send"
                    }
                    |> Async.AwaitTask
            }
        )
    ]
