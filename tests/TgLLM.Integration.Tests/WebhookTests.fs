/// Tests for the webhook update source — secret-token verification, `Update` parsing, and
/// that a pushed callback-query update surfaces as a mapped `ButtonPressed` on `Updates` (the same
/// domain event long polling produces).
module TgLLM.Integration.Tests.WebhookTests

open System.Collections.Generic
open System.Threading
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.Webhooks
open TgLLM.Integration.Tests.FakeBotApiServer

let private drain (source: IUpdateSource) : System.Threading.Tasks.Task<AgentEvent list> =
    task {
        let events = ResizeArray<AgentEvent>()
        let enumerator = source.Updates(CancellationToken.None).GetAsyncEnumerator(CancellationToken.None)
        let mutable go = true

        while go do
            let! hasNext = enumerator.MoveNextAsync()

            if hasNext then events.Add enumerator.Current else go <- false

        do! enumerator.DisposeAsync()
        return List.ofSeq events
    }

[<Tests>]
let webhookTests =
    testList
        "WebhookUpdateSource"
        [

          testList
              "verifySecretToken"
              [ test "no configured secret accepts anything" {
                    Expect.isTrue (Webhook.verifySecretToken None (Some "whatever")) "unconfigured ⇒ accept"
                    Expect.isTrue (Webhook.verifySecretToken None None) "unconfigured, no header ⇒ accept"
                }
                test "matching header is accepted" {
                    Expect.isTrue (Webhook.verifySecretToken (Some "s3cret") (Some "s3cret")) "match ⇒ accept"
                }
                test "mismatched header is rejected" {
                    Expect.isFalse (Webhook.verifySecretToken (Some "s3cret") (Some "nope")) "mismatch ⇒ reject"
                }
                test "missing header when a secret is configured is rejected" {
                    Expect.isFalse (Webhook.verifySecretToken (Some "s3cret") None) "missing header ⇒ reject"
                } ]

          testCaseAsync "a pushed callback-query update surfaces as a mapped ButtonPressed"
          <| async {
              do!
                  task {
                      let token = CallbackToken.generate ()

                      let json =
                          TelegramJson.callbackQueryUpdate 7 "wq" (CallbackToken.value token) 321L 12 654L "Wanda"

                      let update = Webhook.parseUpdate json

                      let source = WebhookUpdateSource()
                      do! source.Ingest(update, CancellationToken.None)
                      source.Complete()

                      let! events = drain (source :> IUpdateSource)

                      match events with
                      | [ ButtonPressed press ] ->
                          Expect.equal press.Token token "surfaces the pressed token"
                          Expect.equal (UMX.untag press.Chat) 321L "carries the chat id"
                      | _ -> failtestf "expected exactly one ButtonPressed, got %A" events
                  }
                  |> Async.AwaitTask
          } ]
