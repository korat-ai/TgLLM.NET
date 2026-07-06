/// T001 (Setup): a placeholder so the new `TgLLM.Persistence.Tests` project has at least one green
/// test and the solution's test discovery/reporting is exercised end-to-end. Replaced by the real
/// `FileBindingStoreTests` in T025 (US3, out of scope for T001-T020).
module TgLLM.Persistence.Tests.PlaceholderTests

open Expecto

[<Tests>]
let placeholderTests =
    testList "TgLLM.Persistence (skeleton)" [
        testCase "the project is wired into the solution and its test runner executes" <| fun _ ->
            Expect.equal TgLLM.Persistence.Placeholder.pendingUserStory "US3" "placeholder module is reachable"
    ]
