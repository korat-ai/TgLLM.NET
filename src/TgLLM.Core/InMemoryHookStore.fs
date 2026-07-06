namespace TgLLM.Core

open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Default `IHookStore`: a `ConcurrentDictionary` keyed by `CallbackToken`. All operations
/// complete synchronously — hence `ValueTask` throughout.
type InMemoryHookStore() =
    let hooks = ConcurrentDictionary<CallbackToken, Hook>()

    interface IHookStore with
        member _.Register(bindings: IReadOnlyList<HookBinding>, _ct: CancellationToken) : ValueTask =
            for binding in bindings do
                hooks[binding.Token] <- binding.Hook
            ValueTask.CompletedTask

        member _.TryResolve(token: CallbackToken, _ct: CancellationToken) : ValueTask<Hook voption> =
            match hooks.TryGetValue token with
            | true, hook -> ValueTask.FromResult(ValueSome hook)
            | false, _ -> ValueTask.FromResult ValueNone

        member _.Remove(tokens: IReadOnlyList<CallbackToken>, _ct: CancellationToken) : ValueTask =
            for token in tokens do
                hooks.TryRemove(token) |> ignore
            ValueTask.CompletedTask
