/// FsCheck property for `ToolKeyboardOps.deliver`'s stamping step: every tool binding one `deliver`
/// call produces carries the SAME send-time `owner`/`deniedNotice`/`expiresAt`/`singleUse` —
/// uniformly, regardless of how many tool buttons the plan has — while each binding's own
/// `ToolName`/`Arg` stay exactly what the input `PlanButton` specified. Complements the
/// hand-written example-based coverage already in `ToolDispatchProcessorTests.fs` (review #4's own
/// compensation test, the EditKeyboardAsync tests) with a property over an ARBITRARY plan shape and
/// arbitrary send-time options, rather than one fixed example.
module TgLLM.Core.Tests.ToolKeyboardOpsStampingTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open FsCheck
open FSharp.UMX
open TgLLM.Core

/// Bounds an FsCheck-generated `PositiveInt` to a small, test-fast row/button count (1..4) — same
/// convention as `ToolPlanTests.fs`.
let private toSmallCount (PositiveInt n) = (n % 4) + 1

/// Builds a plan of ALL tool buttons (deterministic, always-valid label/tool-name; `Arg` alternates
/// `Some`/`None` by position so both are exercised) — this property is about the STAMPING step
/// `deliver` applies uniformly, not about label/shape validation (already covered elsewhere), so
/// every input here is guaranteed to plan successfully.
let private buildRows (rowButtonCounts: int list) : PlanButton list list =
    rowButtonCounts
    |> List.mapi (fun rowIdx count ->
        [ for colIdx in 0 .. count - 1 ->
              let arg = if (rowIdx + colIdx) % 2 = 0 then Some $"arg{rowIdx}_{colIdx}" else None
              ToolButton($"r{rowIdx}b{colIdx}", $"tool{rowIdx}_{colIdx}", arg) ])

[<Tests>]
let toolKeyboardOpsStampingTests =
    testList "ToolKeyboardOps.deliver stamping [property]" [

        testProperty
            "deliver stamps every produced binding uniformly with the send-time owner/deniedNotice/expiresAt/singleUse, leaving token/name/arg otherwise unchanged"
        <| fun (data: NonEmptyArray<PositiveInt>) (ownerId: int64 option) (deniedNotice: string option) (expirySeconds: int64 option) (singleUse: bool) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount |> List.truncate 10
            let rows = buildRows rowButtonCounts
            let plan: ToolKeyboard = { Rows = rows }

            let owner =
                match ownerId with
                | None -> Anyone
                | Some uid -> User(UMX.tag<userId> uid)

            let expiresAt = expirySeconds |> Option.map (fun s -> DateTimeOffset.UnixEpoch.AddSeconds(float (s % 1_000_000_000L)))

            let store = InMemoryBindingStore() :> IBindingStore
            let tracker = MessageBindingTracker()
            let chat = UMX.tag<chatId> 1L
            let send (_keyboard: RegisteredKeyboard) : Task<MessageId> = task { return UMX.tag<messageId> 1L }

            let deliverTask =
                ToolKeyboardOps.deliver
                    "property-test"
                    CallbackToken.generate
                    store
                    tracker
                    chat
                    None
                    owner
                    deniedNotice
                    expiresAt
                    singleUse
                    send
                    CancellationToken.None
                    plan

            let messageId = deliverTask.GetAwaiter().GetResult()

            let savedTokens = tracker.TryGetPrevious(chat, messageId) |> Option.defaultValue []

            let savedBindings =
                savedTokens
                |> List.map (fun token ->
                    match (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult() with
                    | ValueSome b -> b
                    | ValueNone -> failwith "test invariant violated: a just-saved binding's token must resolve")

            // Every tool button in the input plan, in the SAME row/column order `ToolPlan.plan`
            // itself produces bindings in (verified separately by `ToolPlanTests.fs`).
            let toolButtons =
                rows
                |> List.collect (
                    List.choose (function
                        | ToolButton(_, toolName, arg) -> Some(toolName, arg)
                        | UrlButton _
                        | WebAppButton _
                        | CopyTextButton _ -> None)
                )

            let sameCount = List.length savedBindings = List.length toolButtons

            let stampingUniform =
                savedBindings
                |> List.forall (fun b -> b.Owner = owner && b.DeniedNotice = deniedNotice && b.ExpiresAt = expiresAt && b.SingleUse = singleUse)

            let nameArgUnchanged =
                List.forall2
                    (fun (binding: ToolBinding) (toolName, arg) -> ToolName.value binding.ToolName = toolName && binding.Arg = arg)
                    savedBindings
                    toolButtons

            sameCount && stampingUniform && nameArgUnchanged
    ]
