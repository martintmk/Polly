using System.Diagnostics.CodeAnalysis;
using Polly.Hedging.Utils;
using Polly.Telemetry;

namespace Polly.Hedging;

internal sealed class HedgingResilienceStrategy<T> : ResilienceStrategy<T>
{
    private readonly TimeProvider _timeProvider;
    private readonly ResilienceStrategyTelemetry _telemetry;
    private readonly HedgingController<T> _controller;

    public HedgingResilienceStrategy(
        TimeSpan hedgingDelay,
        int maxHedgedAttempts,
        HedgingHandler<T> hedgingHandler,
        Func<OnHedgingArguments<T>, ValueTask>? onHedging,
        Func<HedgingDelayGeneratorArguments, ValueTask<TimeSpan>>? hedgingDelayGenerator,
        TimeProvider timeProvider,
        ResilienceStrategyTelemetry telemetry)
    {
        HedgingDelay = hedgingDelay;
        TotalAttempts = maxHedgedAttempts + 1; // include the initial attempt
        DelayGenerator = hedgingDelayGenerator;
        _timeProvider = timeProvider;
        HedgingHandler = hedgingHandler;
        OnHedging = onHedging;

        _telemetry = telemetry;
        _controller = new HedgingController<T>(telemetry, timeProvider, HedgingHandler, TotalAttempts);
    }

    public TimeSpan HedgingDelay { get; }

    public int TotalAttempts { get; }

    public Func<HedgingDelayGeneratorArguments, ValueTask<TimeSpan>>? DelayGenerator { get; }

    public HedgingHandler<T> HedgingHandler { get; }

    public Func<OnHedgingArguments<T>, ValueTask>? OnHedging { get; }

    [ExcludeFromCodeCoverage] // coverlet issue
    protected internal override async ValueTask<Outcome<T>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<T>>> callback,
        ResilienceContext context,
        TState state)
    {
        // create hedging execution context
        var hedgingContext = _controller.GetContext(context);

        try
        {
            return await ExecuteCoreAsync(hedgingContext, callback, context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        finally
        {
            await hedgingContext.DisposeAsync().ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    private async ValueTask<Outcome<T>> ExecuteCoreAsync<TState>(
        HedgingExecutionContext<T> hedgingContext,
        Func<ResilienceContext, TState, ValueTask<Outcome<T>>> callback,
        ResilienceContext context,
        TState state)
    {
        // Capture the original cancellation token so it stays the same while hedging is executing.
        // If we do not do this the inner strategy can replace the cancellation token and with the concurrent
        // nature of hedging this can cause issues.
        var cancellationToken = context.CancellationToken;
        var continueOnCapturedContext = context.ContinueOnCapturedContext;

        var attempt = -1;
        while (true)
        {
            attempt++;
            var start = _timeProvider.GetTimestamp();
            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome.FromException<T>(new OperationCanceledException(cancellationToken).TrySetStackTrace());
            }

            var loadedExecution = await hedgingContext.LoadExecutionAsync(callback, state).ConfigureAwait(context.ContinueOnCapturedContext);

            if (loadedExecution.Outcome is Outcome<T> outcome)
            {
                return outcome;
            }

            var delay = await GetHedgingDelayAsync(context, hedgingContext.LoadedTasks).ConfigureAwait(continueOnCapturedContext);
            var execution = await hedgingContext.TryWaitForCompletedExecutionAsync(delay).ConfigureAwait(continueOnCapturedContext);
            if (execution is null)
            {
                // If completedHedgedTask is null it indicates that we still do not have any finished hedged task within the hedging delay.
                // We will create additional hedged task in the next iteration.
                await HandleOnHedgingAsync(
                    new OnHedgingArguments<T>(context, null, attempt, duration: delay)).ConfigureAwait(context.ContinueOnCapturedContext);
                continue;
            }

            outcome = execution.Outcome;

            if (!execution.IsHandled)
            {
                execution.AcceptOutcome();
                return outcome;
            }

            var executionTime = _timeProvider.GetElapsedTime(start);
            await HandleOnHedgingAsync(
                new OnHedgingArguments<T>(context, outcome, attempt, executionTime)).ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    private async ValueTask HandleOnHedgingAsync(OnHedgingArguments<T> args)
    {
        _telemetry.Report<OnHedgingArguments<T>, T>(new(ResilienceEventSeverity.Warning, HedgingConstants.OnHedgingEventName), args.Context, default, args);

        if (OnHedging is not null)
        {
            // If nothing has been returned or thrown yet, the result is a transient failure,
            // and other hedged request will be awaited.
            // Before it, one needs to perform the task adjacent to each hedged call.
            await OnHedging(args).ConfigureAwait(args.Context.ContinueOnCapturedContext);
        }
    }

    internal ValueTask<TimeSpan> GetHedgingDelayAsync(ResilienceContext context, int attempt)
    {
        if (DelayGenerator == null)
        {
            return new ValueTask<TimeSpan>(HedgingDelay);
        }

        return DelayGenerator(new HedgingDelayGeneratorArguments(context, attempt));
    }
}
