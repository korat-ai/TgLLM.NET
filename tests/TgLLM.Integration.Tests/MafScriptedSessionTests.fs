/// Sanity coverage for `ScriptedAgent`'s session serialize/deserialize round trip
/// (`MafScriptedAgent.fs`): the double must actually carry an identifiable, round-trippable
/// session state, since a later durable-session feature scripts BOTH a successful round trip AND
/// a corrupt/foreign persisted session (`DeserializeSessionAsync` throwing) against it.
module TgLLM.Integration.Tests.MafScriptedSessionTests

open System.Text.Json
open Expecto
open TgLLM.Integration.Tests.MafScriptedAgent

[<Tests>]
let mafScriptedSessionTests =
    testList "ScriptedAgent session serialize/deserialize" [

        testCaseAsync "SerializeSessionAsync of a freshly created session emits its own nonce as JSON"
        <| async {
            do!
                task {
                    let agent = ScriptedAgent []
                    let! session = agent.CreateSessionAsync()
                    let scripted = session :?> ScriptedSession

                    let! element = agent.SerializeSessionAsync session

                    let found, prop = element.TryGetProperty "nonce"
                    Expect.isTrue found "the serialized element carries a 'nonce' property"
                    Expect.equal (prop.GetString()) scripted.Nonce "the serialized nonce matches the session's own nonce"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "DeserializeSessionAsync restores the SAME nonce, even on a DIFFERENT agent instance"
        <| async {
            do!
                task {
                    let sourceAgent = ScriptedAgent []
                    let! session = sourceAgent.CreateSessionAsync()
                    let originalNonce = (session :?> ScriptedSession).Nonce
                    let! element = sourceAgent.SerializeSessionAsync session

                    // A restore doesn't need to happen on the SAME agent instance that serialized —
                    // exactly what a bot-restart-and-resume round trip looks like.
                    let targetAgent = ScriptedAgent []
                    let! restored = targetAgent.DeserializeSessionAsync element

                    Expect.equal ((restored :?> ScriptedSession).Nonce) originalNonce "the restored session carries the SAME nonce that was serialized"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "DeserializeSessionAsync of a foreign JSON shape throws rather than fabricating a session"
        <| async {
            do!
                task {
                    let agent = ScriptedAgent []
                    let foreign = JsonSerializer.SerializeToElement {| other = 1 |}

                    let! threw =
                        task {
                            try
                                let! _ = agent.DeserializeSessionAsync foreign
                                return false
                            with :? System.InvalidOperationException ->
                                return true
                        }

                    Expect.isTrue threw "a corrupt/foreign persisted session shape must throw, not silently fabricate a session"
                }
                |> Async.AwaitTask
        }
    ]
