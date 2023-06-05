using System;
using System.Globalization;
using System.Threading.Tasks;
using Polly.Core.Tests.Helpers;
using Polly.Hedging;
using Polly.Hedging.Controller;
using Polly.Hedging.Utils;
using Polly.Strategy;
using Polly.Utils;

namespace Polly.Core.Tests.Hedging.Controller;

public class HedgingExecutionContextTests : IDisposable
{
    private const string Handled = "Handled";
    private static readonly TimeSpan AssertTimeout = TimeSpan.FromSeconds(5);
    private readonly ResiliencePropertyKey<string> _myKey = new("my-key");
    private readonly HedgingHandler _hedgingHandler;
    private readonly CancellationTokenSource _cts;
    private readonly HedgingTimeProvider _timeProvider;
    private readonly List<TaskExecution> _createdExecutions = new();
    private readonly List<TaskExecution> _returnedExecutions = new();
    private readonly List<HedgingExecutionContext> _resets = new();
    private readonly ResilienceContext _resilienceContext;
    private readonly AutoResetEvent _onReset = new(false);
    private int _maxAttempts = 2;

    public HedgingExecutionContextTests()
    {
        _timeProvider = new HedgingTimeProvider();
        _cts = new CancellationTokenSource();
        _hedgingHandler = new HedgingHandler();
        _hedgingHandler.SetHedging<DisposableResult>(handler =>
        {
            handler.ShouldHandle = args => args switch
            {
                { Exception: ApplicationException } => PredicateResult.True,
                { Result: DisposableResult result } when result.Name == Handled => PredicateResult.True,
                _ => PredicateResult.False
            };

            handler.HedgingActionGenerator = args => Generator(args);
        });

        _resilienceContext = ResilienceContext.Get().Initialize<string>(false);
        _resilienceContext.CancellationToken = _cts.Token;
        _resilienceContext.Properties.Set(_myKey, "dummy");
    }

    public void Dispose()
    {
        _cts.Dispose();
        _onReset.Dispose();
    }

    [Fact]
    public void Ctor_Ok()
    {
        var context = Create();

        context.LoadedTasks.Should().Be(0);
        context.Snapshot.Context.Should().BeNull();

        context.Should().NotBeNull();
    }

    [Fact]
    public void Initialize_Ok()
    {
        var props = _resilienceContext.Properties;
        var context = Create();

        context.Initialize(_resilienceContext);

        context.Snapshot.Context.Should().Be(_resilienceContext);
        context.Snapshot.Context.Properties.Should().NotBeSameAs(props);
        context.Snapshot.OriginalProperties.Should().BeSameAs(props);
        context.Snapshot.OriginalCancellationToken.Should().Be(_cts.Token);
        context.Snapshot.Context.Properties.Should().HaveCount(1);
        context.IsInitialized.Should().BeTrue();
    }

    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [Theory]
    public async Task TryWaitForCompletedExecutionAsync_Initialized_Ok(int delay)
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        var delayTimeSpan = TimeSpan.FromSeconds(delay);

        var task = context.TryWaitForCompletedExecutionAsync(delayTimeSpan);

        _timeProvider.Advance(TimeSpan.FromHours(1));

        (await task).Should().BeNull();
    }

    [Fact]
    public async Task TryWaitForCompletedExecutionAsync_FinishedTask_Ok()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        await context.LoadExecutionAsync((_, _) => new Outcome<string>("dummy").AsValueTask(), "state");

        var task = await context.TryWaitForCompletedExecutionAsync(TimeSpan.Zero);

        task.Should().NotBeNull();
        task!.ExecutionTaskSafe!.IsCompleted.Should().BeTrue();
        task.Outcome.Result.Should().Be("dummy");
        task.AcceptOutcome();
        context.LoadedTasks.Should().Be(1);
    }

    [Fact]
    public async Task TryWaitForCompletedExecutionAsync_ConcurrentExecution_Ok()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        for (int i = 0; i < _maxAttempts - 1; i++)
        {
            await LoadExecutionAsync(context, TimeSpan.FromHours(1));
        }

        for (int i = 0; i < _maxAttempts; i++)
        {
            (await context.TryWaitForCompletedExecutionAsync(TimeSpan.Zero)).Should().BeNull();
        }

        _timeProvider.Advance(TimeSpan.FromDays(1));
        await context.TryWaitForCompletedExecutionAsync(TimeSpan.Zero);
        await context.Tasks[0].ExecutionTaskSafe!;
        context.Tasks[0].AcceptOutcome();
    }

    [Fact]
    public async Task TryWaitForCompletedExecutionAsync_SynchronousExecution_Ok()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        for (int i = 0; i < _maxAttempts - 1; i++)
        {
            await LoadExecutionAsync(context, TimeSpan.FromHours(1));
        }

        var task = context.TryWaitForCompletedExecutionAsync(System.Threading.Timeout.InfiniteTimeSpan).AsTask();
        task.Wait(20).Should().BeFalse();
        _timeProvider.Advance(TimeSpan.FromDays(1));
        await task;
        context.Tasks[0].AcceptOutcome();
    }

    [Fact]
    public async Task TryWaitForCompletedExecutionAsync_HedgedExecution_Ok()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        for (int i = 0; i < _maxAttempts - 1; i++)
        {
            await LoadExecutionAsync(context, TimeSpan.FromHours(1));
        }

        var hedgingDelay = TimeSpan.FromSeconds(5);
        var count = _timeProvider.DelayEntries.Count;
        var task = context.TryWaitForCompletedExecutionAsync(hedgingDelay).AsTask();
        task.Wait(20).Should().BeFalse();
        _timeProvider.DelayEntries.Should().HaveCount(count + 1);
        _timeProvider.DelayEntries.Last().Delay.Should().Be(hedgingDelay);
        _timeProvider.Advance(TimeSpan.FromDays(1));
        await task;
        await context.Tasks[0].ExecutionTaskSafe!;
        context.Tasks[0].AcceptOutcome();
    }

    [Fact]
    public async Task TryWaitForCompletedExecutionAsync_TwiceWhenSecondaryGeneratorNotRegistered_Ok()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        await context.LoadExecutionAsync((_, _) => new Outcome<string>("dummy").AsValueTask(), "state");
        await context.LoadExecutionAsync((_, _) => new Outcome<string>("dummy").AsValueTask(), "state");

        var task = await context.TryWaitForCompletedExecutionAsync(TimeSpan.Zero);

        task!.AcceptOutcome();
        context.LoadedTasks.Should().Be(1);
    }

    [Fact]
    public async Task TryWaitForCompletedExecutionAsync_TwiceWhenSecondaryGeneratorRegistered_Ok()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        await LoadExecutionAsync(context);
        await LoadExecutionAsync(context);

        Generator = args => () => Task.FromResult(new DisposableResult { Name = "secondary" });

        var task = await context.TryWaitForCompletedExecutionAsync(TimeSpan.Zero);
        task!.Type.Should().Be(HedgedTaskType.Primary);
        task!.AcceptOutcome();
        context.LoadedTasks.Should().Be(2);
        context.Tasks[0].Type.Should().Be(HedgedTaskType.Primary);
        context.Tasks[1].Type.Should().Be(HedgedTaskType.Secondary);
    }

    [Fact]
    public async Task LoadExecutionAsync_MaxTasks_NoMoreTasksAdded()
    {
        _maxAttempts = 3;
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        for (int i = 0; i < _maxAttempts; i++)
        {
            (await LoadExecutionAsync(context)).Loaded.Should().BeTrue();
        }

        (await LoadExecutionAsync(context)).Loaded.Should().BeFalse();

        context.LoadedTasks.Should().Be(_maxAttempts);
        context.Tasks[0].AcceptOutcome();
        _returnedExecutions.Should().HaveCount(0);
    }

    [Fact]
    public async Task LoadExecutionAsync_EnsureCorrectAttemptNumber()
    {
        var attempt = -1;
        var context = Create();
        context.Initialize(_resilienceContext);
        Generator = args =>
        {
            attempt = args.Attempt;
            return null;
        };

        await LoadExecutionAsync(context);
        await LoadExecutionAsync(context);

        // primary is 0, this one is 1
        attempt.Should().Be(1);
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task LoadExecutionAsync_NoMoreSecondaryTasks_AcceptFinishedOutcome(bool allExecuted)
    {
        _maxAttempts = 4;
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(allExecuted ? TimeSpan.Zero : TimeSpan.FromHours(1));

        // primary
        await LoadExecutionAsync(context);

        // secondary
        await LoadExecutionAsync(context);

        // secondary couldn't be created
        if (allExecuted)
        {
            await context.TryWaitForCompletedExecutionAsync(TimeSpan.Zero);
            await context.TryWaitForCompletedExecutionAsync(TimeSpan.Zero);
        }

        var pair = await LoadExecutionAsync(context);
        pair.Loaded.Should().BeFalse();

        _returnedExecutions.Count.Should().Be(1);
        if (allExecuted)
        {
            pair.Outcome.Should().NotBeNull();
            context.Tasks[0].IsAccepted.Should().BeTrue();
        }
        else
        {
            pair.Outcome.Should().BeNull();
            context.Tasks[0].IsAccepted.Should().BeFalse();
        }

        context.Tasks[0].AcceptOutcome();
    }

    [Fact]
    public async Task LoadExecution_NoMoreTasks_Throws()
    {
        _maxAttempts = 0;
        var context = Create();
        context.Initialize(_resilienceContext);

        await context.Invoking(c => LoadExecutionAsync(c)).Should().ThrowAsync<InvalidOperationException>();
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task Complete_EnsureOriginalContextPreparedWithAcceptedOutcome(bool primary)
    {
        // arrange
        var type = primary ? HedgedTaskType.Primary : HedgedTaskType.Secondary;
        var context = Create();
        var originalProps = _resilienceContext.Properties;
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.Zero);
        await ExecuteAllTasksAsync(context, 2);
        context.Tasks.First(v => v.Type == type).AcceptOutcome();

        // act
        context.Complete();

        // assert
        _resilienceContext.Properties.Should().BeSameAs(originalProps);
        if (primary)
        {
            _resilienceContext.Properties.Should().HaveCount(1);
            _resilienceContext.ResilienceEvents.Should().HaveCount(0);
        }
        else
        {
            _resilienceContext.Properties.Should().HaveCount(2);
            _resilienceContext.ResilienceEvents.Should().HaveCount(1);
        }
    }

    [Fact]
    public void Complete_NoTasks_EnsureCleaned()
    {
        var props = _resilienceContext.Properties;
        var context = Create();
        context.Initialize(_resilienceContext);
        context.Complete();
        _resilienceContext.Properties.Should().BeSameAs(props);
    }

    [Fact]
    public async Task Complete_NoAcceptedTasks_ShouldNotThrow()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.Zero);
        await ExecuteAllTasksAsync(context, 2);

        context.Invoking(c => c.Complete()).Should().NotThrow();
    }

    [Fact]
    public async Task Complete_MultipleAcceptedTasks_ShouldNotThrow()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.Zero);
        await ExecuteAllTasksAsync(context, 2);
        context.Tasks[0].AcceptOutcome();
        context.Tasks[1].AcceptOutcome();

        context.Invoking(c => c.Complete()).Should().NotThrow();
    }

    [Fact]
    public async Task Complete_EnsurePendingTasksCleaned()
    {
        using var assertPrimary = new ManualResetEvent(false);
        using var assertSecondary = new ManualResetEvent(false);

        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.FromHours(1));
        (await LoadExecutionAsync(context)).Execution!.OnReset = (execution) =>
        {
            execution.Outcome.Result.Should().BeOfType<DisposableResult>();
            execution.Outcome.Exception.Should().BeNull();
            assertPrimary.Set();
        };
        (await LoadExecutionAsync(context)).Execution!.OnReset = (execution) =>
        {
            execution.Outcome.Exception.Should().BeAssignableTo<OperationCanceledException>();
            assertSecondary.Set();
        };

        await context.TryWaitForCompletedExecutionAsync(System.Threading.Timeout.InfiniteTimeSpan);

        var pending = context.Tasks[1].ExecutionTaskSafe!;
        pending.Wait(10).Should().BeFalse();

        context.Tasks[0].AcceptOutcome();
        context.Complete();

        await pending;

        assertPrimary.WaitOne(AssertTimeout).Should().BeTrue();
        assertSecondary.WaitOne(AssertTimeout).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_EnsureCleaned()
    {
        var context = Create();
        context.Initialize(_resilienceContext);
        ConfigureSecondaryTasks(TimeSpan.Zero);
        await ExecuteAllTasksAsync(context, 2);
        context.Tasks[0].AcceptOutcome();

        context.Complete();

        context.LoadedTasks.Should().Be(0);
        context.Snapshot.Context.Should().BeNull();

        _onReset.WaitOne(AssertTimeout);
        _resets.Count.Should().Be(1);
        _returnedExecutions.Count.Should().Be(2);
    }

    private async Task ExecuteAllTasksAsync(HedgingExecutionContext context, int count)
    {
        for (int i = 0; i < count; i++)
        {
            (await LoadExecutionAsync(context)).Loaded.Should().BeTrue();
            await context.TryWaitForCompletedExecutionAsync(System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private async Task<HedgingExecutionContext.ExecutionInfo<DisposableResult>> LoadExecutionAsync(HedgingExecutionContext context, TimeSpan? primaryDelay = null, bool error = false)
    {
        return await context.LoadExecutionAsync(
            async (c, _) =>
            {
                if (primaryDelay != null)
                {
                    await _timeProvider.Delay(primaryDelay.Value, c.CancellationToken);
                }

                if (error)
                {
                    throw new InvalidOperationException("Forced error.");
                }

                return new Outcome<DisposableResult>(new DisposableResult { Name = "primary" });
            },
            "state");
    }

    private void ConfigureSecondaryTasks(params TimeSpan[] delays)
    {
        Generator = args =>
        {
            var attempt = args.Attempt - 1;

            if (attempt >= delays.Length)
            {
                return null;
            }

            args.Context.AddResilienceEvent(new ReportedResilienceEvent("dummy-event"));

            return async () =>
            {
                args.Context.Properties.Set(new ResiliencePropertyKey<int>(attempt.ToString(CultureInfo.InvariantCulture)), attempt);
                await _timeProvider.Delay(delays[attempt], args.Context.CancellationToken);
                return new DisposableResult(delays[attempt].ToString());
            };
        };
    }

    private Func<HedgingActionGeneratorArguments<DisposableResult>, Func<Task<DisposableResult>>?> Generator { get; set; } = args => () => Task.FromResult(new DisposableResult { Name = Handled });

    private HedgingExecutionContext Create()
    {
        var handler = _hedgingHandler.CreateHandler()!;
        var pool = new ObjectPool<TaskExecution>(
            () =>
            {
                var execution = new TaskExecution(handler, CancellationTokenSourcePool.Create(_timeProvider));
                _createdExecutions.Add(execution);
                return execution;
            },
            execution =>
            {
                _returnedExecutions.Add(execution);
                return true;
            });

        return new(pool, _timeProvider, _maxAttempts, context =>
        {
            _resets.Add(context);
            _onReset.Set();
        });
    }
}
