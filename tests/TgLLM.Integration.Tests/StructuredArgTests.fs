/// Structured-argument round-trip through the F# façade: `Plan.toolWith<'T>` serializes a typed
/// payload into a button's bound argument; `PressContext.GetArg<'T>()`/`TryGetArg<'T>()` deserialize
/// it back out. A plain string argument built via `Plan.toolWithArg` keeps routing unchanged through
/// the existing `.Arg` accessor — the structured payload generalizes it, it does not replace it.
module TgLLM.Integration.Tests.StructuredArgTests

open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp

type private ApprovalRequest = { Id: int; Reason: string }

let private sampleUser: EndUser =
    { Id = UMX.tag<userId> 1L
      FirstName = "Test"
      Username = null }

let private validLabel: ButtonLabel =
    match ButtonLabel.create "b" with
    | Ok l -> l
    | Error e -> failwithf "test setup: unreachable %A" e

/// A minimal, real `PressContext` carrying `arg` as its bound argument — these tests only care
/// what `GetArg`/`TryGetArg`/`Arg` observe, not the rest of the press's fields.
let private pressContextWithArg (arg: string option) : PressContext =
    let replyTextAsync = fun (_: string) -> Task.FromResult(UMX.tag<messageId> 0L)

    match arg with
    | Some a -> PressContext(validLabel, UMX.tag<chatId> 1L, sampleUser, UMX.tag<messageId> 1L, CancellationToken.None, replyTextAsync, arg = a)
    | None -> PressContext(validLabel, UMX.tag<chatId> 1L, sampleUser, UMX.tag<messageId> 1L, CancellationToken.None, replyTextAsync)

/// Plans `button` alone, runs it through `ToolPlan.plan`, and returns its single binding's `Arg` —
/// the exact opaque string a real send/press round-trip would carry.
let private bindingArgFor (button: PlanButton) : string option =
    match ToolPlan.plan (Seq.initInfinite (fun _ -> CallbackToken.generate ())) { Rows = [ [ button ] ] } with
    | Ok(_, [ binding ]) -> binding.Arg
    | other -> failwithf "expected exactly one tool binding, got %A" other

[<Tests>]
let structuredArgTests =
    testList "Structured arguments (Plan.toolWith / PressContext.GetArg)" [

        testCase "a structured payload round-trips plan -> binding -> GetArg<'T>() byte-for-byte" <| fun _ ->
            let original = { Id = 42; Reason = "looks good" }
            let button = Plan.toolWith "Approve" "approve" original
            let ctx = pressContextWithArg (bindingArgFor button)

            Expect.equal (ctx.GetArg<ApprovalRequest>()) original "the exact payload comes back out"

        testCase "TryGetArg<'T>() returns Some for a valid structured payload" <| fun _ ->
            let original = { Id = 7; Reason = "ship it" }
            let button = Plan.toolWith "Approve" "approve" original
            let ctx = pressContextWithArg (bindingArgFor button)

            Expect.equal (ctx.TryGetArg<ApprovalRequest>()) (Some original) "TryGetArg round-trips the same value as GetArg"

        testCase "TryGetArg<'T>() returns None, never throws, for a payload that is not valid JSON for 'T" <| fun _ ->
            let ctx = pressContextWithArg (Some "not valid json for the target type")
            Expect.equal (ctx.TryGetArg<ApprovalRequest>()) None "a shape mismatch is reported as None, not an exception"

        testCase "TryGetArg<'T>() returns None for an argument-less press" <| fun _ ->
            let ctx = pressContextWithArg None
            Expect.equal (ctx.TryGetArg<ApprovalRequest>()) None "no argument at all is also None"

        testCase "GetArg<'T>() fails fast for an argument-less press rather than returning a default value" <| fun _ ->
            let ctx = pressContextWithArg None

            try
                ctx.GetArg<ApprovalRequest>() |> ignore
                failtest "expected GetArg to fail fast on an argument-less press"
            with :? System.InvalidOperationException -> ()

        testCase "a slice-2 plain string argument (Plan.toolWithArg) still reads via .Arg unchanged" <| fun _ ->
            let button = Plan.toolWithArg "Approve" "approve" "raw-string-arg"
            let ctx = pressContextWithArg (bindingArgFor button)

            Expect.equal ctx.Arg "raw-string-arg" "a plain slice-2 string argument still routes through .Arg unchanged"
    ]
