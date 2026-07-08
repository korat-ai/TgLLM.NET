namespace TgLLM.Maf

open FSharp.UMX
open TgLLM.Core

/// Who may decide an approval a run raises — pure, total, an FsCheck property target. Reuses
/// Core's `OwnerScope` (`Anyone | User of UserId`); this module only decides which scope applies
/// when the host didn't pass one explicitly.
module RunOwner =

    /// Bot API vendor fact (core.telegram.org): a private chat's `chat.id` IS the peer's user id
    /// (positive; group/channel ids are negative) — the "peer in a private chat" default is
    /// knowable from `ChatId` alone, with no further lookup.
    let private isPrivateChat (chat: ChatId) : bool = UMX.untag chat > 0L

    /// Resolution order (host policy first, obvious inference second):
    /// 1. `explicitScope` — the host's own choice for this run — always wins.
    /// 2. `initiator = Some sender` — a message-initiated turn — defaults to `User sender`: the
    ///    message's own sender owns the approvals its turn raises.
    /// 3. `initiator = None` (a host-initiated run) in a private chat — defaults to `User peer`,
    ///    the chat's own id reinterpreted as a user id.
    /// 4. `initiator = None` in a non-private chat — no inference is "obvious" here, so the
    ///    default is `Anyone`, not a hard-coded business rule.
    let resolve (explicitScope: OwnerScope option) (initiator: UserId option) (chat: ChatId) : OwnerScope =
        match explicitScope, initiator with
        | Some scope, _ -> scope
        | None, Some sender -> User sender
        | None, None -> if isPrivateChat chat then User(UMX.tag<userId> (UMX.untag chat)) else Anyone
