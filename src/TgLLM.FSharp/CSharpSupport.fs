/// C#-facing helpers used by the `TgLLM.CSharp` façade. They keep F# curried functions, F# list
/// construction, and single-case-type unwrapping OUT of the C# call site so the C# package can stay
/// idiomatic (Principle II). This is NOT part of the idiomatic F# public surface.
namespace TgLLM.FSharp

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open TgLLM.Core
open TgLLM.A2UI

/// C#-facing bridge for `TgLLM.A2UI.A2uiAction`: unwraps its `string option`/JSON-typed context
/// values into plain BCL shapes the C# façade's `A2uiRenderer` can build its own DTO from without
/// touching `FSharpOption`/`JsonNode` pattern matching itself.
module A2uiActionBridge =

    /// One resolved context entry, stringified: an already-string JSON leaf passes through as-is;
    /// any other JSON shape (number/bool/object/array) uses its JSON text; an unresolved context
    /// path (`None`) is the empty string — the same "never a throw, always some string" contract
    /// `DynString.resolve` uses for bound text/labels. A `struct` tuple, not `System.Tuple`, so a
    /// C# `foreach (var (key, value) in ...)` deconstructs it natively.
    let private contextEntry (key: string, value: JsonNode option) : struct (string * string) =
        match value with
        | None -> struct (key, "")
        | Some(:? JsonValue as v) when v.GetValueKind() = JsonValueKind.String -> struct (key, v.GetValue<string>())
        | Some node -> struct (key, node.ToJsonString())

    /// `action.Context`, stringified and as a plain array — see `contextEntry`.
    let contextEntries (action: A2uiAction) : struct (string * string) array =
        action.Context |> List.map contextEntry |> List.toArray

    /// `action.ActionId` as a nullable `string`, never `FSharpOption`.
    let actionId (action: A2uiAction) : string | null = action.ActionId |> Option.toObj

/// C#-facing bridge for `TgLLM.A2UI.A2uiError`: turns the DU itself into two plain strings, so a C#
/// observer callback (`A2uiRenderer.Create`'s `onError`) can discriminate the condition
/// programmatically (`kind`) without ever pattern-matching an F# union, and still read a
/// human-readable message (`description`) for logging.
module A2uiErrorBridge =

    /// The DU case name — a stable, plain-string tag a C# host can switch on
    /// (`"MalformedMessage"`/`"UnknownCatalog"`/`"UnsupportedComponent"`/`"DuplicateSurface"`/
    /// `"UnknownSurface"`/`"WrongChat"`), never the `A2uiError` DU itself.
    let kind (error: A2uiError) : string =
        match error with
        | MalformedMessage _ -> "MalformedMessage"
        | UnknownCatalog _ -> "UnknownCatalog"
        | UnsupportedComponent _ -> "UnsupportedComponent"
        | DuplicateSurface _ -> "DuplicateSurface"
        | UnknownSurface _ -> "UnknownSurface"
        | WrongChat _ -> "WrongChat"

    /// The SAME human-readable description `A2uiIngestResult.Error` already uses for a call's own
    /// immediate failure — reused here so an observed condition reads identically regardless of
    /// which of the two channels (return value vs. observer) a caller inspects it through.
    let description (error: A2uiError) : string = A2uiError.describe error

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

    /// The SAME `JsonSerializerOptions` `Plan.toolWith` (ToolRouter.fs) uses to serialize a
    /// structured tool-button argument — exposed here (the one deliberate public entry point
    /// across the assembly boundary) so the C# façade's `PressContext.GetArg<T>`/`TryGetArg<T>`
    /// deserialize with the IDENTICAL `JsonFSharpConverter` configuration, rather than
    /// `System.Text.Json`'s bare defaults. Without this, a payload whose wire shape
    /// this configuration changes — e.g. a tuple, serialized as a JSON array rather than
    /// `System.Text.Json`'s own default shape — fails to round-trip on the host's own button.
    let structuredArgJsonOptions: JsonSerializerOptions = StructuredArgJson.options

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
