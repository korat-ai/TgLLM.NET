namespace TgLLM.Maf

open System.Collections.Concurrent
open Microsoft.Extensions.AI
open TgLLM.Core

/// One live, undecided approval. Holds the LIVE MAF request object — required to build the resume
/// content via `request.CreateResponse` — which is exactly why this table cannot survive a process
/// restart even though the buttons' bindings (`IBindingStore`) can: a post-restart tap still routes
/// and is acked by the engine, but deterministically lands in the stale path
/// (`IMafObserver.OnStaleDecision`).
///
/// Deliberately does NOT carry the decision buttons' own `CallbackToken`s: `TgLLM.FSharp.TgBot`
/// exposes no accessor for the binding store, or for the tokens `SendKeyboardPlan`/
/// `EditKeyboardPlan` assign internally, so there is no seam this leaf can reach to remove an
/// untapped sibling button's binding as post-decision hygiene. This is not a correctness gap: the
/// at-most-once guarantee rests entirely on `PendingApprovals.TryConsume`'s atomic remove, not on
/// binding cleanup — an untapped sibling binding is simply left to expire or be superseded, never
/// able to resume a second time regardless.
[<NoComparison; NoEquality>]
type PendingApproval =
    { Chat: ChatId
      Request: ToolApprovalRequestContent
      /// Resolved once, at render time (`RunOwner.resolve`).
      Owner: OwnerScope
      /// The approval message — the edit-in-place target once this entry is consumed.
      MessageId: MessageId }

/// Keyed by `(chat, requestId)`. `TryConsume` is the at-most-once gate: it removes the entry
/// ATOMICALLY and returns it to exactly one caller — a racing sibling tap (or a redelivered/
/// duplicate decision that got past the engine's own query-id dedup) gets `ValueNone` and is
/// surfaced as stale, never resumed twice.
[<Sealed>]
type PendingApprovals() =
    let table = ConcurrentDictionary<ChatId * string, PendingApproval>()

    /// Records a new pending approval — called once, right after its message reaches the wire.
    member _.Add(entry: PendingApproval) : unit = table[(entry.Chat, entry.Request.RequestId)] <- entry

    /// Looks up the entry for `(chat, requestId)` WITHOUT removing it — `ValueNone` on a miss,
    /// exactly like `TryConsume`. Used to check a presser against `PendingApproval.Owner` BEFORE
    /// deciding whether to consume: `Bridge.fs`'s `HandleDecision` runs under this chat's own
    /// `withChatLock`, so a `TryGet` peek here is never raced by a concurrent consume/abandon for
    /// the SAME key — nothing else touching this table for this chat can run concurrently.
    member _.TryGet(chat: ChatId, requestId: string) : PendingApproval voption =
        match table.TryGetValue((chat, requestId)) with
        | true, entry -> ValueSome entry
        | false, _ -> ValueNone

    /// Removes and returns the entry for `(chat, requestId)` if it is still pending — `ValueNone`
    /// on a miss (already consumed, never existed, or belongs to a different chat).
    /// `ConcurrentDictionary.TryRemove` is itself atomic, so two concurrent callers for the SAME
    /// key can never both receive `ValueSome`.
    member _.TryConsume(chat: ChatId, requestId: string) : PendingApproval voption =
        match table.TryRemove((chat, requestId)) with
        | true, entry -> ValueSome entry
        | false, _ -> ValueNone

    /// Drains every entry still pending for `chat` (a failed/ended run's own leftovers) and
    /// returns them — the caller reports each as stale rather than leaving it in the table to be
    /// discovered later by a tap that will now never resume anything.
    member _.AbandonAllFor(chat: ChatId) : PendingApproval list =
        table.Keys
        |> Seq.filter (fun (entryChat, _) -> entryChat = chat)
        |> Seq.toList
        |> List.choose (fun key ->
            match table.TryRemove key with
            | true, entry -> Some entry
            | false, _ -> None)
