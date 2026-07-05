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
