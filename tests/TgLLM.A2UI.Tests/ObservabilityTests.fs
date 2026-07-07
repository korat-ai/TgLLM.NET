/// Tests for the A2UI observability seam (`IA2uiObserver`/`NoopA2uiObserver`/`A2uiObservability`):
/// the pure(-ish) helpers `A2uiRenderer.Ingest` (the F# façade) funnels every surfaced condition
/// through, exercised here against a bare recording double — no `TgBot`/transport/IO involved.
module TgLLM.A2UI.Tests.ObservabilityTests

open Expecto
open TgLLM.A2UI

let private recordingObserver () : IA2uiObserver * ResizeArray<A2uiError> * ResizeArray<ActionDescriptor> =
    let errors = ResizeArray<A2uiError>()
    let malformedActions = ResizeArray<ActionDescriptor>()

    { new IA2uiObserver with
        member _.OnA2uiError(error: A2uiError) = errors.Add error
        member _.OnMalformedAction(descriptor: ActionDescriptor) = malformedActions.Add descriptor },
    errors,
    malformedActions

let private renderedSurface (text: string) (unsupported: (string * string) list) : RenderedSurface =
    { Text = text
      Keyboard = { Rows = [] }
      Unsupported = unsupported }

[<Tests>]
let observabilityTests =
    testList "A2uiObservability" [

        testCase "NoopA2uiObserver reports nothing to anyone, for either method" <| fun _ ->
            let observer = NoopA2uiObserver() :> IA2uiObserver
            // No exception, no observable effect — nothing to assert beyond "this doesn't throw".
            observer.OnA2uiError(UnknownCatalog "bogus")
            observer.OnMalformedAction { SurfaceId = "s"; SourceComponentId = "c"; Name = "n"; Context = []; WantResponse = true; ActionId = None }

        testCase "reportError reports the error to the observer and returns it as Error" <| fun _ ->
            let observer, errors, _ = recordingObserver ()
            let error = UnknownCatalog "bogus-catalog"

            let result = A2uiObservability.reportError observer error

            Expect.equal result (Error error) "the same error is returned to the caller"
            Expect.equal (List.ofSeq errors) [ error ] "the same error reached the observer"

        testCase "reportError reports a MalformedMessage exactly like any other A2uiError" <| fun _ ->
            let observer, errors, _ = recordingObserver ()
            let error = MalformedMessage "missing required field 'version'"

            A2uiObservability.reportError observer error |> ignore

            Expect.equal (List.ofSeq errors) [ error ] "a malformed message is surfaced through the same channel"

        testCase "reportUnsupported reports one UnsupportedComponent per collected entry, in order" <| fun _ ->
            let observer, errors, _ = recordingObserver ()
            let rendered = renderedSurface "body" [ "TextField", "tf1"; "Slider", "sl1" ]

            A2uiObservability.reportUnsupported observer rendered

            Expect.equal
                (List.ofSeq errors)
                [ UnsupportedComponent("TextField", "tf1"); UnsupportedComponent("Slider", "sl1") ]
                "every unsupported entry is surfaced, in the render pass's own order"

        testCase "reportUnsupported reports nothing when the render pass collected no unsupported entries" <| fun _ ->
            let observer, errors, _ = recordingObserver ()
            let rendered = renderedSurface "body" []

            A2uiObservability.reportUnsupported observer rendered

            Expect.equal errors.Count 0 "supported-only content has nothing to surface"

        testCase "reportUnsupported never touches OnMalformedAction — the two observer methods stay independent" <| fun _ ->
            let observer, _, malformedActions = recordingObserver ()
            let rendered = renderedSurface "" [ "TextField", "tf1" ]

            A2uiObservability.reportUnsupported observer rendered

            Expect.equal malformedActions.Count 0 "an unsupported component is not a malformed action"
    ]
