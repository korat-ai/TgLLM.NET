/// T026 (feature 002-llm-tool-router, US3, data-model.md "Tool binding", research.md D5): a
/// durable, JSON-on-disk `IBindingStore`. Core stays IO-agnostic (Principle III) — this leaf project
/// is where the actual file IO lives. Loads existing bindings when opened (`openAt`), so a restart
/// pointing at the same file restores every binding a keyboard was sent with (SC-004); every
/// mutation (`Save`/`Remove`) rewrites the whole file under a single in-process lock
/// (single-writer, per T026's own wording) before completing.
namespace TgLLM.Persistence

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open TgLLM.Core

/// The on-disk JSON shape of one `ToolBinding` (data-model.md "Tool binding"). `ToolBinding`'s own
/// fields are library-internal wrapper types (`CallbackToken`, `ToolName`) with private constructors
/// and smart-constructor invariants — not directly STJ-serializable, and not something a durable
/// store's wire format should be coupled to anyway (CLAUDE.md's "DTO ≠ domain type" serialization
/// rule: DUs/opaque wrappers change shape, break readers of an existing bindings file). This DTO is
/// the plain wire shape; `BindingDto.ofDomain`/`toDomain` map at the file boundary. NOT marked
/// `private` (disclosed deviation from the usual "hide the DTO" instinct): `System.Text.Json`'s
/// default reflection-based (de)serializer only sees PUBLIC constructors/properties, and this
/// project ships no `.fsi` signature file to hide a public type from other assemblies anyway — this
/// module has no public functions/types besides `FileBindingStore` itself, so `BindingDto` is
/// unreachable from outside this assembly in practice despite the accessibility modifier.
type BindingDto = { Token: string; ToolName: string; Arg: string | null }

module BindingDto =
    let ofDomain (binding: ToolBinding) : BindingDto =
        { Token = CallbackToken.value binding.Token
          ToolName = ToolName.value binding.ToolName
          Arg = binding.Arg |> Option.toObj }

    /// `None` for a row this store could never have written itself (a hand-edited or corrupted
    /// file) — skipped on load rather than failing the whole `openAt` call. There is no caller here
    /// to hand a `Result`/exception to (this runs at file-open time, not in response to a request),
    /// so "best-effort load, drop what doesn't parse" is the only sensible contract, mirroring how
    /// `CallbackToken.tryParse` is itself total or how `Mapping.toAgentEvent` skips an unparsable
    /// update rather than throwing.
    let toDomain (dto: BindingDto) : ToolBinding option =
        match CallbackToken.tryParse dto.Token, ToolName.create dto.ToolName with
        | ValueSome token, Ok toolName ->
            Some
                { Token = token
                  ToolName = toolName
                  Arg = dto.Arg |> Option.ofObj }
        | _ -> None

/// Durable, JSON-on-disk `IBindingStore` (contracts/tool-router.md "Durable store"). `openAt` is the
/// only public constructor path (mirrors slice-1's smart-constructor convention) — it both creates
/// AND loads, so there is no way to end up with an instance that hasn't seen whatever is already on
/// disk.
[<Sealed>]
type FileBindingStore
    private
    (
        path: string,
        initial: ConcurrentDictionary<CallbackToken, ToolBinding>
    ) =

    /// Single in-process writer lock (T026: "single-writer serialization") — every `Save`/`Remove`
    /// mutates the in-memory index AND rewrites the file as one atomic-from-this-process's-view
    /// step. A PoC-scope file store (plan.md "Scale/Scope") has no cross-process writer to
    /// coordinate with, so a plain `lock` is sufficient; it deliberately does the (synchronous) file
    /// IO while held, so two concurrent `Save`s can never interleave their writes.
    let gate = obj ()
    let bindings = initial

    /// Rewrites the WHOLE file from the current in-memory index. Only ever called under `gate`.
    /// Writes to a sibling temp file then `File.Move`s it into place (atomic on the same volume):
    /// a crash mid-write leaves the temp file corrupt but `path` itself untouched, rather than a
    /// truncated `path` that the next `openAt` would have to recover from.
    let persist () =
        let dtos = bindings.Values |> Seq.map BindingDto.ofDomain |> Seq.toArray
        let tempPath = $"{path}.tmp"
        File.WriteAllText(tempPath, JsonSerializer.Serialize dtos)
        File.Move(tempPath, path, overwrite = true)

    interface IBindingStore with
        member _.Save(newBindings: IReadOnlyList<ToolBinding>, _ct: CancellationToken) : ValueTask =
            lock gate (fun () ->
                for binding in newBindings do
                    bindings[binding.Token] <- binding

                persist ())

            ValueTask.CompletedTask

        member _.TryGet(token: CallbackToken, _ct: CancellationToken) : ValueTask<ToolBinding voption> =
            match bindings.TryGetValue token with
            | true, binding -> ValueTask.FromResult(ValueSome binding)
            | false, _ -> ValueTask.FromResult ValueNone

        member _.Remove(tokens: IReadOnlyList<CallbackToken>, _ct: CancellationToken) : ValueTask =
            lock gate (fun () ->
                for token in tokens do
                    bindings.TryRemove(token) |> ignore

                persist ())

            ValueTask.CompletedTask

    /// Opens (or creates) a durable binding store backed by `path` (contracts/tool-router.md
    /// `FileBindingStore.openAt`): loads any bindings already on disk (the SC-004 restart guarantee
    /// — a NEW instance over the SAME path sees everything a previous instance saved) or starts
    /// empty if the file doesn't exist yet.
    static member openAt(path: string) : FileBindingStore =
        let initial = ConcurrentDictionary<CallbackToken, ToolBinding>()

        if File.Exists path then
            let json = File.ReadAllText path

            if not (String.IsNullOrWhiteSpace json) then
                // Best-effort load (mirrors `BindingDto.toDomain`'s own per-row contract above): a
                // truncated write (crash mid-`persist`, before the atomicity fix) or a hand-edited
                // file can leave genuinely-unparsable JSON, or the JSON literal `null` (STJ then
                // returns a null array reference, Always-Rule 5) — either way there is no caller
                // here to hand a `Result`/exception to, so start empty rather than crash the bot.
                try
                    match JsonSerializer.Deserialize<BindingDto[]> json |> Option.ofObj with
                    | None -> ()
                    | Some dtos ->
                        for dto in dtos do
                            match BindingDto.toDomain dto with
                            | Some binding -> initial[binding.Token] <- binding
                            | None -> ()
                with :? JsonException -> ()

        FileBindingStore(path, initial)
