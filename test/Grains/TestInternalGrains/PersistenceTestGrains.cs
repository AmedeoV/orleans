using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Storage;

namespace UnitTests.Grains
{
    internal static class TestRuntimeEnvironmentUtility
    {
        public static string CaptureRuntimeEnvironment()
        {
            var callStack = Utils.GetStackTrace(1); // Don't include this method in stack trace
            return String.Format(
                "   TaskScheduler={0}" + Environment.NewLine
                + "   RuntimeContext={1}" + Environment.NewLine
                + "   WorkerPoolThread={2}" + Environment.NewLine
                + "   Thread.CurrentThread.ManagedThreadId={4}" + Environment.NewLine
                + "   StackTrace=" + Environment.NewLine
                + "   {5}",
                TaskScheduler.Current,
                RuntimeContext.Current,
                Thread.CurrentThread.Name,
                Thread.CurrentThread.ManagedThreadId,
                callStack);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class PersistenceTestGrainState
    {
        public PersistenceTestGrainState()
        {
            SortedDict = new SortedDictionary<int, int>();
        }

        [Id(0)]
        public int Field1 { get; set; }
        [Id(1)]
        public string Field2 { get; set; }
        [Id(2)]
        public SortedDictionary<int, int> SortedDict { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class PersistenceGenericGrainState<T>
    {
        [Id(0)]
        public T Field1 { get; set; }
        [Id(1)]
        public string Field2 { get; set; }
        [Id(2)]
        public SortedDictionary<T, T> SortedDict { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceTestGrain : Grain<PersistenceTestGrainState>, IPersistenceTestGrain
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<bool> CheckStateInit()
        {
            Assert.NotNull(State);
            Assert.Equal(0, State.Field1);
            Assert.Null(State.Field2);
            //Assert.NotNull(State.Field3, "Null Field3");
            //Assert.AreEqual(0, State.Field3.Count, "Field3 = {0}", String.Join("'", State.Field3));
            Assert.NotNull(State.SortedDict);
            return Task.FromResult(true);
        }

        public Task<string> CheckProviderType()
        {
            IGrainStorage grainStorage = GrainStorageExtensions.GetGrainStorage(GetType(), this.ServiceProvider);
            Assert.NotNull(grainStorage);
            return Task.FromResult(grainStorage.GetType().FullName);
        }

        public Task DoSomething()
        {
            return Task.CompletedTask;
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            State.SortedDict[val] = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync();
            return State.Field1;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public async Task DoDelete()
        {
            await ClearStateAsync();
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceTestGenericGrain<T> : PersistenceTestGrain, IPersistenceTestGenericGrain<T>
    {
        //...
    }

    [Orleans.Providers.StorageProvider(ProviderName = "ErrorInjector")]
    public class PersistenceProviderErrorGrain : Grain<PersistenceTestGrainState>, IPersistenceProviderErrorGrain
    {
        private readonly string _id = Guid.NewGuid().ToString();

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync();
            return State.Field1;
        }

        public Task<string> GetActivationId() => Task.FromResult(_id);
    }

    [Orleans.Providers.StorageProvider(ProviderName = "ErrorInjector")]
    public class PersistenceUserHandledErrorGrain : Grain<PersistenceTestGrainState>, IPersistenceUserHandledErrorGrain
    {
        private readonly ILogger logger;
        private readonly DeepCopier<PersistenceTestGrainState> copier;

        public PersistenceUserHandledErrorGrain(ILoggerFactory loggerFactory, DeepCopier<PersistenceTestGrainState> copier)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
            this.copier = copier;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public async Task DoWrite(int val, bool recover)
        {
            var original = this.copier.Copy(State);
            try
            {
                State.Field1 = val;
                await WriteStateAsync();
            }
            catch (Exception exc)
            {
                if (!recover) throw;

                this.logger.Warn(0, "Grain is handling error in DoWrite - Resetting value to " + original, exc);
                State = (PersistenceTestGrainState)original;
            }
        }

        public async Task<int> DoRead(bool recover)
        {
            var original = this.copier.Copy(State);
            try
            {
                await ReadStateAsync();
            }
            catch (Exception exc)
            {
                if (!recover) throw;

                this.logger.Warn(0, "Grain is handling error in DoRead - Resetting value to " + original, exc);
                State = (PersistenceTestGrainState)original;
            }
            return State.Field1;
        }
    }

    public class PersistenceProviderErrorProxyGrain : Grain, IPersistenceProviderErrorProxyGrain
    {
        private readonly string _id = Guid.NewGuid().ToString();

        public Task<int> GetValue(IPersistenceProviderErrorGrain other) => other.GetValue();

        public Task DoWrite(int val, IPersistenceProviderErrorGrain other) => other.DoWrite(val);

        public Task<int> DoRead(IPersistenceProviderErrorGrain other) => other.DoRead();

        public Task<string> GetActivationId() => Task.FromResult(_id);
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceErrorGrain : Grain<PersistenceTestGrainState>, IPersistenceErrorGrain
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task DoWriteError(int val, bool errorBeforeUpdate)
        {
            if (errorBeforeUpdate) throw new ApplicationException("Before Update");
            State.Field1 = val;
            await WriteStateAsync();
            throw new ApplicationException("After Update");
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public async Task<int> DoReadError(bool errorBeforeRead)
        {
            if (errorBeforeRead) throw new ApplicationException("Before Read");
            await ReadStateAsync(); // Attempt to re-read state from store
            throw new ApplicationException("After Read");
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MissingProvider")]
    public class BadProviderTestGrain : Grain<PersistenceTestGrainState>, IBadProviderTestGrain
    {
        private readonly ILogger logger;

        public BadProviderTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger.Warn(1, "OnActivateAsync");
            return Task.CompletedTask;
        }

        public Task DoSomething()
        {
            this.logger.Warn(1, "DoSomething");
            throw new ApplicationException(
                "BadProviderTestGrain.DoSomething should never get called when provider is missing");
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceNoStateTestGrain : Grain, IPersistenceNoStateTestGrain
    {
        private readonly ILogger logger;

        public PersistenceNoStateTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger.Info(1, "OnActivateAsync");
            return Task.CompletedTask;
        }

        public Task DoSomething()
        {
            this.logger.Info(1, "DoSomething");
            return Task.CompletedTask;
        }
    }

    public class ServiceIdGrain : Grain, IServiceIdGrain
    {
        private readonly IOptions<ClusterOptions> clusterOptions;

        public ServiceIdGrain(IOptions<ClusterOptions> clusterOptions)
        {
            this.clusterOptions = clusterOptions;
        }

        public Task<string> GetServiceId()
        {
            return Task.FromResult(clusterOptions.Value.ServiceId);
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
    public class GrainStorageTestGrain : Grain<PersistenceTestGrainState>,
        IGrainStorageTestGrain, IGrainStorageTestGrain_LongKey
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
    public class GrainStorageGenericGrain<T> : Grain<PersistenceGenericGrainState<T>>,
        IGrainStorageGenericGrain<T>
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<T> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(T val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<T> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
    public class GrainStorageTestGrainExtendedKey : Grain<PersistenceTestGrainState>,
        IGrainStorageTestGrain_GuidExtendedKey, IGrainStorageTestGrain_LongExtendedKey
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task<string> GetExtendedKeyValue()
        {
            string extKey;
            _ = this.GetPrimaryKey(out extKey);
            return Task.FromResult(extKey);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "DDBStore")]
    public class AWSStorageTestGrain : Grain<PersistenceTestGrainState>,
        IAWSStorageTestGrain, IAWSStorageTestGrain_LongKey
    {
        private readonly string _id = Guid.NewGuid().ToString();

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task<string> GetActivationId() => Task.FromResult(_id);

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "DDBStore")]
    public class AWSStorageGenericGrain<T> : Grain<PersistenceGenericGrainState<T>>,
        IAWSStorageGenericGrain<T>
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<T> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(T val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<T> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "DDBStore")]
    public class AWSStorageTestGrainExtendedKey : Grain<PersistenceTestGrainState>,
        IAWSStorageTestGrain_GuidExtendedKey, IAWSStorageTestGrain_LongExtendedKey
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task<string> GetExtendedKeyValue()
        {
            string extKey;
            _ = this.GetPrimaryKey(out extKey);
            return Task.FromResult(extKey);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStorageEmulator")]
    public class MemoryStorageTestGrain : Grain<MemoryStorageTestGrain.NestedPersistenceTestGrainState>,
        IMemoryStorageTestGrain
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync();
        }

        [Serializable]
        [GenerateSerializer]
        public class NestedPersistenceTestGrainState
        {
            [Id(0)]
            public int Field1 { get; set; }
            [Id(1)]
            public string Field2 { get; set; }
            [Id(2)]
            public SortedDictionary<int, int> SortedDict { get; set; }
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class UserState
    {
        public UserState()
        {
            Friends = new List<IUser>();
        }

        [Id(0)]
        public string Name { get; set; }
        [Id(1)]
        public string Status { get; set; }
        [Id(2)]
        public List<IUser> Friends { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class DerivedUserState : UserState
    {
        [Id(0)]
        public int Field1 { get; set; }
        [Id(1)]
        public int Field2 { get; set; }
    }

    /// <summary>
    /// Orleans grain implementation class.
    /// </summary>
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStorageEmulator")]
    public class UserGrain : Grain<DerivedUserState>, IUser
    {
        public Task SetName(string name)
        {
            State.Name = name;
            return WriteStateAsync();
        }

        public Task<string> GetStatus()
        {
            return Task.FromResult(String.Format("{0} : {1}", State.Name, State.Status));
        }

        public Task<string> GetName()
        {
            return Task.FromResult(State.Name);
        }

        public Task UpdateStatus(string status)
        {
            State.Status = status;
            return WriteStateAsync();
        }

        public Task AddFriend(IUser friend)
        {
            if (!State.Friends.Contains(friend))
                State.Friends.Add(friend);
            else
                throw new Exception("Already a friend.");

            return Task.CompletedTask;
        }

        public Task<List<IUser>> GetFriends()
        {
            return Task.FromResult(State.Friends);
        }

        public async Task<string> GetFriendsStatuses()
        {
            var sb = new StringBuilder();
            var promises = new List<Task<string>>();

            foreach (var friend in State.Friends)
                promises.Add(friend.GetStatus());

            var friends = await Task.WhenAll(promises);

            foreach (var f in friends)
            {
                sb.AppendLine(f);
            }

            return sb.ToString();
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class StateForIReentrentGrain
    {
        public StateForIReentrentGrain()
        {
            DictOne = new Dictionary<string, int>();
            DictTwo = new Dictionary<string, int>();
        }

        [Id(0)]
        public int One { get; set; }
        [Id(1)]
        public int Two { get; set; }
        [Id(2)]
        public Dictionary<string, int> DictOne { get; set; }
        [Id(3)]
        public Dictionary<string, int> DictTwo { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    [Reentrant]
    public class ReentrentGrainWithState : Grain<StateForIReentrentGrain>, IReentrentGrainWithState
    {
        private const int Multiple = 100;

        private IReentrentGrainWithState _other;
        private IGrainContext _context;
        private TaskScheduler _scheduler;
        private ILogger logger;
        private bool executing;
        private Task outstandingWriteStateOperation;

        public ReentrentGrainWithState(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _context = RuntimeContext.Current;
            _scheduler = TaskScheduler.Current;
            executing = false;
            return base.OnActivateAsync(cancellationToken);
        }

        // When reentrant grain is doing WriteStateAsync, etag violations are posssible due to concurent writes.
        // The solution is to serialize all writes, and make sure only a single write is outstanind at any moment in time.
        // No deadlocks are posssible with that approach, since all WriteStateAsync go to the store, which does not issue call into grains,
        // thus cycle of calls is not posssible.
        // Implementaton: need to use While and not if, due to the same "early check becomes later invalid" standard problem, like in conditional variables.
        private async Task PerformSerializedStateUpdate()
        {
            while (outstandingWriteStateOperation != null)
            {
                await outstandingWriteStateOperation;
            }
            outstandingWriteStateOperation = WriteStateAsync();
            await outstandingWriteStateOperation;
            outstandingWriteStateOperation = null;
        }

        public Task Setup(IReentrentGrainWithState other)
        {
            logger.Info("Setup");
            _other = other;
            return Task.CompletedTask;
        }

        public async Task SetOne(int val)
        {
            logger.Info("SetOne Start");
            CheckRuntimeEnvironment();
            var iStr = val.ToString(CultureInfo.InvariantCulture);
            State.One = val;
            State.DictOne[iStr] = val;
            State.DictTwo[iStr] = val;
            CheckRuntimeEnvironment();
            await PerformSerializedStateUpdate();
            CheckRuntimeEnvironment();
        }

        public async Task SetTwo(int val)
        {
            logger.Info("SetTwo Start");
            CheckRuntimeEnvironment();
            var iStr = val.ToString(CultureInfo.InvariantCulture);
            State.Two = val;
            State.DictTwo[iStr] = val;
            State.DictOne[iStr] = val;
            CheckRuntimeEnvironment();
            await PerformSerializedStateUpdate();
            CheckRuntimeEnvironment();
        }

        public async Task Test1()
        {
            logger.Info(" ==================================== Test1 Started");
            CheckRuntimeEnvironment();
            for (var i = 1*Multiple; i < 2*Multiple; i++)
            {
                var t1 = SetOne(i);
                await t1;
                CheckRuntimeEnvironment();

                var t2 = PerformSerializedStateUpdate();
                await t2;
                CheckRuntimeEnvironment();

                var t3 = _other.SetTwo(i);
                await t3;
                CheckRuntimeEnvironment();

                var t4 = PerformSerializedStateUpdate();
                await t4;
                CheckRuntimeEnvironment();
            }
            CheckRuntimeEnvironment();
            logger.Info(" ==================================== Test1 Done");
        }

        public async Task Test2()
        {
            logger.Info("==================================== Test2 Started");
            CheckRuntimeEnvironment();
            for (var i = 2*Multiple; i < 3*Multiple; i++)
            {
                var t1 = _other.SetOne(i);
                await t1;
                CheckRuntimeEnvironment();

                var t2 = PerformSerializedStateUpdate();
                await t2;
                CheckRuntimeEnvironment();

                var t3 = SetTwo(i);
                await t3;
                CheckRuntimeEnvironment();

                var t4 = PerformSerializedStateUpdate();
                await t4;
                CheckRuntimeEnvironment();
            }
            CheckRuntimeEnvironment();
            logger.Info(" ==================================== Test2 Done");
        }

        public async Task Task_Delay(bool doStart)
        {
            var wrapper = new Task<Task>(async () =>
            {
                logger.Info("Before Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(1);
                logger.Info("After Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(2);
                logger.Info("After Task.Delay #2 TaskScheduler.Current=" + TaskScheduler.Current);
            });

            if (doStart)
            {
                wrapper.Start(); // THIS IS THE KEY STEP!
            }

            await wrapper.Unwrap();
        }

        private async Task DoDelay(int i)
        {
            logger.Info("Before Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
            await Task.Delay(1);
            logger.Info("After Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
        }

        private void CheckRuntimeEnvironment()
        {
            if (executing)
            {
                var errorMsg = "Found out that this grain is already in the middle of execution."
                               + " Single threaded-ness violation!\n" +
                               TestRuntimeEnvironmentUtility.CaptureRuntimeEnvironment();
                this.logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                throw new Exception(errorMsg);
                //Environment.Exit(1);
            }

            if (RuntimeContext.Current == null)
            {
                var errorMsg = "Found RuntimeContext.Current == null.\n" + TestRuntimeEnvironmentUtility.CaptureRuntimeEnvironment();
                this.logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                throw new Exception(errorMsg);
                //Environment.Exit(1);
            }

            var context = RuntimeContext.Current;
            var scheduler = TaskScheduler.Current;

            executing = true;
            Assert.Equal(_scheduler, scheduler);
            Assert.Equal(_context, context);
            Assert.NotNull(context);
            executing = false;
        }
    }

    internal class NonReentrentStressGrainWithoutState : Grain, INonReentrentStressGrainWithoutState
    {
        private const int Multiple = 100;
        private ILogger logger;
        private bool executing;
        private const int LEVEL = 2; // level 2 is enough to repro the problem.

        private static int _counter = 1;
        private int _id;

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _id = _counter++;
            var loggerFactory = this.ServiceProvider?.GetService<ILoggerFactory>();
            //if grain created outside a cluster
            if (loggerFactory == null)
                loggerFactory = NullLoggerFactory.Instance;
            logger = loggerFactory.CreateLogger($"NonReentrentStressGrainWithoutState-{_id}");

            executing = false;
            Log("--> OnActivateAsync");
            Log("<-- OnActivateAsync");
            return base.OnActivateAsync(cancellationToken);
        }

        private async Task SetOne(int iter, int level)
        {
            Log(String.Format("---> SetOne {0}-{1}_0", iter, level));
            CheckRuntimeEnvironment("SetOne");
            if (level > 0)
            {
                Log("SetOne {0}-{1}_1. Before await Task.Done.", iter, level);
                await Task.CompletedTask;
                Log("SetOne {0}-{1}_2. After await Task.Done.", iter, level);
                CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_3", iter, level));
                Log("SetOne {0}-{1}_4. Before await Task.Delay.", iter, level);
                await Task.Delay(TimeSpan.FromMilliseconds(10));
                Log("SetOne {0}-{1}_5. After await Task.Delay.", iter, level);
                CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_6", iter, level));
                var nextLevel = level - 1;
                await SetOne(iter, nextLevel);
                Log("SetOne {0}-{1}_7 => {2}. After await SetOne call.", iter, level, nextLevel);
                CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_8", iter, level));
                Log("SetOne {0}-{1}_9. Finished SetOne.", iter, level);
            }
            CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_10", iter, level));
            Log("<--- SetOne {0}-{1}_11", iter, level);
        }

        public async Task Test1()
        {
            Log(String.Format("Test1.Start"));

            CheckRuntimeEnvironment("Test1.BeforeLoop");
            var tasks = new List<Task>();
            for (var i = 0; i < Multiple; i++)
            {
                Log("Test1_ ------>" + i);
                CheckRuntimeEnvironment(String.Format("Test1_{0}_0", i));
                var task = SetOne(i, LEVEL);
                Log("After SetOne call " + i);
                CheckRuntimeEnvironment(String.Format("Test1_{0}_1", i));
                tasks.Add(task);
                CheckRuntimeEnvironment(String.Format("Test1_{0}_2", i));
                Log("Test1_ <------" + i);
            }
            CheckRuntimeEnvironment("Test1.AfterLoop");
            Log(String.Format("Test1_About to WhenAll"));
            await Task.WhenAll(tasks);
            Log(String.Format("Test1.Finish"));
            CheckRuntimeEnvironment("Test1.Finish-CheckRuntimeEnvironment");
//#if DEBUG
//            // HACK for testing
//            Logger.SetTraceLevelOverrides(overridesOff.ToList());
//#endif
        }

        public async Task Task_Delay(bool doStart)
        {
            var wrapper = new Task<Task>(async () =>
            {
                logger.Info("Before Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(1);
                logger.Info("After Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(2);
                logger.Info("After Task.Delay #2 TaskScheduler.Current=" + TaskScheduler.Current);
            });

            if (doStart)
            {
                wrapper.Start(); // THIS IS THE KEY STEP!
            }

            await wrapper.Unwrap();
        }

        private async Task DoDelay(int i)
        {
            logger.Info("Before Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
            await Task.Delay(1);
            logger.Info("After Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
        }

        private void CheckRuntimeEnvironment(string str)
        {
            var callStack = new StackTrace();
            //Log("CheckRuntimeEnvironment - {0} Executing={1}", str, executing);
            if (executing)
            {
                var errorMsg = string.Format(
                    "Found out that grain {0} is already in the middle of execution."
                    + "\n Single threaded-ness violation!"
                    + "\n {1} \n Call Stack={2}",
                    this._id,
                    TestRuntimeEnvironmentUtility.CaptureRuntimeEnvironment(),
                    callStack);
                this.logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                //Environment.Exit(1);
                throw new Exception(errorMsg);
            }
            //Assert.IsFalse(executing, "Found out that this grain is already in the middle of execution. Single threaded-ness violation!");
            executing = true;
            //Log("CheckRuntimeEnvironment - Start sleep " + str);
            Thread.Sleep(10);
            executing = false;
            //Log("CheckRuntimeEnvironment - End sleep " + str);
        }


        private void Log(string fmt, params object[] args)
        {
            var msg = fmt; // +base.CaptureRuntimeEnvironment();
            logger.Info(msg, args);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class InternalGrainStateData
    {
        [Id(0)]
        public int One { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    internal class InternalGrainWithState : Grain<InternalGrainStateData>, IInternalGrainWithState
    {
        private ILogger logger;

        public InternalGrainWithState(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public Task SetOne(int val)
        {
            logger.Info("SetOne");
            State.One = val;
            return Task.CompletedTask;
        }
    }

    public interface IBaseStateData // Note: I am deliberately not using IGrainState here.
    {
        int Field1 { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class StateInheritanceTestGrainData : IBaseStateData
    {
        [Id(0)]
        private int Field2 { get; set; }

        [Id(1)]
        public int Field1 { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StateInheritanceTestGrain : Grain<StateInheritanceTestGrainData>, IStateInheritanceTestGrain
    {
        private ILogger logger;

        public StateInheritanceTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<int> GetValue()
        {
            var val = State.Field1;
            logger.Info("GetValue {0}", val);
            return Task.FromResult(val);
        }

        public Task SetValue(int val)
        {
            State.Field1 = val;
            logger.Info("SetValue {0}", val);
            return WriteStateAsync();
        }
    }
}