/// Example: an F# agent that proactively pushes an interactive keyboard every 30s
/// (an external stimulus, not a reply) over long polling, and reacts to taps. Set BOT_TOKEN and
/// CHAT_ID environment variables, then `dotnet run`.
module PollingFSharp.Program

open System
open System.Threading.Tasks
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp

let private requireEnv (name: string) : string =
    match Environment.GetEnvironmentVariable name |> Option.ofObj with
    | Some value -> value
    | None -> failwithf "environment variable %s is required" name

[<EntryPoint>]
let main _ =
    task {
        let botToken = requireEnv "BOT_TOKEN"
        let chat: ChatId = UMX.tag<chatId> (int64 (requireEnv "CHAT_ID"))

        use! bot = TgBot.startPolling (TgBotConfig.create botToken)

        let keyboard =
            Keyboard.create
                [ [ Button.on "Yes" (fun ctx -> ctx.ReplyTextAsync "You picked Yes")
                    Button.on "No" (fun ctx -> ctx.ReplyTextAsync "You picked No") ] ]

        match keyboard with
        | Error error ->
            eprintfn "invalid keyboard: %A" error
            return 1
        | Ok spec ->
            printfn "Polling. Pushing a keyboard to chat %d every 30s (Ctrl+C to stop)." (UMX.untag chat)

            while true do
                let! _ = bot.SendKeyboard(chat, MessageText.unsafe "Deploy?", spec)
                do! Task.Delay(TimeSpan.FromSeconds 30.0)

            return 0
    }
    |> fun run -> run.GetAwaiter().GetResult()
