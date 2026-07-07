/// Tests for the tool registry's manifest emission at the F# façade: `ToolRegistry.Register`'s
/// optional `description`/`argSchema`, the structured `Manifest()`, and the neutral wire JSON
/// `ManifestJson()` — a plain `[{ name, description, parameters }]` array with no vendor wrapper.
module TgLLM.Integration.Tests.ManifestTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open TgLLM.Core
open TgLLM.FSharp

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private hasField (key: string) (node: JsonNode) : bool = node.[key] |> Option.ofObj |> Option.isSome

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private noopHandler (_: PressContext) : Task<unit> = Task.FromResult(())

let private parseArray (json: string) : JsonArray =
    match JsonNode.Parse json |> Option.ofObj with
    | Some(:? JsonArray as array) -> array
    | Some other -> failwithf "expected a JSON array, got %A" other
    | None -> failtest "expected ManifestJson() to parse to non-null JSON"

let private entryNamed (name: string) (array: JsonArray) : JsonNode =
    [ 0 .. array.Count - 1 ]
    |> List.choose (fun i -> array.[i] |> Option.ofObj)
    |> List.find (fun e -> (e |> field "name" |> asString) = name)

[<Tests>]
let manifestTests =
    testList "Tool manifest (F# façade)" [

        testCase "Manifest() lists every registered tool, carrying metadata only for the ones that supplied it" <| fun _ ->
            let registry =
                ToolRegistry
                    .create()
                    .Register("approve", noopHandler, description = "Approves a request", argSchema = """{"type":"object"}""")
                    .Register("reject", noopHandler)

            let entries = registry.Manifest().Tools |> List.map (fun e -> e.Name, e.Description, e.Parameters)

            Expect.contains
                entries
                ("approve", Some "Approves a request", Some """{"type":"object"}""")
                "the metadata-carrying tool appears with its description and schema"

            Expect.contains entries ("reject", None, None) "the metadata-less tool appears name-only"

        testCase "ManifestJson() produces a neutral array with no vendor wrapper" <| fun _ ->
            let registry =
                ToolRegistry
                    .create()
                    .Register("approve", noopHandler, description = "Approves a request", argSchema = """{"type":"object"}""")
                    .Register("reject", noopHandler)

            let array = parseArray (registry.ManifestJson())
            Expect.equal array.Count 2 "every registered tool appears exactly once"

            let approveEntry = array |> entryNamed "approve"
            Expect.equal (approveEntry |> field "description" |> asString) "Approves a request" "the description reached the wire"

            Expect.equal
                (approveEntry |> field "parameters" |> field "type" |> asString)
                "object"
                "a JSON-Schema-shaped argSchema is embedded as raw JSON, not double-encoded as a string"

            let rejectEntry = array |> entryNamed "reject"
            Expect.isFalse (rejectEntry |> hasField "description") "a metadata-less tool omits 'description' entirely"
            Expect.isFalse (rejectEntry |> hasField "parameters") "a metadata-less tool omits 'parameters' entirely"

        testCase "a non-JSON argSchema is embedded as a plain JSON string, not raw-spliced" <| fun _ ->
            let registry = ToolRegistry.create().Register("escalate", noopHandler, argSchema = "free-form text, not JSON")
            let entry = parseArray (registry.ManifestJson()) |> entryNamed "escalate"

            Expect.equal
                (entry |> field "parameters" |> asString)
                "free-form text, not JSON"
                "a non-JSON schema string is carried as a JSON string value, not parsed"

        testCase "an empty registry's ManifestJson() is an empty array" <| fun _ ->
            let registry = ToolRegistry.create()
            Expect.equal (registry.ManifestJson()) "[]" "no tools registered means an empty neutral array"
    ]
