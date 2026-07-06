namespace TgLLM.Persistence.LiteDb

/// A durable, embedded-LiteDB `IBindingStore`: the second durable backend proving the store seam
/// generalizes beyond the file store, using a pure-managed single-file document store (no native
/// dependency). Expiry eviction is a collection delete by an indexed field. The real implementation
/// lands with the lifecycle work; this placeholder keeps the leaf project compiling until then.
module internal Placeholder =
    let internal reserved = ()
