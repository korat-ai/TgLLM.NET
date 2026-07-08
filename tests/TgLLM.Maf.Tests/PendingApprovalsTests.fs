/// Tests for `PendingApprovals`: `Add`/`TryConsume` delivers each entry to exactly one caller and
/// removes it atomically (a second consume misses); `AbandonAllFor` drains a chat's own entries and
/// leaves other chats untouched.
module TgLLM.Maf.Tests.PendingApprovalsTests

open System.Collections.Generic
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open Microsoft.Extensions.AI
open TgLLM.Core
open TgLLM.Maf

let private chat (id: int64) : ChatId = UMX.tag<chatId> id
let private messageId (id: int64) : MessageId = UMX.tag<messageId> id

let private requestContent (requestId: string) (toolName: string) : ToolApprovalRequestContent =
    let call = FunctionCallContent("call-1", toolName, Dictionary<string, obj>() :> IDictionary<string, obj>)
    ToolApprovalRequestContent(requestId, call)

/// `PendingApproval` is `[<NoComparison; NoEquality>]` (it holds a live MAF request object) — every
/// "this consume/abandon missed" assertion below checks the `voption` case directly rather than via
/// `Expect.equal`, which would need an equality constraint this type deliberately doesn't support.
let private isMiss (result: PendingApproval voption) : bool =
    match result with
    | ValueNone -> true
    | ValueSome _ -> false

let private entry (chatId: int64) (requestId: string) : PendingApproval =
    { Chat = chat chatId
      Request = requestContent requestId "send_email"
      Owner = Anyone
      MessageId = messageId 1L }

[<Tests>]
let pendingApprovalsTests =
    testList "PendingApprovals" [

        testCase "TryConsume returns the entry that was Added" <| fun _ ->
            let table = PendingApprovals()
            let e = entry 1L "req-1"
            table.Add e

            match table.TryConsume(chat 1L, "req-1") with
            | ValueSome got -> Expect.equal got.Request.RequestId "req-1" "the consumed entry is the one that was added"
            | ValueNone -> failwith "expected ValueSome"

        testCase "TryConsume on an unknown (chat, requestId) misses" <| fun _ ->
            let table = PendingApprovals()
            Expect.isTrue (isMiss (table.TryConsume(chat 1L, "nope"))) "nothing was ever added for this key"

        testCase "a second TryConsume for the same key misses — at most once" <| fun _ ->
            let table = PendingApprovals()
            table.Add(entry 1L "req-1")
            table.TryConsume(chat 1L, "req-1") |> ignore
            Expect.isTrue (isMiss (table.TryConsume(chat 1L, "req-1"))) "the entry was already consumed"

        testCase "the SAME requestId in two DIFFERENT chats are independent entries" <| fun _ ->
            let table = PendingApprovals()
            table.Add(entry 1L "req-shared")
            table.Add(entry 2L "req-shared")

            match table.TryConsume(chat 1L, "req-shared") with
            | ValueSome got -> Expect.equal got.Chat (chat 1L) "chat 1's own entry is returned for chat 1's key"
            | ValueNone -> failwith "expected ValueSome"

            match table.TryConsume(chat 2L, "req-shared") with
            | ValueSome got -> Expect.equal got.Chat (chat 2L) "chat 2's own entry is untouched by chat 1's consume"
            | ValueNone -> failwith "expected ValueSome"

        testCase "AbandonAllFor drains only the requested chat's entries" <| fun _ ->
            let table = PendingApprovals()
            table.Add(entry 1L "req-a")
            table.Add(entry 1L "req-b")
            table.Add(entry 2L "req-c")

            let drained = table.AbandonAllFor(chat 1L)

            Expect.equal (List.length drained) 2 "both of chat 1's entries were drained"
            Expect.isTrue (isMiss (table.TryConsume(chat 1L, "req-a"))) "chat 1's entries are gone after abandon"
            Expect.isTrue (isMiss (table.TryConsume(chat 1L, "req-b"))) "chat 1's entries are gone after abandon"

            match table.TryConsume(chat 2L, "req-c") with
            | ValueSome _ -> ()
            | ValueNone -> failwith "chat 2's entry must survive abandoning chat 1"

        testCase "AbandonAllFor on a chat with nothing pending returns an empty list" <| fun _ ->
            let table = PendingApprovals()
            Expect.isTrue (List.isEmpty (table.AbandonAllFor(chat 99L))) "nothing was ever pending for this chat"

        testCase "concurrent TryConsume calls for the same key deliver to exactly one caller" <| fun _ ->
            task {
                let table = PendingApprovals()
                table.Add(entry 1L "req-race")

                let attempts = 50
                let results = Array.zeroCreate<PendingApproval voption> attempts

                let tasks =
                    [| for i in 0 .. attempts - 1 -> Task.Run(fun () -> results[i] <- table.TryConsume(chat 1L, "req-race")) |]

                do! Task.WhenAll tasks

                let hits = results |> Array.filter (fun r -> r.IsSome) |> Array.length
                Expect.equal hits 1 "exactly one of the racing callers consumed the entry"
            }
            |> fun t -> t.GetAwaiter().GetResult()
    ]
