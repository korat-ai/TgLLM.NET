/// Example (Principle VIII): an F# webhook bot. Registers a webhook, sends an initial keyboard, and
/// hosts the receiving endpoint with `MapTelegramWebhook`. The SAME hook code as the polling example.
/// Set BOT_TOKEN, PUBLIC_URL (https, reachable by Telegram), WEBHOOK_SECRET and CHAT_ID,
/// then `dotnet run`.
module WebhookFSharp.Program

open System
open FSharp.UMX
open Microsoft.AspNetCore.Builder
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.AspNetCore

let private requireEnv (name: string) : string =
    match Environment.GetEnvironmentVariable name |> Option.ofObj with
    | Some value -> value
    | None -> failwithf "environment variable %s is required" name

[<EntryPoint>]
let main args =
    task {
        let botToken = requireEnv "BOT_TOKEN"
        let publicUrl = requireEnv "PUBLIC_URL"
        let secret = requireEnv "WEBHOOK_SECRET"
        let chat: ChatId = UMX.tag<chatId> (int64 (requireEnv "CHAT_ID"))

        use! bot = TgBot.startWebhook (TgWebhookConfig.create (botToken, publicUrl, secret))

        let keyboard =
            Keyboard.create
                [ [ Button.on "Yes" (fun ctx -> ctx.ReplyTextAsync "You picked Yes")
                    Button.on "No" (fun ctx -> ctx.ReplyTextAsync "You picked No") ] ]

        match keyboard with
        | Error error ->
            eprintfn "invalid keyboard: %A" error
            return 1
        | Ok spec ->
            let! _ = bot.SendKeyboard(chat, MessageText.unsafe "Deploy?", spec)

            let app = WebApplication.CreateBuilder(args).Build()
            app.MapTelegramWebhook("/telegram/webhook", bot.WebhookSource, secret) |> ignore
            printfn "Webhook bot listening. Telegram POSTs updates to %s/telegram/webhook" publicUrl
            do! app.RunAsync()
            return 0
    }
    |> fun run -> run.GetAwaiter().GetResult()
