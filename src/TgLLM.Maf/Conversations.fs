namespace TgLLM.Maf

open System.Collections.Concurrent
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

    /// Returns the chat's conversation, creating (and caching) its session on first call.
    member _.GetOrCreate(chat: ChatId, create: unit -> ValueTask<AgentSession>) : ValueTask<Conversation> =
        let lazySession = sessions.GetOrAdd(chat, (fun _ -> Lazy<Task<AgentSession>>(fun () -> (create ()).AsTask())))

        ValueTask<Conversation>(
            task {
                let! session = lazySession.Value
                return { Chat = chat; Session = session }
            }
        )

    /// Drops a chat's cached session — the next `GetOrCreate` for it starts fresh. Used when a
    /// run's resume fails: the agent's own continuation is now presumably dead, so restarting the
    /// conversation is safer than reusing a session that failed mid-turn.
    member _.Drop(chat: ChatId) : unit = sessions.TryRemove(chat) |> ignore
