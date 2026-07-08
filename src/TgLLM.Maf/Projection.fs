namespace TgLLM.Maf

open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.AI
open TgLLM.Core
open TgLLM.FSharp

/// Projects a MAF-declared `AIFunction` into a `ToolRegistry` tool: field mapping per
/// `AIFunction.Name`/`.Description`/`.JsonSchema` (probe-confirmed against the pinned 1.13.0/
/// 10.6.0 binaries — `AIFunctionFactory.Create` performs no name validation of its own, so an
/// empty/whitespace `.Name` is a real condition this module must surface, not an unreachable
/// case), and a registered handler that parses the tapped button's structured JSON-object
/// argument into an `AIFunctionArguments` and invokes the function, replying its
/// JSON-serialized result. Standalone — usable without any `MafBridge`/bot, given only a
/// `ToolRegistry`.
module MafTools =

    /// Parses `raw` (the tapped button's `ToolBinding.Arg`, conventionally a JSON object) into
    /// the dictionary `AIFunctionArguments` wraps. `null`/empty/whitespace `raw` — a legitimate
    /// no-argument tap — yields `Ok` of an EMPTY argument set. Anything else that fails to parse as
    /// a JSON OBJECT (malformed JSON, or well-formed JSON that isn't an object — an array, a bare
    /// number, ...) is `Error ()`: a MALFORMED payload is a reason to REFUSE the tap, not to
    /// silently invoke the function with whatever partial/empty argument set happened to result
    /// (the same "refuse malformed rather than degrade-and-proceed" stance
    /// `ApprovalDescriptor.tryParse`'s own callers take for a decision button). Each property's
    /// value is carried as its raw `JsonElement` — the same shape MAF's own LLM-driven
    /// function-calling loop hands `AIFunctionArguments` (a `JsonElement` per argument, converted
    /// to the target parameter type by `AIFunction.InvokeAsync` itself via its own
    /// `JsonSerializerOptions`), reflection-confirmed against `AIFunctionFactory.Create`'s
    /// delegate-wrapping functions — so a projected tool binds arguments identically whether MAF's
    /// own model loop or a Telegram button supplied them.
    let private parseArguments (raw: string | null) : Result<AIFunctionArguments, unit> =
        let emptyArguments () = AIFunctionArguments(Dictionary<string, obj | null>() :> IDictionary<string, obj | null>)

        match Option.ofObj raw with
        | None -> Ok(emptyArguments ())
        | Some json when System.String.IsNullOrWhiteSpace json -> Ok(emptyArguments ())
        | Some json ->
            try
                use document = JsonDocument.Parse json

                if document.RootElement.ValueKind = JsonValueKind.Object then
                    let dict = Dictionary<string, obj | null>()

                    for prop in document.RootElement.EnumerateObject() do
                        dict[prop.Name] <- box (prop.Value.Clone())

                    Ok(AIFunctionArguments(dict :> IDictionary<string, obj | null>))
                else
                    Error ()
            with :? JsonException ->
                Error ()

    /// The registered `Tool` for one projected `AIFunction`: parse the tap's arg, invoke, reply
    /// the JSON-serialized result. A malformed arg (`parseArguments`'s `Error`) never reaches
    /// `AIFunction.InvokeAsync` at all — the function is skipped and a plain-text refusal is
    /// replied instead, so a bad payload can never silently run the function with a wrong/empty
    /// argument set. An invalid reply (over the Bot API length limit) is a programmer/tool-output
    /// error (Always-Rule 6) — `PressContext.ReplyTextAsync` already fails fast on that for every
    /// other tool in this library, so this is not a condition this leaf's `IMafObserver` surfaces
    /// separately.
    let private invoke (f: AIFunction) : Tool =
        fun (ctx: PressContext) ->
            task {
                match parseArguments ctx.Arg with
                | Error () ->
                    let! _ = ctx.ReplyTextAsync $"⚠ {f.Name}: the tapped button's arguments could not be parsed — the tool was not run."
                    ()
                | Ok arguments ->
                    let! result = f.InvokeAsync(arguments, ctx.CancellationToken)
                    let! _ = ctx.ReplyTextAsync(JsonSerializer.Serialize result)
                    ()
            }
            :> Task

    /// One function's projection outcome: `Ok` carries the tool's validated name, its registered
    /// `Tool`, and the manifest metadata to register it under; `Error` is the surfaced
    /// `ProjectionProblem` — the declaration's own invalid name, a name that collides with one of
    /// this bridge's OWN reserved decision-tool names (`ReservedName`, checked FIRST: registering
    /// either would silently override `maf-approve`/`maf-reject`'s own handler, breaking the whole
    /// approval loop — a strictly worse outcome than an ordinary duplicate, so it is refused even
    /// before the duplicate-name check below runs), or a name that repeats an EARLIER function
    /// already seen in THIS SAME projected sequence (`seenNames` is threaded through the whole
    /// `Seq.map` fold below, so only the FIRST occurrence of a name ever succeeds).
    let private projectOne (seenNames: HashSet<string>) (f: AIFunction) : Result<ToolName * Tool * ToolMetadata, ProjectionProblem> =
        match ToolName.create f.Name with
        | Error e -> Error(InvalidToolName(f.Name, e))
        | Ok toolName ->
            let name = ToolName.value toolName

            if name = ReservedToolNames.Approve || name = ReservedToolNames.Reject then
                Error(ReservedName name)
            elif not (seenNames.Add name) then
                Error(DuplicateName name)
            else
                // `AITool.Description` is a non-nullable `string` per the resolved binaries' own
                // nullability annotations — still defensively checked for blank content (empty or
                // whitespace-only, e.g. an `AIFunctionFactory.Create` caller that passed `""`),
                // not for `null` (which the type system already rules out here).
                let description =
                    if System.String.IsNullOrWhiteSpace f.Description then
                        None
                    else
                        Some f.Description

                let metadata: ToolMetadata =
                    { Description = description
                      ArgSchema = Some(f.JsonSchema.GetRawText()) }

                Ok(toolName, invoke f, metadata)

    /// Registers every projectable function into `registry`, in order; a per-function problem
    /// (an invalid declared name, or a name repeated within THIS `functions` sequence) is
    /// collected AND mirrored to `observer` — its valid siblings still register (never
    /// all-or-nothing, mirroring the A2UI leaf's "supported siblings still render" policy).
    let projectWith (observer: IMafObserver) (registry: ToolRegistry) (functions: AIFunction seq) : ProjectionReport =
        let seenNames = HashSet<string>()
        let registered = ResizeArray<string>()
        let problems = ResizeArray<ProjectionProblem>()

        for f in functions do
            match projectOne seenNames f with
            | Ok(toolName, tool, metadata) ->
                registry.Registry.Register(toolName, tool, metadata)
                registered.Add(ToolName.value toolName)
            | Error problem ->
                problems.Add problem
                observer.OnProjectionProblem problem

        { Registered = List.ofSeq registered
          Problems = List.ofSeq problems }

    /// Zero-config variant of `projectWith`: problems are still collected in the returned report
    /// (nothing is ever silently lost), just not mirrored anywhere else — the caller that has no
    /// bridge/observer to mirror to reads `ProjectionReport.Problems` directly.
    let project (registry: ToolRegistry) (functions: AIFunction seq) : ProjectionReport =
        projectWith (NoopMafObserver() :> IMafObserver) registry functions

/// C#-facing outcome of `MafTools.Project` — plain `IReadOnlyList<string>` collections, no F#
/// idioms. `Problems` flattens each `ProjectionProblem` to a human-readable string (mirrors
/// `MafSurfacedEvent.Description`'s own flatten-for-C# convention, `CSharpSurface.fs`) rather than
/// exposing the F# union directly.
[<Sealed>]
type ToolProjectionResult(registered: IReadOnlyList<string>, problems: IReadOnlyList<string>) =
    member _.Registered: IReadOnlyList<string> = registered
    member _.Problems: IReadOnlyList<string> = problems

/// C#-facing static entry point over the F# `MafTools.project` above — a `type` of the SAME simple
/// name as that F# `module`, deliberately: F# resolves the module's lowercase `project` and this
/// type's uppercase `Project` as distinct members with no ambiguity, and the compiler auto-applies
/// its own `ModuleSuffix` collision handling for the underlying IL class names, so both remain
/// reachable under `TgLLM.Maf.MafTools` from either language. MUST live in this SAME file as the
/// `module MafTools` above — the F# compiler rejects a module and a type of the same name declared
/// in two DIFFERENT files of the same assembly (`error FS0250`), even though it accepts them
/// declared together in one file; this is why this type is here rather than alongside every other
/// C#-facing type in `CSharpSurface.fs`.
[<Sealed>]
type MafTools =
    /// Registers every projectable declaration into `registry`, in order — the C#-facing
    /// counterpart to `MafTools.project`. Standalone: usable without any `MafTelegramBridge`/bot,
    /// given only a `TgLLM.FSharp.ToolRegistry` (already a C#-callable type — see
    /// `MafTelegramBridge.StartPollingAsync`'s own `TgBotConfig` parameter for the same
    /// already-accepted precedent of reusing an F#-façade type directly on this leaf's C# surface).
    static member Project(registry: ToolRegistry, functions: AIFunction seq) : ToolProjectionResult =
        let report = MafTools.project registry functions
        let problems = report.Problems |> List.map (fun p -> $"%A{p}") |> ResizeArray :> IReadOnlyList<string>
        ToolProjectionResult(ResizeArray report.Registered :> IReadOnlyList<string>, problems)
