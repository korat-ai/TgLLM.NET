/// A durable, JSON-on-disk `ISessionStore`. Core stays IO-agnostic — this leaf project is where the
/// actual file IO lives, mirroring `FileBindingStore.fs`'s own structure for the session-store seam.
/// Loads existing session records when opened (`OpenAt`), so a restart pointing at the same file
/// restores every chat's session; every mutation (`Save`/`Remove`/`EvictIdle`) rewrites the whole
/// file under a single in-process writer lock before completing.
namespace TgLLM.Persistence

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open TgLLM.Core

/// The on-disk JSON shape of one `SessionRecord`. `Payload` is opaque `byte[]` on the domain side
/// (`SessionRecord.Payload`'s own doc comment: Core never inspects it); `System.Text.Json` CAN
/// serialize a raw `byte[]` property directly (it emits/reads a Base64 string under the hood), but
/// this DTO does the Base64 encoding explicitly as a plain `string` field instead, mirroring
/// `BindingDto`'s own convention of keeping every DTO field a plain, inspectable primitive rather
/// than leaning on an encoder implementation detail of the specific serializer in use. NOT marked
/// `private` (same disclosed deviation as `BindingDto`, for the same reason: `System.Text.Json`'s
/// default reflection-based (de)serializer only sees PUBLIC constructors/properties). `[<NoComparison>]`:
/// this DTO is never ordered/compared, only (de)serialized.
[<NoComparison>]
type SessionRowDto =
    { ChatId: int64
      PayloadBase64: string
      LastActivityAt: DateTimeOffset }

module SessionRowDto =
    let ofDomain (chat: ChatId) (record: SessionRecord) : SessionRowDto =
        { ChatId = UMX.untag chat
          PayloadBase64 = Convert.ToBase64String record.Payload
          LastActivityAt = record.LastActivityAt }

    /// `None` for a row this store could never have written itself. `OpenAt` treats that as file
    /// corruption and fails the whole load so partial durable state is never accepted silently.
    let toDomain (dto: SessionRowDto) : (ChatId * SessionRecord) option =
        try
            let chat = UMX.tag<chatId> dto.ChatId
            let record: SessionRecord = { Payload = Convert.FromBase64String dto.PayloadBase64; LastActivityAt = dto.LastActivityAt }
            Some(chat, record)
        with
        | :? FormatException
        | :? ArgumentNullException -> None

/// Durable, JSON-on-disk `ISessionStore`. `OpenAt` is the only public constructor path (mirrors
/// `FileBindingStore.openAt`'s convention) — it both creates AND loads, so there is no way to end up
/// with an instance that hasn't seen whatever is already on disk.
[<Sealed>]
type FileSessionStore
    private
    (
        path: string,
        initial: ConcurrentDictionary<ChatId, SessionRecord>
    ) =

    /// Single in-process writer lock — every `Save`/`Remove`/`EvictIdle` mutates the in-memory index
    /// AND rewrites the file as one atomic-from-this-process's-view step, mirroring
    /// `FileBindingStore`'s own `gate`.
    let gate = obj ()
    let sessions = initial

    /// Rewrites the WHOLE file from the current in-memory index. Only ever called under `gate`.
    /// Writes to a sibling temp file then `File.Move`s it into place (atomic on the same volume):
    /// a crash mid-write leaves the temp file corrupt but `path` itself untouched, rather than a
    /// truncated `path` that the next `OpenAt` would have to recover from.
    let persist () =
        let dtos = sessions |> Seq.map (fun kv -> SessionRowDto.ofDomain kv.Key kv.Value) |> Seq.toArray
        let tempPath = $"{path}.tmp"
        File.WriteAllText(tempPath, JsonSerializer.Serialize dtos)
        File.Move(tempPath, path, overwrite = true)

    interface ISessionStore with
        member _.Save(chat: ChatId, record: SessionRecord, _ct: CancellationToken) : ValueTask =
            lock gate (fun () ->
                sessions[chat] <- record
                persist ())

            ValueTask.CompletedTask

        member _.TryGet(chat: ChatId, _ct: CancellationToken) : ValueTask<SessionRecord voption> =
            match sessions.TryGetValue chat with
            | true, record -> ValueTask.FromResult(ValueSome record)
            | false, _ -> ValueTask.FromResult ValueNone

        member _.Remove(chat: ChatId, _ct: CancellationToken) : ValueTask =
            lock gate (fun () ->
                sessions.TryRemove(chat) |> ignore
                persist ())

            ValueTask.CompletedTask

        /// Sweeps the in-memory index for records at or before `olderThan` (per `ISessionStore.
        /// EvictIdle`'s own boundary contract), removes them, and persists the result — same
        /// in-process contract as `InMemorySessionStore.EvictIdle`, but durable: a store reopened
        /// over the same file does not resurrect an evicted record.
        member _.EvictIdle(olderThan: DateTimeOffset) : ValueTask<int> =
            lock gate (fun () ->
                let idleChats =
                    sessions
                    |> Seq.choose (fun kv -> if kv.Value.LastActivityAt <= olderThan then Some kv.Key else None)
                    |> Seq.toList

                if not (List.isEmpty idleChats) then
                    for chat in idleChats do
                        sessions.TryRemove(chat) |> ignore

                    persist ()

                ValueTask.FromResult(List.length idleChats))

    /// Opens (or creates) a durable session store backed by `path`: loads any session records
    /// already on disk (the restart guarantee — a NEW instance over the SAME path sees everything a
    /// previous instance saved) or starts empty if the file doesn't exist yet.
    static member OpenAt(path: string) : FileSessionStore =
        let initial = ConcurrentDictionary<ChatId, SessionRecord>()

        if File.Exists path then
            let json = File.ReadAllText path

            if String.IsNullOrWhiteSpace json then
                raise (InvalidDataException($"Session store '{path}' is empty or whitespace."))

            try
                match JsonSerializer.Deserialize<SessionRowDto[]> json |> Option.ofObj with
                | None -> raise (InvalidDataException($"Session store '{path}' contains JSON null."))
                | Some dtos ->
                    for dto in dtos do
                        if isNull (box dto) then
                            raise (InvalidDataException($"Session store '{path}' contains a null row."))

                        match SessionRowDto.toDomain dto with
                        | Some(chat, record) -> initial[chat] <- record
                        | None ->
                            raise (InvalidDataException($"Session store '{path}' contains an invalid session row."))
            with :? JsonException as ex ->
                raise (InvalidDataException($"Session store '{path}' is not valid JSON.", ex))

        FileSessionStore(path, initial)
