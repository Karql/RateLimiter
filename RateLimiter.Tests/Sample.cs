using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ComposableAsync;
using FluentAssertions;
using RateLimiter.Tests.TestClass;
using ComposableAsync.Factory;
using System.Diagnostics;

namespace RateLimiter.Tests
{
    public class Sample
    {
        private readonly ITestOutputHelper _Output;

        public Sample(ITestOutputHelper output)
        {
            _Output = output;
        }

        private void ConsoleIt()
        {
            _Output.WriteLine($"{DateTime.Now:MM/dd/yyy HH:mm:ss.fff}");
        }

        [Fact(Skip = "for demo purpose only")]
        public async Task SimpleUsage()
        {
            var timeConstraint = TimeLimiter.GetFromMaxCountByInterval(5, TimeSpan.FromSeconds(1));

            for (int i = 0; i < 1000; i++)
            {
                await timeConstraint.Enqueue(() => ConsoleIt());
            }
        }

        [Fact]
        public async Task SimpleUsageWithCancellation()
        {
            var timeConstraint = TimeLimiter.GetFromMaxCountByInterval(3, TimeSpan.FromSeconds(1));
            var cts = new CancellationTokenSource(1100);

            for (var i = 0; i < 1000; i++)
            {
                try
                {
                    await timeConstraint.Enqueue(() =>ConsoleIt(), cts.Token);
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        [Fact]
        public async Task SimpleUsageAwaitable()
        {
            var timeConstraint = TimeLimiter.GetFromMaxCountByInterval(5, TimeSpan.FromSeconds(1));
            for (var i = 0; i < 50; i++)
            {
                await timeConstraint;
                ConsoleIt();
            }
        }

        [Fact]
        public async Task SimpleUsageAwaitableCancellable()
        {
            var timeConstraint = TimeLimiter.GetFromMaxCountByInterval(5, TimeSpan.FromSeconds(1));
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.1));
            var token = cts.Token;
            var count = 0;

            Func<Task> cancellable = async () =>
            {
                while (true)
                {
                    await timeConstraint;
                    token.ThrowIfCancellationRequested();
                    ConsoleIt();
                    count++;
                }
            };

            await cancellable.Should().ThrowAsync<OperationCanceledException>();
            count.Should().Be(10);
        }
       
        [Fact]
        public async Task UsageWithFactory()
        {
            var wrapped = new TimeLimited(_Output);
            var timeConstraint = TimeLimiter.GetFromMaxCountByInterval(5, TimeSpan.FromMilliseconds(100));
            var timeLimited = timeConstraint.Proxify<ITimeLimited>(wrapped);

            var watch = Stopwatch.StartNew();

            for (var i = 0; i < 50; i++)
            {
                await timeLimited.GetValue();
            }

            watch.Stop();
            watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(900));
            _Output.WriteLine($"Elapsed: {watch.Elapsed}");

            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(110));

            Func<Task> cancellable = async () =>
            {
                while (true) 
                {
                    await timeLimited.GetValue(cts.Token);
                }
            };

            await cancellable.Should().ThrowAsync<OperationCanceledException>();
             
            var res = await timeLimited.GetValue();
            res.Should().BeLessOrEqualTo(56);
        }

        [Fact(Skip = "for demo purpose only")]
        public async Task TestOneThread()
        {
            var constraint = new CountByIntervalAwaitableConstraint(5, TimeSpan.FromSeconds(1));
            var constraint2 = new CountByIntervalAwaitableConstraint(1, TimeSpan.FromMilliseconds(100));
            var timeConstraint = TimeLimiter.Compose(constraint, constraint2);

            for (var i = 0; i < 1000; i++)
            {
                await timeConstraint.Enqueue(() => ConsoleIt());
            }
        }

        [Fact(Skip = "for demo purpose only")]
        public async Task Test100Thread()
        {
            var constraint = new CountByIntervalAwaitableConstraint(5, TimeSpan.FromSeconds(1));
            var constraint2 = new CountByIntervalAwaitableConstraint(1, TimeSpan.FromMilliseconds(100));
            var timeConstraint = TimeLimiter.Compose(constraint, constraint2);

            var tasks = new List<Task>();

            for (int i = 0; i < 100; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        await timeConstraint.Enqueue(() => ConsoleIt());
                    }
                }));
            }

            await Task.WhenAll(tasks.ToArray());
        }

        //[Fact(Skip = "for demo purpose only")]
        [Fact]
        public async Task ShouldBeAbleToContinueAfterCancelation()
        {
            var constraint1 = new CountByIntervalAwaitableConstraint(1, TimeSpan.FromSeconds(1));
            var constraint2 = new CountByIntervalAwaitableConstraint(10, TimeSpan.FromMinutes(1));
            var timeConstraint = TimeLimiter.Compose(constraint1, constraint2);

            Func<CancellationToken, Task> fun = async (token) =>
            {
                await timeConstraint.Enqueue(async () =>
                {
                    ConsoleIt();
                }, token);
            };

            var cts = new CancellationTokenSource(100);

            try
            {
                await Task.WhenAll(fun(cts.Token), fun(cts.Token));
                // task 1 executed immediately
                // task 2 waiting for constraint1 (constraint2 return new DisposeAction(OnEnded);) 
            }

            catch
            {
                // when cancellation happend DisposeAction for task 2 wont be call, and _Semaphore wont be released
                ;
            }

            cts.Dispose();
            cts = new CancellationTokenSource(60000); // 1 min to not cancelled on brake point - for test could be e.g. 5s

            // in this place deadlock occurs on CountByIntervalAwaitableConstraint:
            // public async Task<IDisposable> WaitForReadiness(CancellationToken cancellationToken)
            // {
            //      await _Semaphore.WaitAsync(cancellationToken); <- this semaphore is not released
            Func<Task> t = async () => await Task.WhenAll(fun(cts.Token), fun(cts.Token));
            await t.Should().NotThrowAsync();
            cts.IsCancellationRequested.Should().BeFalse();
            cts.Dispose();
        }
    }
}
