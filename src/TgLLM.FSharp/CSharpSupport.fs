/// C#-facing helpers used by the `TgLLM.CSharp` façade. They keep F# curried functions, F# list
/// construction, and single-case-type unwrapping OUT of the C# call site so the C# package can stay
/// idiomatic (Principle II). This is NOT part of the idiomatic F# public surface.
namespace TgLLM.FSharp

open System
open System.Collections.Generic
open System.Threading.Tasks
open TgLLM.Core

type Keyboards =

    /// Build a validated keyboard from C# rows of (label, Core hook). Validation errors surface as
    /// the F# `Result` the C# façade turns into a `TgKeyboardException`.
    static member Build
        (rows: IReadOnlyList<IReadOnlyList<ValueTuple<string, Func<PressContext, Task>>>>)
        : Result<KeyboardSpec, KeyboardError> =
        [ for row in rows ->
              [ for pair in row ->
                    let struct (label, handler) = pair
                    ({ Label = label; Hook = (fun ctx -> handler.Invoke ctx) }: TgLLM.Core.ButtonSpec) ] ]
        |> TgLLM.Core.Keyboard.create

module CSharpSupport =

    /// The tapped button's visible label as a plain string (unwraps the F# `ButtonLabel`).
    let buttonLabelText (ctx: PressContext) : string = ButtonLabel.value ctx.ButtonLabel

/// C#-facing bridge for Tool Router registration: keeps F# curried functions and `ToolName`'s
/// smart constructor OUT of the C# call site, mirroring `Keyboards.Build`'s role for slice-1's
/// keyboard builder.
type ToolRegistrations =

    /// A fresh in-memory `IToolRegistry`, for callers that want the raw core port directly
    /// (the C# façade instead wraps `TgLLM.FSharp.ToolRegistry`, see `ToolRegistry.cs`).
    static member CreateInMemory() : IToolRegistry = InMemoryToolRegistry() :> IToolRegistry

    /// Registers a C#-friendly `Func<PressContext, Task>` handler (a BCL delegate, not an
    /// FSharpFunc) against `registry`. `description`/`argSchema` are advisory manifest metadata —
    /// see `TgLLM.Core.ToolMetadata`'s own doc comment; omitting both registers exactly as before.
    /// An invalid (empty-after-trim) `name` is a programmer error by the host (Always-Rule 6), so
    /// it throws rather than returning a `Result` the C# façade would have to unwrap on every
    /// registration call.
    ///
    /// `description`/`argSchema` are normalized through `Option.bind Option.ofObj` before use: F#'s
    /// "omitted argument becomes `None`" convenience is a source-level feature of the CALLER's own
    /// F# code — a C# caller has no such sugar and always passes an explicit value, so a `null` it
    /// passes for either optional parameter arrives here as `Some null`, not `None`. Collapsing
    /// `Some null` to `None` (leaving a genuine `Some "text"` untouched) is what makes "the C#
    /// caller didn't set this" behave like "omitted" instead of silently storing a null
    /// description/schema.
    static member Register
        (
            registry: IToolRegistry,
            name: string,
            handler: Func<PressContext, Task>,
            ?description: string | null,
            ?argSchema: string | null
        ) : unit =
        match ToolName.create name with
        | Ok toolName ->
            let description = description |> Option.bind Option.ofObj
            let argSchema = argSchema |> Option.bind Option.ofObj

            let metadata =
                match description, argSchema with
                | None, None -> None
                | _ -> Some { Description = description; ArgSchema = argSchema }

            registry.Register(toolName, (fun ctx -> handler.Invoke ctx), ?metadata = metadata)
        | Error e -> invalidArg (nameof name) $"invalid tool name ({e})"

/// C#-facing bridge for building a neutral Tool Router plan: the C# façade's
/// `PlanRowBuilder.Tool`/`.Url` call `Plan.tool`/`Plan.toolWithArg`/`Plan.url` directly (plain
/// strings in, a `PlanButton` out — no bridge needed for those), then `PlanBuilder.Build` calls
/// this to turn C#-shaped rows into the validated `ToolKeyboard`.
type ToolPlans =
    static member BuildRows(rows: IReadOnlyList<IReadOnlyList<PlanButton>>) : Result<ToolKeyboard, ToolError> =
        [ for row in rows -> List.ofSeq row ] |> Plan.rows
