/// Example: a deploy-approval bot exercising the Tool Router's press authorization, LLM-facing tool
/// manifest, structured button arguments, richer client-side buttons, and durable LiteDB storage.
/// Runs over LONG POLLING by default, or WEBHOOKS when `TRANSPORT=webhook` — the tool code is
/// IDENTICAL either way. Set `BOT_TOKEN` and `CHAT_ID` (a private chat, so its chat id doubles as
/// the owner's user id); for webhooks also `PUBLIC_URL` and `WEBHOOK_SECRET`; then `dotnet run`.
module ToolRouterFSharp.Program

open System
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open Microsoft.AspNetCore.Builder
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.AspNetCore
open TgLLM.Persistence.LiteDb

let private requireEnv (name: string) : string =
    match Environment.GetEnvironmentVariable name |> Option.ofObj with
    | Some value -> value
    | None -> failwith $"environment variable {name} is required"

/// A structured payload bound to the "Ship" button. Any serializable F# record works as a tool
/// argument — the payload lives in the library's own binding store, free of a callback button's
/// tiny data limit — and comes back out typed via `ctx.GetArg<DeployRequest>()`.
type DeployRequest = { Version: string; Canary: bool }

/// The tool catalog a real agent would register once at startup. The library ships no business
/// tools of its own — these are entirely example/host code. "Ship" carries a description and an
/// argument schema; both are advisory metadata that only shape what `tools.ManifestJson()` reports,
/// never routing itself — a tool registered without either still registers and routes identically.
let private tools =
    ToolRegistry
        .create()
        .Register(
            "ship",
            (fun ctx ->
                task {
                    let request = ctx.GetArg<DeployRequest>()
                    let audience = if request.Canary then "as a canary" else "to everyone"
                    do! ctx.EditTextAsync $"Shipping {request.Version} {audience} — approved by {ctx.User.FirstName}"
                    ctx.Answer("Shipping...", alert = false)
                }),
            description = "Ship the pending build to production",
            argSchema =
                """{ "type": "object", "properties": { "Version": { "type": "string" }, "Canary": { "type": "boolean" } }, "required": ["Version"] }"""
        )
        .Register(
            "reject",
            (fun ctx ->
                task {
                    do! ctx.EditTextAsync $"Rejected by {ctx.User.FirstName}"
                    ctx.Answer("Rejected", alert = true)
                }),
            description = "Reject the pending build"
        )

/// A data-driven plan — stand-in for an LLM's own tool-call decision. Mixes a structured-argument
/// tool button with a plain one, a WebApp launch, a CopyText button, and a plain link.
let private buildPlan (version: string) : Result<ToolKeyboard, ToolError> =
    Plan.rows
        [ [ Plan.toolWith "Ship" "ship" { Version = version; Canary = true }
            Plan.tool "Reject" "reject" ]
          [ Plan.webApp "Release notes" "https://example.test/release-notes"
            Plan.copyText "Copy build tag" $"build-{version}" ]
          [ Plan.url "Docs" "https://example.test/docs" ] ]

[<EntryPoint>]
let main args =
    task {
        let botToken = requireEnv "BOT_TOKEN"
        let chat: ChatId = UMX.tag<chatId> (int64 (requireEnv "CHAT_ID"))
        let transport = Environment.GetEnvironmentVariable "TRANSPORT" |> Option.ofObj |> Option.defaultValue "polling"

        // The neutral wire JSON a host feeds straight into its LLM's function-calling API.
        printfn $"Tool manifest for this agent's LLM:\n{tools.ManifestJson()}"

        match buildPlan "42" with
        | Error error ->
            eprintfn $"invalid plan: {error}"
            return 1
        | Ok plan ->
            // Owner-scoped to the chat's own user (in a private chat, the chat id and the user id
            // are the same account), kept alive for 10 minutes, and consumed after the first press.
            let owner = Owner.user (UMX.untag chat)
            let expiresIn = TimeSpan.FromMinutes 10.
            use bindingStore = LiteDbBindingStore.OpenAt "bindings.db"

            match transport with
            | "webhook" ->
                let publicUrl = requireEnv "PUBLIC_URL"
                let secret = requireEnv "WEBHOOK_SECRET"

                use! bot =
                    TgBot.startWebhook (
                        (TgWebhookConfig.create (botToken, publicUrl, secret))
                            .WithTools(tools)
                            .WithBindingStore(bindingStore)
                    )

                let! _ =
                    bot.SendKeyboardPlan(
                        chat,
                        MessageText.unsafe "Deploy build #42?",
                        plan,
                        owner = owner,
                        expiresIn = expiresIn,
                        singleUse = true
                    )

                let app = WebApplication.CreateBuilder(args).Build()
                app.MapTelegramWebhook("/telegram/webhook", bot.WebhookSource, secret) |> ignore
                printfn $"Tool Router bot (webhook) listening. Telegram POSTs updates to {publicUrl}/telegram/webhook"
                do! app.RunAsync()
                return 0
            | _ ->
                use! bot =
                    TgBot.startPolling (
                        (TgBotConfig.create botToken)
                            .WithTools(tools)
                            .WithBindingStore(bindingStore)
                    )

                let! _ =
                    bot.SendKeyboardPlan(
                        chat,
                        MessageText.unsafe "Deploy build #42?",
                        plan,
                        owner = owner,
                        expiresIn = expiresIn,
                        singleUse = true
                    )

                printfn "Tool Router bot (long polling) running. Ctrl+C to stop."
                do! Task.Delay Timeout.InfiniteTimeSpan
                return 0
    }
    |> fun run -> run.GetAwaiter().GetResult()
