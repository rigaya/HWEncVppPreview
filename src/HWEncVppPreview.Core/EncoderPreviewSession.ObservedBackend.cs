namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    private sealed class ObservedBackend(IPreviewBackend inner, EncoderPreviewSession owner) : IPreviewBackend, IRawOwnedBackend
    {
        public Task InitializeEncoderAsync(string encoderPath, CancellationToken cancellationToken = default)
            => inner.InitializeEncoderAsync(encoderPath, cancellationToken);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => inner.GetHistoryRequirementAsync(request, cancellationToken);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => RunObservedAsync(request, token => inner.RunAsync(request, sink, token), cancellationToken);

        public Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken cancellationToken = default)
            => RunObservedAsync(request, token => inner is IRawOwnedBackend owned
                ? owned.RunOwnedAsync(request, sink, scope, token)
                : Task.FromException<PreviewRunResult>(new InvalidOperationException("owned backendが必要です。")), cancellationToken);

        private async Task<PreviewRunResult> RunObservedAsync(PreviewRequest request,
            Func<CancellationToken, Task<PreviewRunResult>> run, CancellationToken cancellationToken)
        {
            var context = owner.ContextForPhysical(request);
            if (context?.Kind == RequestKind.Before) owner.ObserveBeforePhysical(context, request);
            using var cancellation = context?.BeforeCancellation == null ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.BeforeCancellation.Token);
            try
            {
                var result = await run(cancellation?.Token ?? cancellationToken).ConfigureAwait(false);
                if (context != null) context.Diagnostics = result.StandardError;
                return result;
            }
            catch (PreviewBackendException exception)
            {
                if (context != null) context.Diagnostics = exception.StandardError;
                throw;
            }
        }
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

}
