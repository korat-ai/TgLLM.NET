using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TgLLM.FSharp;
using TgLLM.Webhooks;

namespace TgLLM.CSharp;

/// <summary>Configuration for a <see cref="TelegramAgent"/> (T032, contracts/csharp-facade.md).</summary>
public sealed class TelegramAgentOptions
{
    /// <summary>The bot token from BotFather.</summary>
    public required string BotToken { get; init; }

    /// <summary>Public HTTPS URL Telegram POSTs updates to (webhook mode).</summary>
    public string? PublicUrl { get; init; }

    /// <summary>Secret token echoed in the webhook request header and verified (webhook mode).</summary>
    public string? SecretToken { get; init; }

    /// <summary>Override the Bot API endpoint (tests / local Bot API server / test environment).</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Surface hook failures / unknown presses through this logger (FR-009).</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Tools available to <see cref="TelegramAgent.SendKeyboardPlanAsync"/>-sent keyboards
    /// (feature 002-llm-tool-router, T019). <c>null</c> means no Tool Router is wired in.</summary>
    public ToolRegistry? Tools { get; init; }
}

/// <summary>
/// The C# public façade (T032). An agent that ingests updates in the background — over long polling
/// or webhooks with identical handler code (FR-013) — and sends keyboards/messages to a chat.
/// </summary>
public sealed class TelegramAgent : IAsyncDisposable
{
    private readonly TgBot _bot;

    private TelegramAgent(TgBot bot) => _bot = bot;

    /// <summary>Start ingesting updates via long polling.</summary>
    public static async Task<TelegramAgent> StartPollingAsync(TelegramAgentOptions options, CancellationToken ct = default)
    {
        var config = TgBotConfig.create(options.BotToken);

        if (!string.IsNullOrEmpty(options.BaseUrl))
        {
            config = config.WithBaseUrl(options.BaseUrl);
        }

        if (options.Logger is not null)
        {
            config = config.WithLogger(options.Logger);
        }

        if (options.Tools is not null)
        {
            config = config.WithTools(options.Tools.Inner);
        }

        var bot = await TgBot.startPolling(config);
        return new TelegramAgent(bot);
    }

    /// <summary>Start ingesting updates via webhooks; map the endpoint with
    /// <c>app.MapTelegramWebhook(agent.WebhookSource, secret)</c>.</summary>
    public static async Task<TelegramAgent> StartWebhookAsync(TelegramAgentOptions options, CancellationToken ct = default)
    {
        var config = TgWebhookConfig.create(options.BotToken, options.PublicUrl ?? string.Empty, options.SecretToken ?? string.Empty);

        if (!string.IsNullOrEmpty(options.BaseUrl))
        {
            config = config.WithBaseUrl(options.BaseUrl);
        }

        if (options.Logger is not null)
        {
            config = config.WithLogger(options.Logger);
        }

        if (options.Tools is not null)
        {
            config = config.WithTools(options.Tools.Inner);
        }

        var bot = await TgBot.startWebhook(config);
        return new TelegramAgent(bot);
    }

    /// <summary>The webhook ingress to hand to <c>MapTelegramWebhook</c> (webhook mode only).</summary>
    public WebhookUpdateSource WebhookSource => _bot.WebhookSource;

    /// <summary>Send an interactive keyboard to a chat; returns the sent message id.</summary>
    public Task<long> SendKeyboardAsync(long chatId, string text, Keyboard keyboard, CancellationToken ct = default) =>
        _bot.SendKeyboard(chatId, text, keyboard.Spec);

    /// <summary>Send a keyboard built from a Tool Router plan (feature 002-llm-tool-router, T019);
    /// presses route to the tools registered via <see cref="TelegramAgentOptions.Tools"/>.</summary>
    public Task<long> SendKeyboardPlanAsync(long chatId, string text, KeyboardPlan plan, CancellationToken ct = default) =>
        _bot.SendKeyboardPlan(chatId, text, plan.Plan);

    /// <summary>Send a plain text message to a chat; returns the sent message id.</summary>
    public Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default) =>
        _bot.SendText(chatId, text);

    public ValueTask DisposeAsync() => ((IAsyncDisposable)_bot).DisposeAsync();
}
