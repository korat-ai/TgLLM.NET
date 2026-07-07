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
open FSharp.UMX
open TgLLM.Core

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

    /// A URL button: opens client-side, carries no token/binding/tool.
    let url (label: string) (url: string) : PlanButton = UrlButton(label, url)

    /// Builds the neutral plan from rows of buttons, validating shape (>=1 row, each >=1 button)
    /// AND every button (label, tool name, url) immediately — mirrors slice-1's `Keyboard.create`,
    /// which also catches a bad label at build time rather than deferring to send time. Delegates to
    /// `TgLLM.Core.ToolPlan.validate` (the single source of truth `ToolPlan.plan` itself re-checks
    /// at send time) so the two never drift apart.
    let rows (rows: PlanButton list list) : Result<ToolKeyboard, ToolError> = ToolPlan.validate { Rows = rows }
