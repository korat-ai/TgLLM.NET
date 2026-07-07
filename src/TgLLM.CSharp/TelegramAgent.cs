using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TgLLM.FSharp;
using TgLLM.Webhooks;

namespace TgLLM.CSharp;

/// <summary>Configuration for a <see cref="TelegramAgent"/>.</summary>
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

    /// <summary>Surface hook failures / unknown presses through this logger.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Tools available to <see cref="TelegramAgent.SendKeyboardPlanAsync"/>-sent
    /// keyboards. <c>null</c> means no Tool Router is wired in.</summary>
    public ToolRegistry? Tools { get; init; }

    /// <summary>The store backing every tool-button binding. <c>null</c> (the default) keeps the
    /// in-memory default; pass e.g. <c>TgLLM.Persistence.FileBindingStore.OpenAt("bindings.json")</c>
    /// so bindings survive a restart.</summary>
    public TgLLM.Core.IBindingStore? BindingStore { get; init; }
}

/// <summary>
/// The C# public façade. An agent that ingests updates in the background — over long polling or
/// webhooks with identical handler code — and sends keyboards/messages to a chat.
/// </summary>
public sealed class TelegramAgent : IAsyncDisposable
{
    private readonly TgBot _bot;

    private TelegramAgent(TgBot bot) => _bot = bot;

    /// <summary>
    /// Start ingesting updates via long polling.
    /// </summary>
    /// <param name="options">Bot configuration.</param>
    /// <param name="ct">
    /// Accepted for API-shape consistency, but NOT currently honored: the underlying
    /// <c>TgLLM.FSharp.TgBot.startPolling</c> has no per-call cancellation seam, and threading one
    /// in here (e.g. via <c>Task.WaitAsync</c>) would risk abandoning a partially-started
    /// <c>TgBot</c> — one that has already begun ingesting updates in the background — with no
    /// reference left to dispose it, which is worse than the current no-op. Documented explicitly
    /// rather than silently ignored.
    /// </param>
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

        if (options.BindingStore is not null)
        {
            config = config.WithBindingStore(options.BindingStore);
        }

        var bot = await TgBot.startPolling(config);
        return new TelegramAgent(bot);
    }

    /// <summary>
    /// Start ingesting updates via webhooks; map the endpoint with
    /// <c>app.MapTelegramWebhook(agent.WebhookSource, secret)</c>.
    /// </summary>
    /// <param name="options">Bot configuration.</param>
    /// <param name="ct">
    /// Accepted for API-shape consistency, but NOT currently honored — same reason as
    /// <see cref="StartPollingAsync"/>'s <c>ct</c>: <c>TgLLM.FSharp.TgBot.startWebhook</c> has no
    /// per-call cancellation seam, and abandoning a partially-started <c>TgBot</c> would leak it.
    /// Documented explicitly rather than silently ignored.
    /// </param>
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

        if (options.BindingStore is not null)
        {
            config = config.WithBindingStore(options.BindingStore);
        }

        var bot = await TgBot.startWebhook(config);
        return new TelegramAgent(bot);
    }

    /// <summary>The webhook ingress to hand to <c>MapTelegramWebhook</c> (webhook mode only).</summary>
    public WebhookUpdateSource WebhookSource => _bot.WebhookSource;

    /// <summary>
    /// Send an interactive keyboard to a chat; returns the sent message id.
    /// </summary>
    /// <param name="chatId">The target chat.</param>
    /// <param name="text">The message text sent alongside the keyboard.</param>
    /// <param name="keyboard">The keyboard layout.</param>
    /// <param name="ct">
    /// Cancels the WAIT for this call to complete (the awaited task then faults with
    /// <see cref="OperationCanceledException"/>). Does NOT abort the underlying Telegram Bot API
    /// request itself — <c>TgLLM.FSharp.TgBot</c>'s send methods have no per-call cancellation
    /// seam of their own, so the send may still complete in the background after this call
    /// returns. Threaded via <see cref="Task.WaitAsync(CancellationToken)"/> rather than silently
    /// ignored.
    /// </param>
    public Task<long> SendKeyboardAsync(long chatId, string text, Keyboard keyboard, CancellationToken ct = default) =>
        _bot.SendKeyboard(chatId, text, keyboard.Spec).WaitAsync(ct);

    /// <summary>
    /// Send a keyboard built from a Tool Router plan; presses route to the tools registered via
    /// <see cref="TelegramAgentOptions.Tools"/>.
    /// </summary>
    /// <param name="chatId">The target chat.</param>
    /// <param name="text">The message text sent alongside the keyboard.</param>
    /// <param name="plan">The neutral Tool Router keyboard plan.</param>
    /// <param name="owner">
    /// Scopes every tool button on this keyboard to that presser (see <see cref="Owner"/>);
    /// <c>null</c> (the default) means <see cref="Owner.Anyone"/> — any presser resolves the
    /// button, unchanged behavior.
    /// </param>
    /// <param name="deniedNotice">
    /// Overrides the notice a refused non-owner presser sees; <c>null</c> (the default) uses the
    /// library's built-in notice.
    /// </param>
    /// <param name="ct">Same semantics as <see cref="SendKeyboardAsync"/>'s <c>ct</c>.</param>
    public Task<long> SendKeyboardPlanAsync(
        long chatId,
        string text,
        KeyboardPlan plan,
        TgLLM.Core.OwnerScope? owner = null,
        string? deniedNotice = null,
        CancellationToken ct = default) =>
        // `deniedNotice` reaches the F# optional parameter through its generated
        // `string -> FSharpOption<string>` implicit conversion, which itself maps a `null` argument
        // to `None` (the same "no override" the F# façade's own callers get by omitting the
        // argument) — the `!` here silences a nullable-reference-type false positive on that
        // conversion, not an actual null-safety gap.
        _bot.SendKeyboardPlan(chatId, text, plan.Plan, owner ?? Owner.Anyone, deniedNotice!).WaitAsync(ct);

    /// <summary>
    /// Send a plain text message to a chat; returns the sent message id.
    /// </summary>
    /// <param name="chatId">The target chat.</param>
    /// <param name="text">The message text.</param>
    /// <param name="ct">Same semantics as <see cref="SendKeyboardAsync"/>'s <c>ct</c>.</param>
    public Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default) =>
        _bot.SendText(chatId, text).WaitAsync(ct);

    public ValueTask DisposeAsync() => ((IAsyncDisposable)_bot).DisposeAsync();
}
