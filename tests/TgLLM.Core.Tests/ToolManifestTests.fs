/// Tests for the tool registry's manifest emission: the neutral self-description a host feeds to
/// an LLM's function-calling API. Emission here is purely structured — no JSON. Turning a
/// `ToolManifest` into wire JSON is a façade concern (System.Text.Json never appears in Core).
module TgLLM.Core.Tests.ToolManifestTests

open System.Threading.Tasks
open Expecto
open TgLLM.Core

let private name (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

let private noop: Tool = fun _ -> Task.CompletedTask

[<Tests>]
let toolManifestTests =
    testList "IToolRegistry.Manifest" [

        testCase "a tool registered with a description and an argument schema appears with both" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry

            registry.Register(
                name "approve",
                noop,
                metadata = { Description = Some "Approves a pending request"; ArgSchema = Some """{"type":"object"}""" }
            )

            match registry.Manifest() with
            | { Tools = [ entry ] } ->
                Expect.equal entry.Name "approve" "the registered name is carried"
                Expect.equal entry.Description (Some "Approves a pending request") "the description is carried"
                Expect.equal entry.Parameters (Some """{"type":"object"}""") "the argument schema is carried under the neutral Parameters field"
            | other -> failwithf "expected exactly one manifest entry, got %A" other

        testCase "a tool registered with no metadata appears name-only" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(name "reject", noop)

            match registry.Manifest() with
            | { Tools = [ entry ] } ->
                Expect.equal entry.Name "reject" "the registered name is still carried"
                Expect.equal entry.Description None "no description was supplied at registration"
                Expect.equal entry.Parameters None "no argument schema was supplied at registration"
            | other -> failwithf "expected exactly one manifest entry, got %A" other

        testCase "every registered tool is present in the manifest regardless of metadata" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(name "approve", noop, metadata = { Description = Some "d1"; ArgSchema = None })
            registry.Register(name "reject", noop)
            registry.Register(name "escalate", noop, metadata = { Description = None; ArgSchema = Some "{}" })

            let names = registry.Manifest().Tools |> List.map (fun t -> t.Name) |> Set.ofList
            Expect.equal names (Set.ofList [ "approve"; "reject"; "escalate" ]) "every registered tool appears in the manifest"

        testCase "manifest order is the registration order and stable across repeated calls" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(name "first", noop)
            registry.Register(name "second", noop)
            registry.Register(name "third", noop)

            let namesA = registry.Manifest().Tools |> List.map (fun t -> t.Name)
            let namesB = registry.Manifest().Tools |> List.map (fun t -> t.Name)

            Expect.equal namesA [ "first"; "second"; "third" ] "tools appear in the order they were registered"
            Expect.equal namesA namesB "calling Manifest() twice yields the same order"

        testCase "re-registering an existing name replaces its metadata without duplicating the entry" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(name "approve", noop)
            registry.Register(name "approve", noop, metadata = { Description = Some "updated"; ArgSchema = None })

            match registry.Manifest().Tools with
            | [ entry ] ->
                Expect.equal entry.Name "approve" "still exactly one entry for the re-registered name"
                Expect.equal entry.Description (Some "updated") "re-registering replaces the previously stored metadata"
            | other -> failwithf "expected exactly one manifest entry after re-registration, got %A" other

        testCase "an empty registry emits an empty manifest" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            Expect.equal (registry.Manifest().Tools) [] "no tools registered means no manifest entries"
    ]
