using Xunit;

// WPF focus, native text input, BodyEditing and UI preferences are process-wide state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
