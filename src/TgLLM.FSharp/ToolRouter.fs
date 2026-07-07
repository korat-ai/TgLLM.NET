/// The idiomatic F# public surface for the Tool Router: a fluent tool registry and a `Plan`
/// module that turns an LLM-agnostic decision (which buttons, which registered tool + optional
/// arg, or a URL) into a `ToolKeyboard` the core's `ToolPlan.plan` can send. Compiles BEFORE
/// `TgBot.fs` (see the compile-order comment in `TgLLM.FSharp.fsproj`): `TgBotConfig`'s `Tools`
/// field and `TgBot`'s `SendKeyboardPlan` member need `ToolRegistry` to already exist, and must
/// stay INTRINSIC members of their own types (not cross-file type augmentations) so they behave
/// like ordinary members for reflection-based tooling (e.g. the C# façade's idiom-leak canary)
/// and are naturally callable from C#.
namespace TgLLM.FSharp

open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Serialization
open FSharp.UMX
open TgLLM.Core

/// Shared serializer options for structured tool-button arguments (`Plan.toolWith`,
/// `PressContext.GetArg`/`TryGetArg`), configured with `JsonFSharpConverter` so an F# record or
/// discriminated-union payload round-trips exactly like a C#-shaped one: plain
/// `System.Text.Json` cannot deserialize an F# record (its default constructor-matching heuristic
/// doesn't recognize the compiler-generated shape) or represent a DU at all. `private` — purely an
/// internal detail of how structured arguments are (de)serialized, never part of the public
/// surface.
module private StructuredArgJson =
    let options: JsonSerializerOptions = JsonFSharpOptions.Default().ToJsonSerializerOptions()

/// A fluent, mutable tool registry: wraps a plain `TgLLM.Core.IToolRegistry` so F# consumers can
/// chain `.Register` calls while building it.
[<Sealed>]
type ToolRegistry private (registry: IToolRegistry) =

    /// The underlying core registry — consumed by `TgBot.startPolling`/`startWebhook` to build a
    /// `ToolDispatch`, and by the C# façade's registration bridge (`ToolRouterSupport`).
    member _.Registry: IToolRegistry = registry

    /// Registers (or replaces) a tool under `name`. The handler may return any `Task<'a>`; its
    /// result is ignored by the runtime (it reacts through the `PressContext` it is given), same
    /// convention as the slice-1 `Button.on` hook. An invalid (empty-after-trim) `name` is a
    /// programmer error by the host (Always-Rule 6) — it fails fast rather than threading a
    /// `Result` through a fluent builder API.
    member this.Register(name: string, handler: PressContext -> Task<'a>) : ToolRegistry =
        match ToolName.create name with
        | Ok toolName ->
            registry.Register(toolName, fun ctx -> (handler ctx) :> Task)
            this
        | Error e -> invalidArg (nameof name) $"ToolRegistry.Register: invalid tool name ({e})"

    static member create() : ToolRegistry = ToolRegistry(InMemoryToolRegistry() :> IToolRegistry)

/// Idiomatic F# constructors for `TgLLM.Core.OwnerScope` (US1) — passed to
/// `TgBot.SendKeyboardPlan`'s `?owner` parameter. `OwnerScope` itself is a plain Core DU (fine to
/// use directly), so this module is a small naming convenience, not a wrapper type.
module Owner =

    /// Any presser in the chat may tap the keyboard's tool buttons — slice-2 behavior, unchanged.
    let anyone: OwnerScope = Anyone

    /// Only this Telegram user may tap the keyboard's tool buttons; every other (or unidentifiable)
    /// presser is refused with a notice.
    let user (id: int64) : OwnerScope = User(UMX.tag<userId> id)

/// Turns an LLM's button/tool/arg decision into the neutral `ToolKeyboard` plan. The library
/// ships no vendor LLM parsers — the host maps its own LLM output into calls to
/// `tool`/`toolWithArg`/`url`, then `rows`.
module Plan =

    /// A tool button with no argument.
    let tool (label: string) (toolName: string) : PlanButton = ToolButton(label, toolName, None)

    /// A tool button carrying a bound string argument.
    let toolWithArg (label: string) (toolName: string) (arg: string) : PlanButton = ToolButton(label, toolName, Some arg)

    /// A tool button carrying a bound structured payload: `arg` is serialized (System.Text.Json)
    /// into the same opaque string slot `toolWithArg` fills — it round-trips back out via
    /// `PressContext.GetArg<'T>()`/`TryGetArg<'T>()` on tap. Generalizes `toolWithArg`; a plain
    /// string argument built with that function keeps working unchanged.
    let toolWith<'T> (label: string) (toolName: string) (arg: 'T) : PlanButton =
        ToolButton(label, toolName, Some(JsonSerializer.Serialize<'T>(arg, StructuredArgJson.options)))

    /// A URL button: opens client-side, carries no token/binding/tool.
    let url (label: string) (url: string) : PlanButton = UrlButton(label, url)

    /// Builds the neutral plan from rows of buttons, validating shape (>=1 row, each >=1 button)
    /// AND every button (label, tool name, url) immediately — mirrors slice-1's `Keyboard.create`,
    /// which also catches a bad label at build time rather than deferring to send time. Delegates to
    /// `TgLLM.Core.ToolPlan.validate` (the single source of truth `ToolPlan.plan` itself re-checks
    /// at send time) so the two never drift apart.
    let rows (rows: PlanButton list list) : Result<ToolKeyboard, ToolError> = ToolPlan.validate { Rows = rows }

/// Typed access to the payload behind `PressContext.Arg` — the opaque string `Plan.toolWith`
/// serializes a structured argument into. Wrapped in a module (rather than declared loose in this
/// namespace) because F# only allows extension members directly in a namespace when the extended
/// type is defined in the SAME file/namespace — `PressContext` lives in `TgLLM.Core`. `AutoOpen` so
/// these members are in scope for any F# consumer that already does `open TgLLM.FSharp`, with no
/// extra `open` needed. Purely an F# convenience: the C# façade's `PressContext.GetArg<T>`/
/// `TryGetArg<T>` are separate, native members on the C# wrapper type, not this extension.
[<AutoOpen>]
module PressContextExtensions =

    type PressContext with

        /// Deserializes the bound argument as `'T`. An argument-less press, or a payload that is
        /// not valid JSON for `'T`, is a programmer error by the tool author (Always-Rule 6) — this
        /// fails fast rather than returning a default value; use `TryGetArg` for a non-throwing
        /// variant.
        member ctx.GetArg<'T>() : 'T =
            match ctx.Arg |> Option.ofObj with
            | None -> invalidOp "PressContext.GetArg: this press carries no argument."
            | Some json ->
                match JsonSerializer.Deserialize<'T>(json, StructuredArgJson.options) with
                | null -> invalidOp "PressContext.GetArg: the payload deserialized to null."
                | value -> value

        /// The safe variant: `None` for an argument-less press or a payload that fails to
        /// deserialize as `'T`, never an exception.
        member ctx.TryGetArg<'T>() : 'T option =
            match ctx.Arg |> Option.ofObj with
            | None -> None
            | Some json ->
                try
                    match JsonSerializer.Deserialize<'T>(json, StructuredArgJson.options) with
                    | null -> None
                    | value -> Some value
                with :? JsonException ->
                    None
