/// A durable, JSON-on-disk `IBindingStore`. Core stays IO-agnostic (Principle III) — this leaf
/// project is where the actual file IO lives. Loads existing bindings when opened (`openAt`), so
/// a restart pointing at the same file restores every binding a keyboard was sent with; every
/// mutation (`Save`/`Remove`) rewrites the whole file under a single in-process writer lock
/// before completing.
namespace TgLLM.Persistence

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open TgLLM.Core

/// The on-disk JSON shape of one `ToolBinding`. `ToolBinding`'s own fields are library-internal
/// wrapper types (`CallbackToken`, `ToolName`) with private constructors and smart-constructor
/// invariants — not directly STJ-serializable, and not something a durable store's wire format
/// should be coupled to anyway (DTOs stay decoupled from domain types: DUs/opaque wrappers change
/// shape, break readers of an existing bindings file). This DTO is the plain wire shape;
/// `BindingDto.ofDomain`/`toDomain` map at the file boundary. NOT marked
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
            // `BindingDto` itself doesn't carry Owner/ExpiresAt/SingleUse yet (this store's on-disk
            // shape is untouched by this slice's foundational phase, US4 is out of scope here) —
            // every row this store has ever written is therefore slice-2-shaped, so
            // `ToolBinding.create`'s defaults (Anyone/None/false) are exactly correct, not a
            // temporary shortcut (FR-017).
            Some(ToolBinding.create token toolName (dto.Arg |> Option.ofObj))
        | _ -> None

/// Durable, JSON-on-disk `IBindingStore`. `openAt` is the only public constructor path (mirrors
/// slice-1's smart-constructor convention) — it both creates AND loads, so there is no way to end
/// up with an instance that hasn't seen whatever is already on disk.
[<Sealed>]
type FileBindingStore
    private
    (
        path: string,
        initial: ConcurrentDictionary<CallbackToken, ToolBinding>
    ) =

    /// Single in-process writer lock — every `Save`/`Remove` mutates the in-memory index AND
    /// rewrites the file as one atomic-from-this-process's-view step. A PoC-scope file store has
    /// no cross-process writer to coordinate with, so a plain `lock` is sufficient; it
    /// deliberately does the (synchronous) file IO while held, so two concurrent `Save`s can never
    /// interleave their writes.
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

        /// Sweeps the in-memory index for bindings past their `ExpiresAt` (per `Expiry.isLive`),
        /// removes them, and persists the result — same in-process contract as
        /// `InMemoryBindingStore.EvictExpired`. NOTE (accepted, documented limitation, mirrors
        /// `BindingDto`'s own doc comment above): the on-disk `BindingDto` shape does not yet carry
        /// `ExpiresAt` — every row this store persists and reloads is still slice-2-shaped, so a
        /// binding's `ExpiresAt` only survives for the lifetime of the CURRENT process, not across
        /// a restart. `EvictExpired` therefore only ever has something to evict for bindings saved
        /// (with an expiry) THIS session; wiring the full field through the on-disk DTO is US4
        /// scope, not this eviction-seam addition.
        member _.EvictExpired(now: DateTimeOffset) : ValueTask<int> =
            lock gate (fun () ->
                let expiredTokens =
                    bindings
                    |> Seq.choose (fun kv -> if Expiry.isLive now kv.Value.ExpiresAt then None else Some kv.Key)
                    |> Seq.toList

                if not (List.isEmpty expiredTokens) then
                    for token in expiredTokens do
                        bindings.TryRemove(token) |> ignore

                    persist ()

                ValueTask.FromResult(List.length expiredTokens))

    /// Opens (or creates) a durable binding store backed by `path`: loads any bindings already on
    /// disk (the restart guarantee — a NEW instance over the SAME path sees everything a previous
    /// instance saved) or starts empty if the file doesn't exist yet.
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
