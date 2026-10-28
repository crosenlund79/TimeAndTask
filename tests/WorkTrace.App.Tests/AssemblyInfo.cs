// All UI tests share one WPF dispatcher thread (one Application per process). Running classes in
// parallel would interleave them on that thread and mix up windows and binding-error capture.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
