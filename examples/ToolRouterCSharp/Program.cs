// Example (Principle VIII, feature 002-llm-tool-router, T031): a Tool Router bot — register named
// tools ONCE, then turn a data-driven decision (a stand-in for an LLM's tool-call output) into a
// neutral plan the library sends and routes. Runs over LONG POLLING by default, or WEBHOOKS when
// TRANSPORT=webhook is set — the tool code is IDENTICAL either way (FR-013). Set BOT_TOKEN and
// CHAT_ID; for webhooks also PUBLIC_URL and WEBHOOK_SECRET; then `dotnet run`.
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using TgLLM.AspNetCore;
using TgLLM.CSharp;

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"environment variable {name} is required");

var botToken = RequireEnv("BOT_TOKEN");
var chatId = long.Parse(RequireEnv("CHAT_ID"));
var transport = Environment.GetEnvironmentVariable("TRANSPORT") ?? "polling";

// The tool catalog a real agent would register once at startup. The library ships NO business
// tools of its own (FR-011) — these two are entirely example/host code.
var tools = new ToolRegistry()
    .Register("approve", async ctx =>
    {
        var build = ctx.Arg ?? "?";
        await ctx.EditTextAsync($"Approved build #{build} by {ctx.User.FirstName}");
        ctx.Answer("Approved", alert: false);
    })
    .Register("reject", async ctx =>
    {
        await ctx.EditTextAsync($"Rejected by {ctx.User.FirstName}");
        ctx.Answer("Rejected", alert: true);
    });

// A data-driven plan — stand-in for an LLM's own tool-call decision ("offer these tools, with
// these args, plus a plain link"). The library has no idea an LLM exists (FR-013); a real host maps
// its model's output into these same PlanRowBuilder.Tool/Url calls.
var plan = new PlanBuilder()
    .Row(r => r.Tool("Approve", "approve", "42").Tool("Reject", "reject"))
    .Row(r => r.Url("Docs", "https://example.test/docs"))
    .Build();

if (transport == "webhook")
{
    var publicUrl = RequireEnv("PUBLIC_URL");
    var secret = RequireEnv("WEBHOOK_SECRET");

    await using var agent = await TelegramAgent.StartWebhookAsync(new TelegramAgentOptions
    {
        BotToken = botToken,
        PublicUrl = publicUrl,
        SecretToken = secret,
        Tools = tools,
    });

    await agent.SendKeyboardPlanAsync(chatId, "Deploy build #42?", plan);

    var app = WebApplication.CreateBuilder(args).Build();
    app.MapTelegramWebhook("/telegram/webhook", agent.WebhookSource, secret);
    Console.WriteLine($"Tool Router bot (webhook) listening. Telegram POSTs updates to {publicUrl}/telegram/webhook");
    await app.RunAsync();
}
else
{
    await using var agent = await TelegramAgent.StartPollingAsync(new TelegramAgentOptions
    {
        BotToken = botToken,
        Tools = tools,
    });

    await agent.SendKeyboardPlanAsync(chatId, "Deploy build #42?", plan);
    Console.WriteLine("Tool Router bot (long polling) running. Ctrl+C to stop.");
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
