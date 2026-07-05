// Example (Principle VIII): a C# webhook bot. Registers a webhook, sends an initial keyboard, and
// hosts the receiving endpoint with MapTelegramWebhook. The SAME hook code as the polling example
// (FR-013). Set BOT_TOKEN, PUBLIC_URL (https, reachable by Telegram), WEBHOOK_SECRET and CHAT_ID,
// then `dotnet run`.
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using TgLLM.AspNetCore;
using TgLLM.CSharp;

var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new InvalidOperationException("environment variable BOT_TOKEN is required");
var publicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL")
    ?? throw new InvalidOperationException("environment variable PUBLIC_URL is required");
var secret = Environment.GetEnvironmentVariable("WEBHOOK_SECRET")
    ?? throw new InvalidOperationException("environment variable WEBHOOK_SECRET is required");
var chatId = long.Parse(Environment.GetEnvironmentVariable("CHAT_ID")
    ?? throw new InvalidOperationException("environment variable CHAT_ID is required"));

await using var agent = await TelegramAgent.StartWebhookAsync(new TelegramAgentOptions
{
    BotToken = botToken,
    PublicUrl = publicUrl,
    SecretToken = secret,
});

var keyboard = new KeyboardBuilder()
    .Row(r => r
        .Button("Yes", ctx => ctx.ReplyTextAsync("You picked Yes"))
        .Button("No", ctx => ctx.ReplyTextAsync("You picked No")))
    .Build();

await agent.SendKeyboardAsync(chatId, "Deploy?", keyboard);

var app = WebApplication.CreateBuilder(args).Build();
app.MapTelegramWebhook("/telegram/webhook", agent.WebhookSource, secret);
Console.WriteLine($"Webhook bot listening. Telegram POSTs updates to {publicUrl}/telegram/webhook");
await app.RunAsync();
