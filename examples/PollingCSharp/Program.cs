// Example: a C# agent that proactively pushes an interactive keyboard every 30s
// (an external stimulus, not a reply) over long polling, and reacts to taps. Set BOT_TOKEN and
// CHAT_ID environment variables, then `dotnet run`.
using System;
using System.Threading.Tasks;
using TgLLM.CSharp;

var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new InvalidOperationException("environment variable BOT_TOKEN is required");
var chatId = long.Parse(Environment.GetEnvironmentVariable("CHAT_ID")
    ?? throw new InvalidOperationException("environment variable CHAT_ID is required"));

await using var agent = await TelegramAgent.StartPollingAsync(new TelegramAgentOptions { BotToken = botToken });

var keyboard = new KeyboardBuilder()
    .Row(r => r
        .Button("Yes", ctx => ctx.ReplyTextAsync("You picked Yes"))
        .Button("No", ctx => ctx.ReplyTextAsync("You picked No")))
    .Build();

Console.WriteLine($"Polling. Pushing a keyboard to chat {chatId} every 30s (Ctrl+C to stop).");

while (true)
{
    await agent.SendKeyboardAsync(chatId, "Deploy?", keyboard);
    await Task.Delay(TimeSpan.FromSeconds(30));
}
