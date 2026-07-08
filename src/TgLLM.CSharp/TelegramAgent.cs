using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Core;
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
    /// or <c>TgLLM.Persistence.LiteDb.LiteDbBindingStore.OpenAt("bindings.db")</c> so bindings
    /// survive a restart.</summary>
    public TgLLM.Core.IBindingStore? BindingStore { get; init; }

    /// <summary>Reclaims a per-chat dispatcher channel/worker once idle this long with nothing
    /// buffered. <c>null</c> (the default) keeps a chat's resources for the whole run.</summary>
    public TimeSpan? IdleChatEviction { get; init; }

    /// <summary>Override the clock expiry/redelivery-dedup decisions read "now" from —
    /// <c>null</c> (the default) uses real wall-clock time. Overriding it is primarily useful for
    /// deterministic tests of a host's own expiry logic, not something a production bot normally
    /// needs. Mirrors the F# façade's <c>CommonConfig.Clock</c>/<c>WithClock</c>.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>React to an incoming user text message, run on that message's chat's dispatcher
    /// lane (serialized with that chat's own button presses, in arrival order). <c>null</c> (the
    /// default) means this bot does not answer text messages at all — every pre-existing bot's
    /// behavior stays byte-identical. Config-time only: a bot already ingesting updates cannot
    /// late-bind a handler, so this must be set before <see cref="TelegramAgent.StartPollingAsync"/>/
    /// <see cref="TelegramAgent.StartWebhookAsync"/> is called. Mirrors the F# façade's
    /// <c>CommonConfig.OnMessage</c>/<c>WithOnMessage</c>.</summary>
    public Func<IncomingMessageInfo, CancellationToken, Task>? OnMessage { get; init; }
}

/// <summary>An incoming user text message, as seen by <see cref="TelegramAgentOptions.OnMessage"/>
/// — the message-side sibling of <see cref="PressContext"/>. Plain BCL shapes only (<c>long</c>
/// ids, no UMX-tagged/F#-idiomatic type), mirroring how <see cref="PressContext"/> itself exposes
/// <c>Chat</c>/<c>MessageId</c> as plain <c>long</c>.</summary>
public sealed class IncomingMessageInfo
{
    /// <summary>The chat the message was sent in.</summary>
    public long ChatId { get; }

    /// <summary>The sender's user id.</summary>
    public long SenderId { get; }

    /// <summary>The sender's first name.</summary>
    public string SenderFirstName { get; }

    /// <summary>The sender's @username, if they have one.</summary>
    public string? SenderUsername { get; }

    /// <summary>The message's own id (for e.g. a reply that quotes it).</summary>
    public long MessageId { get; }

    /// <summary>The message's text.</summary>
    public string Text { get; }

    internal IncomingMessageInfo(long chatId, long senderId, string senderFirstName, string? senderUsername, long messageId, string text)
    {
        ChatId = chatId;
        SenderId = senderId;
        SenderFirstName = senderFirstName;
        SenderUsername = senderUsername;
        MessageId = messageId;
        Text = text;
    }
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
    /// The underlying F# façade bot. Internal — a seam for other <c>TgLLM.CSharp</c> types built
    /// ON TOP of an already-running agent (e.g. <see cref="A2uiRenderer"/>) to reach the SAME
    /// send path / Tool Router this agent itself uses, rather than needing their own separate
    /// wiring.
    /// </summary>
    internal TgBot Bot => _bot;

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

        if (options.IdleChatEviction is { } idleChatEviction)
        {
            config = config.WithIdleChatEviction(idleChatEviction);
        }

        if (options.Clock is { } clock)
        {
            config = config.WithClock(ToFSharpClock(clock));
        }

        if (options.OnMessage is { } onMessage)
        {
            config = config.WithOnMessage(ToFSharpMessageHandler(onMessage));
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

        if (options.IdleChatEviction is { } idleChatEviction)
        {
            config = config.WithIdleChatEviction(idleChatEviction);
        }

        if (options.Clock is { } clock)
        {
            config = config.WithClock(ToFSharpClock(clock));
        }

        if (options.OnMessage is { } onMessage)
        {
            config = config.WithOnMessage(ToFSharpMessageHandler(onMessage));
        }

        var bot = await TgBot.startWebhook(config);
        return new TelegramAgent(bot);
    }

    /// <summary>
    /// Adapts a plain BCL <see cref="Func{DateTimeOffset}"/> to the F# façade's <c>Clock</c>
    /// (<c>unit -&gt; DateTimeOffset</c>, i.e. <c>FSharpFunc&lt;Unit, DateTimeOffset&gt;</c>) — a
    /// nilary BCL delegate has no argument to line up with <c>Clock</c>'s single <c>Unit</c>
    /// parameter, so this wraps it explicitly rather than relying on an implicit conversion.
    /// </summary>
    private static FSharpFunc<Unit, DateTimeOffset> ToFSharpClock(Func<DateTimeOffset> clock) =>
        FSharpFunc<Unit, DateTimeOffset>.FromConverter(new Converter<Unit, DateTimeOffset>(_ => clock()));

    /// <summary>
    /// Adapts the public <see cref="Func{IncomingMessageInfo,CancellationToken,Task}"/> to the F#
    /// façade's curried <c>MessageHandler</c> — <c>TgLLM.FSharp.MessageHandlers.Wrap</c> does the
    /// actual <c>Func</c> → <c>FSharpFunc</c> adaptation (the established idiom this façade already
    /// uses for <c>Keyboards.Build</c>/<c>ToolRegistrations.Register</c>: the F# side accepts a
    /// plain BCL delegate directly rather than the C# side hand-building an <c>FSharpFunc</c>);
    /// this method's own job is only the <c>TgLLM.Core.IncomingMessage</c> → <see cref="IncomingMessageInfo"/>
    /// translation, since <c>TgLLM.FSharp</c> has no dependency on this (<c>TgLLM.CSharp</c>-only) DTO.
    /// </summary>
    private static FSharpFunc<TgLLM.Core.IncomingMessage, FSharpFunc<CancellationToken, Task>> ToFSharpMessageHandler(
        Func<IncomingMessageInfo, CancellationToken, Task> handler) =>
        MessageHandlers.Wrap(new Func<TgLLM.Core.IncomingMessage, CancellationToken, Task>(
            (message, ct) => handler(ToIncomingMessageInfo(message), ct)));

    private static IncomingMessageInfo ToIncomingMessageInfo(TgLLM.Core.IncomingMessage message) =>
        new(message.Chat, message.Sender.Id, message.Sender.FirstName, message.Sender.Username, message.MessageId, message.Text);

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
    /// <param name="expiresIn">
    /// Stamps every tool binding this send produces with an expiry this far past the bot's own
    /// clock; <c>null</c> (the default) leaves the binding with no expiry, unchanged behavior.
    /// </param>
    /// <param name="singleUse">
    /// When <c>true</c>, stamps every tool binding this send produces as consumed after its first
    /// successful press; <c>false</c> (the default) leaves bindings reusable, unchanged behavior.
    /// </param>
    /// <param name="ct">Same semantics as <see cref="SendKeyboardAsync"/>'s <c>ct</c>.</param>
    public Task<long> SendKeyboardPlanAsync(
        long chatId,
        string text,
        KeyboardPlan plan,
        TgLLM.Core.OwnerScope? owner = null,
        string? deniedNotice = null,
        TimeSpan? expiresIn = null,
        bool singleUse = false,
        CancellationToken ct = default) =>
        // `deniedNotice` reaches the F# optional parameter through its generated
        // `string -> FSharpOption<string>` implicit conversion, which itself maps a `null` argument
        // to `None` (the same "no override" the F# façade's own callers get by omitting the
        // argument) — the `!` here silences a nullable-reference-type false positive on that
        // conversion, not an actual null-safety gap. `expiresIn` is a value type (`TimeSpan`), so
        // that same implicit conversion has no null case to lean on — `OptionModule.OfNullable`
        // (the same helper `BindingStoreAdapter` already uses for `ExpiresAt`) maps the CLR
        // `Nullable<TimeSpan>` explicitly: `null` -> `None`, a value -> `Some value`. `singleUse` is
        // a non-nullable `bool` (already defaulted to `false` at this boundary), so it flows through
        // the same implicit `bool -> FSharpOption<bool>` conversion `owner` uses, no explicit
        // wrapping needed. `parseMode` has no C#-facing parameter on THIS method — it's
        // `FSharpOption<ParseMode>.None` unconditionally, same as every pre-A2UI call site; the
        // A2UI façade sends through its own path instead of `SendKeyboardPlanAsync`.
        _bot
            .SendKeyboardPlan(
                chatId,
                text,
                plan.Plan,
                owner ?? Owner.Anyone,
                deniedNotice!,
                OptionModule.OfNullable(expiresIn),
                singleUse,
                FSharpOption<TgLLM.Core.ParseMode>.None)
            .WaitAsync(ct);

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
