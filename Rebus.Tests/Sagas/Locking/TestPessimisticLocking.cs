﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Activation;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Persistence.InMem;
using Rebus.Sagas;
using Rebus.Sagas.Locking;
using Rebus.Tests.Extensions;
using Rebus.Transport.InMem;

#pragma warning disable 1998

namespace Rebus.Tests.Sagas.Locking
{
    [TestFixture]
    public class TestPessimisticLocking : FixtureBase
    {
        [TestCase(2, 100)]
        [TestCase(5, 200)]
        [TestCase(10, 1000)]
        public async Task PessimisticLockingWorks(int numberOfSagas, int numberOfMessagesPerSaga)
        {
            using (var activator = new BuiltinHandlerActivator())
            {
                var correlationIds = Enumerable.Range(0, numberOfSagas)
                    .Select(i => $"correlation-id-{i}")
                    .ToList();

                var messages = correlationIds
                    .SelectMany(correlationId => Enumerable
                        .Range(0, numberOfMessagesPerSaga)
                        .Select(_ => correlationId))
                    .InRandomOrder()
                    .ToList();

                var counter = new SharedCounter(messages.Count);
                var numbers = new ConcurrentDictionary<string, int>();
                var inMemorySagaLocks = new InMemorySagaLocks();

                activator.Register(() => new SagaWithContention(counter, numbers));

                Configure.With(activator)
                    .Logging(l => l.Console(LogLevel.Info))
                    .Transport(t => t.UseInMemoryTransport(new InMemNetwork(), "pessimistic-locking-test"))
                    .Options(o =>
                    {
                        o.SetNumberOfWorkers(20);
                        o.SetMaxParallelism(100);

                        o.EnablePessimisticSagaLocking(acquireLockMaximumWaitTime: TimeSpan.FromSeconds(5))
                            .UseInMemoryLock(inMemorySagaLocks);
                    })
                    .Start();

                Console.WriteLine($"Sending {messages.Count} messages as quickly as possible...");

                var bus = activator.Bus;

                Task.WaitAll(messages.Select(message => bus.SendLocal(message)).ToArray());

                var timeoutSeconds = 10 + numberOfMessagesPerSaga * numberOfSagas / 100;

                Console.WriteLine($"Waiting for reset event (max {timeoutSeconds} s)...");

                counter.WaitForResetEvent(timeoutSeconds);

                Console.WriteLine("Checking!");

                foreach (var correlationId in correlationIds)
                {
                    Assert.That(numbers[correlationId], Is.EqualTo(numberOfMessagesPerSaga));
                }

                Assert.That(inMemorySagaLocks.Count, Is.EqualTo(0));
            }
        }

        class SagaWithContention : Saga<SagaWithContentionSagaData>, IAmInitiatedBy<string>
        {
            readonly SharedCounter _counter;
            readonly ConcurrentDictionary<string, int> _numbers;

            public SagaWithContention(SharedCounter counter, ConcurrentDictionary<string, int> numbers)
            {
                _counter = counter;
                _numbers = numbers;
            }

            protected override void CorrelateMessages(ICorrelationConfig<SagaWithContentionSagaData> config)
            {
                config.Correlate<string>(message => message, data => data.CorrelationId);
            }

            public async Task Handle(string message)
            {
                Data.NumberOfHandledMessages++;

                _numbers[message] = Data.NumberOfHandledMessages;

                _counter.Decrement();
            }
        }

        class SagaWithContentionSagaData : ISagaData
        {
            public Guid Id { get; set; }
            public int Revision { get; set; }
            public string CorrelationId { get; set; }
            public int NumberOfHandledMessages { get; set; }
        }
    }
}