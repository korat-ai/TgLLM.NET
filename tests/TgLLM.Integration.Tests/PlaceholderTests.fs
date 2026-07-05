/// Skeleton placeholder so the test project builds and `dotnet test` has something to discover.
/// The real fake-Bot-API integration tests (both transports, SC-008; failure isolation, SC-006;
/// ordering, SC-007) land starting Phase 3 (User Story 1, tasks T023+).
module TgLLM.Integration.Tests.PlaceholderTests

open Expecto

[<Tests>]
let placeholderTests =
    testList "placeholder" [
        testCase "project builds" <| fun _ -> Expect.isTrue true "sanity"
    ]
