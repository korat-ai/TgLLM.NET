namespace TgLLM.Persistence

/// Placeholder for US3 (T025-T026, feature 002-llm-tool-router): a durable, JSON-on-disk
/// `IBindingStore` (`TgLLM.Core.IBindingStore`) that loads existing bindings on construction, so a
/// restart pointing at the same file restores them (SC-004). Deliberately left as a skeleton by the
/// Setup phase (T001) — this feature's US1 (T012-T020) MVP ships only the in-memory default
/// (`TgLLM.Core.InMemoryBindingStore`); the file-backed implementation is out of scope until US3.
module Placeholder =
    /// Nothing lives here yet; T026 replaces this file with the real `FileBindingStore` type.
    let pendingUserStory = "US3"
