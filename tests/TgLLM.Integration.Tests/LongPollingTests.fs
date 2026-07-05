/// T025: failing tests for `LongPollingUpdateSource`, written before `TgLLM.BotApi.LongPollingUpdateSource`
/// exists — this file is filled in once T023/T024 are green (compile order: the fake server and
/// the client's pure mapping functions land first).
module TgLLM.Integration.Tests.LongPollingTests

open Expecto

[<Tests>]
let longPollingTests = testList "LongPollingUpdateSource" []
