/// Example (Principle VIII, feature 002-llm-tool-router, T031): a Tool Router bot — register named
/// tools ONCE, then turn a data-driven decision (a stand-in for an LLM's tool-call output) into a
/// neutral plan the library sends and routes. Runs over LONG POLLING by default, or WEBHOOKS when
/// `TRANSPORT=webhook` — the tool code is IDENTICAL either way (FR-013). Set `BOT_TOKEN` and
/// `CHAT_ID`; for webhooks also `PUBLIC_URL` and `WEBHOOK_SECRET`; then `dotnet run`.
module ToolRouterFSharp.Program

open System
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open Microsoft.AspNetCore.Builder
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.AspNetCore

let private requireEnv (name: string) : string =
    match Environment.GetEnvironmentVariable name |> Option.ofObj with
    | Some value -> value
    | None -> failwithf "environment variable %s is required" name

/// The tool catalog a real agent would register once at startup. The library ships NO business
/// tools of its own (FR-011) — these two are entirely example/host code.
let private tools =
    ToolRegistry
        .create()
        .Register(
            "approve",
            fun ctx ->
                task {
                    let build = ctx.Arg |> Option.ofObj |> Option.defaultValue "?"
                    do! ctx.EditTextAsync $"Approved build #{build} by {ctx.User.FirstName}"
                    ctx.Answer("Approved", alert = false)
                }
        )
        .Register(
            "reject",
            fun ctx ->
                task {
                    do! ctx.EditTextAsync $"Rejected by {ctx.User.FirstName}"
                    ctx.Answer("Rejected", alert = true)
                }
        )

/// A data-driven plan — stand-in for an LLM's own tool-call decision ("offer these tools, with
/// these args, plus a plain link"). The library has no idea an LLM exists (FR-013); a real host maps
/// its model's output into these same `Plan.tool`/`Plan.toolWithArg`/`Plan.url` calls.
let private buildPlan (buildNumber: string) : Result<ToolKeyboard, ToolError> =
    Plan.rows
        [ [ Plan.toolWithArg "Approve" "approve" buildNumber; Plan.tool "Reject" "reject" ]
          [ Plan.url "Docs" "https://example.test/docs" ] ]

[<EntryPoint>]
let main args =
    task {
        let botToken = requireEnv "BOT_TOKEN"
        let chat: ChatId = UMX.tag<chatId> (int64 (requireEnv "CHAT_ID"))
        let transport = Environment.GetEnvironmentVariable "TRANSPORT" |> Option.ofObj |> Option.defaultValue "polling"

        match buildPlan "42" with
        | Error error ->
            eprintfn "invalid plan: %A" error
            return 1
        | Ok plan ->
            match transport with
            | "webhook" ->
                let publicUrl = requireEnv "PUBLIC_URL"
                let secret = requireEnv "WEBHOOK_SECRET"

                use! bot =
                    TgBot.startWebhook (
                        (TgWebhookConfig.create (botToken, publicUrl, secret)).WithTools tools
                    )

                let! _ = bot.SendKeyboardPlan(chat, MessageText.unsafe "Deploy build #42?", plan)

                let app = WebApplication.CreateBuilder(args).Build()
                app.MapTelegramWebhook("/telegram/webhook", bot.WebhookSource, secret) |> ignore
                printfn "Tool Router bot (webhook) listening. Telegram POSTs updates to %s/telegram/webhook" publicUrl
                do! app.RunAsync()
                return 0
            | _ ->
                use! bot = TgBot.startPolling ((TgBotConfig.create botToken).WithTools tools)
                let! _ = bot.SendKeyboardPlan(chat, MessageText.unsafe "Deploy build #42?", plan)
                printfn "Tool Router bot (long polling) running. Ctrl+C to stop."
                do! Task.Delay Timeout.InfiniteTimeSpan
                return 0
    }
    |> fun run -> run.GetAwaiter().GetResult()
