namespace TgLLM.Maf

open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Agents.AI
open TgLLM.Core

/// A chat's agent conversation. Created lazily on the chat's first turn; only ever touched on that
/// chat's own lock (`Bridge.fs`'s `withChatLock`), so no additional internal locking is needed here
/// — `AgentSession` documents no thread-safety guarantee of its own.
[<NoComparison; NoEquality>]
type Conversation = { Chat: ChatId; Session: AgentSession }

/// One in-memory `AgentSession` per chat. `GetOrCreate`'s `create` factory runs AT MOST ONCE per
/// chat, even under concurrent first calls: `Lazy<Task<AgentSession>>` (default
/// `ExecutionAndPublication` thread-safety mode) guarantees exactly one factory invocation wins,
/// and `ConcurrentDictionary.GetOrAdd` guarantees exactly one `Lazy` instance is ever published for
/// a given chat.
[<Sealed>]
type Conversations() =
    let sessions = ConcurrentDictionary<ChatId, Lazy<Task<AgentSession>>>()

    /// Returns the chat's conversation, creating (and caching) its session on first call. A
    /// FAILED create is NOT replayed forever: the classic `Lazy<Task<T>>` pitfall is that the
    /// factory delegate itself returns a `Task` SUCCESSFULLY (so `Lazy` caches that task
    /// reference, once, for good) even though the task it returned later transitions to Faulted —
    /// so a chat whose first `CreateSessionAsync` throws would otherwise be bricked permanently,
    /// with every later turn replaying the SAME captured exception. On a fault, THIS lazy instance
    /// is evicted (only if it is still the published one — a concurrent `Drop`/retry may already
    /// have replaced it, in which case there is nothing of this attempt's to remove), so the NEXT
    /// call gets a fresh attempt instead.
    member _.GetOrCreate(chat: ChatId, create: unit -> ValueTask<AgentSession>) : ValueTask<Conversation> =
        let lazySession = sessions.GetOrAdd(chat, (fun _ -> Lazy<Task<AgentSession>>(fun () -> (create ()).AsTask())))

        ValueTask<Conversation>(
            task {
                try
                    let! session = lazySession.Value
                    return { Chat = chat; Session = session }
                with ex ->
                    (sessions :> ICollection<KeyValuePair<ChatId, Lazy<Task<AgentSession>>>>).Remove(KeyValuePair(chat, lazySession))
                    |> ignore

                    return raise ex
            }
        )

    /// Drops a chat's cached session — the next `GetOrCreate` for it starts fresh. Used when a
    /// run's resume fails: the agent's own continuation is now presumably dead, so restarting the
    /// conversation is safer than reusing a session that failed mid-turn.
    member _.Drop(chat: ChatId) : unit = sessions.TryRemove(chat) |> ignore
