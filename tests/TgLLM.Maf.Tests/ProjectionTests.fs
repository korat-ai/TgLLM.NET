/// Tests for `MafTools.project`/`projectWith`: the `AIFunction` -> `ToolRegistry` field mapping
/// (name/description/schema), manifest parity, and per-function surfacing of an invalid or
/// duplicate declaration while its valid siblings still register.
module TgLLM.Maf.Tests.ProjectionTests

open Expecto
open FsCheck
open Microsoft.Extensions.AI
open TgLLM.FSharp
open TgLLM.Maf

/// A trivial delegate every generated `AIFunction` wraps — the projection only ever reads
/// `Name`/`Description`/`JsonSchema` off the declaration, never invokes it, so the delegate's own
/// behavior is irrelevant to every test below except the dedicated invocation test elsewhere
/// (`MafProjectionInvokeTests`, `TgLLM.Integration.Tests`).
let private echo = System.Func<string, string>(id)

/// A well-formed `AIFunction` with a deterministic name (from an FsCheck seed) and an arbitrary
/// description — FsCheck's plain `string` arbitrary can produce `null` (as
/// `ApprovalDescriptorTests`/`MarkdownTests` already rely on elsewhere in this solution), so a
/// `null` seed is normalized to `""` here before it ever reaches `AIFunctionFactory.Create`
/// (mirroring `MafTools.project`'s own `Option.ofObj`-free, `IsNullOrWhiteSpace`-based defensive
/// handling of `AITool.Description`). `AIFunctionFactory.Create` performs no name validation of
/// its own (reflection-confirmed: an empty/whitespace name is accepted and carried straight
/// through to `.Name`), so THIS generator only ever produces names that are already well-formed
/// by `ToolName.create`'s own rule; the invalid/duplicate-name edge cases below construct their
/// own `AIFunction`s directly with deliberately bad names.
let private toFunction (nameSeed: NonNegativeInt, description: string | null) : AIFunction =
    let (NonNegativeInt seed) = nameSeed
    let desc = description |> Option.ofObj |> Option.defaultValue ""
    AIFunctionFactory.Create(echo, $"tool_{seed}", desc, null)

[<Tests>]
let projectionTests =
    testList "MafTools.project" [

        testProperty "Name maps to the registry's ToolName, for any well-formed declaration" <| fun (seed: NonNegativeInt, description: string | null) ->
            let f = toFunction (seed, description)
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry [ f ]
            let manifestNames = registry.Manifest().Tools |> List.map (fun e -> e.Name)
            report.Registered = [ f.Name ] && manifestNames = [ f.Name ] && List.isEmpty report.Problems

        testProperty "Description maps to ToolMetadata.Description; empty/whitespace maps to None" <| fun (seed: NonNegativeInt, description: string | null) ->
            let f = toFunction (seed, description)
            let registry = ToolRegistry.create ()
            MafTools.project registry [ f ] |> ignore
            let entry = registry.Manifest().Tools |> List.find (fun e -> e.Name = f.Name)
            let expected = if System.String.IsNullOrWhiteSpace f.Description then None else Some f.Description
            entry.Description = expected

        testProperty "JsonSchema.GetRawText() maps to ToolMetadata.ArgSchema verbatim" <| fun (seed: NonNegativeInt, description: string | null) ->
            let f = toFunction (seed, description)
            let registry = ToolRegistry.create ()
            MafTools.project registry [ f ] |> ignore
            let entry = registry.Manifest().Tools |> List.find (fun e -> e.Name = f.Name)
            entry.Parameters = Some(f.JsonSchema.GetRawText())

        testCase "a whitespace-only declared name is surfaced as InvalidToolName, not registered; a valid sibling still registers" <| fun _ ->
            let bad = AIFunctionFactory.Create(echo, "   ", "desc", null)
            let good = AIFunctionFactory.Create(echo, "good_tool", "desc", null)
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry [ bad; good ]

            Expect.equal report.Registered [ "good_tool" ] "only the valid sibling registers"

            match report.Problems with
            | [ InvalidToolName(name, _) ] -> Expect.equal name "   " "the offending raw name is reported"
            | other -> failtestf "expected exactly one InvalidToolName problem, got %A" other

        testCase "an empty declared name is surfaced as InvalidToolName, not registered" <| fun _ ->
            let bad = AIFunctionFactory.Create(echo, "", "desc", null)
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry [ bad ]

            Expect.isEmpty report.Registered "nothing registers"

            match report.Problems with
            | [ InvalidToolName(name, _) ] -> Expect.equal name "" "the offending raw name is reported"
            | other -> failtestf "expected exactly one InvalidToolName problem, got %A" other

        testCase "a name repeated WITHIN one projected set is surfaced as DuplicateName; the FIRST declaration still registers" <| fun _ ->
            let first = AIFunctionFactory.Create(echo, "send_email", "first", null)
            let second = AIFunctionFactory.Create(echo, "send_email", "second", null)
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry [ first; second ]

            Expect.equal report.Registered [ "send_email" ] "only the FIRST declaration registers"

            match report.Problems with
            | [ DuplicateName "send_email" ] -> ()
            | other -> failtestf "expected exactly one DuplicateName problem, got %A" other

            let entry = registry.Manifest().Tools |> List.exactlyOne
            Expect.equal entry.Description (Some "first") "the FIRST declaration's own description won, not the duplicate's"

        testCase "a declared function named maf-approve is refused as ReservedName, never registered — it would silently override the approval loop's own handler" <| fun _ ->
            let bad = AIFunctionFactory.Create(echo, "maf-approve", "a rogue declaration", null)
            let good = AIFunctionFactory.Create(echo, "good_tool", "desc", null)
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry [ bad; good ]

            Expect.equal report.Registered [ "good_tool" ] "only the valid sibling registers — the reserved name never does"

            match report.Problems with
            | [ ReservedName "maf-approve" ] -> ()
            | other -> failtestf "expected exactly one ReservedName problem for 'maf-approve', got %A" other

            Expect.isEmpty
                (registry.Manifest().Tools |> List.filter (fun e -> e.Name = "maf-approve"))
                "the registry's own manifest never carries an entry under the reserved name"

        testCase "a declared function named maf-reject is refused as ReservedName" <| fun _ ->
            let bad = AIFunctionFactory.Create(echo, "maf-reject", "a rogue declaration", null)
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry [ bad ]

            Expect.isEmpty report.Registered "nothing registers"

            match report.Problems with
            | [ ReservedName "maf-reject" ] -> ()
            | other -> failtestf "expected exactly one ReservedName problem for 'maf-reject', got %A" other

        testCase "distinct valid siblings all register alongside an invalid/duplicate one" <| fun _ ->
            let a = AIFunctionFactory.Create(echo, "alpha", "", null)
            let bad = AIFunctionFactory.Create(echo, "", "", null)
            let b = AIFunctionFactory.Create(echo, "beta", "", null)
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry [ a; bad; b ]

            Expect.equal (Set.ofList report.Registered) (Set.ofList [ "alpha"; "beta" ]) "both valid siblings register despite the invalid one between them"
            Expect.equal (List.length report.Problems) 1 "exactly one problem was surfaced"

        testCase "every problem is mirrored to IMafObserver.OnProjectionProblem via projectWith" <| fun _ ->
            let bad = AIFunctionFactory.Create(echo, "", "desc", null)
            let dup1 = AIFunctionFactory.Create(echo, "dup", "one", null)
            let dup2 = AIFunctionFactory.Create(echo, "dup", "two", null)
            let observed = ResizeArray<ProjectionProblem>()

            let observer =
                { new IMafObserver with
                    member _.OnStaleDecision(_) = ()
                    member _.OnMalformedDecision(_) = ()
                    member _.OnResumeFailed(_, _) = ()
                    member _.OnEmptyTurn(_) = ()
                    member _.OnInvalidOutput(_, _) = ()
                    member _.OnProjectionProblem(p) = observed.Add p
                    member _.OnTurnFailed(_, _) = () }

            let registry = ToolRegistry.create ()
            let report = MafTools.projectWith observer registry [ bad; dup1; dup2 ]

            Expect.equal observed.Count 2 "both surfaced problems were mirrored"
            Expect.equal (List.ofSeq observed) report.Problems "the mirrored problems match the returned report, in order"

        testCase "MafTools.project (no observer) never throws for any input, including all-invalid sets" <| fun _ ->
            let allBad = [ AIFunctionFactory.Create(echo, "", "", null); AIFunctionFactory.Create(echo, "   ", "", null) ]
            let registry = ToolRegistry.create ()
            let report = MafTools.project registry allBad
            Expect.isEmpty report.Registered "nothing registers when every declaration is invalid"
            Expect.equal (List.length report.Problems) 2 "both invalid declarations are surfaced"

        testCase "manifest parity: a full declaration's name/description/schema all match verbatim" <| fun _ ->
            let f = AIFunctionFactory.Create(echo, "send_email", "Sends an email", null)
            let registry = ToolRegistry.create ()
            MafTools.project registry [ f ] |> ignore
            let entry = registry.Manifest().Tools |> List.exactlyOne
            Expect.equal entry.Name f.Name "name parity"
            Expect.equal entry.Description (Some f.Description) "description parity"
            Expect.equal entry.Parameters (Some(f.JsonSchema.GetRawText())) "schema parity"
    ]
