using System;
using System.Threading.Tasks;
using Microsoft.PSharp;
using System.Collections.Generic;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Table.Protocol;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.ChainTableInterface;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Migration
{
    interface IMirrorChainTable2 : IChainTable2
    {
        /*
         * Like ExecuteBatch but sets the new ETags the same as in
         * originalResponse (assuming the mirror batch succeeds).  If
         * originalResponse is null, equivalent to ExecuteBatchAsync.
         *
         * originalBatch must be a copy of the original batch with the
         * _original_ ETag (i.e., If-Match) fields for correct processing.
         */
        Task<IList<TableResult>> ExecuteMirrorBatchAsync(
            TableBatchOperation originalBatch, IList<TableResult> originalResponse,
            TableRequestOptions requestOptions = null, OperationContext operationContext = null);
    }
    abstract class AbstractMirrorChainTable2 : AbstractChainTable2, IMirrorChainTable2
    {
        public abstract Task<IList<TableResult>> ExecuteMirrorBatchAsync(
            TableBatchOperation originalBatch, IList<TableResult> originalResponse,
            TableRequestOptions requestOptions = null, OperationContext operationContext = null);

        public override Task<IList<TableResult>> ExecuteBatchAsync(TableBatchOperation batch,
            TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return ExecuteMirrorBatchAsync(batch, null, requestOptions, operationContext);
        }
    }

    // This is expected to be a single call to one of the ITable methods.
    delegate Task<object> TableCall(IChainTable2 table);
    delegate Task<object> MirrorTableCall(IMirrorChainTable2 table);

    static class MigrationModel
    {
        // Start with a single partition per John's suggestion.
        internal static readonly string SINGLE_PARTITION_KEY = "";

        internal static readonly int NUM_SERVICE_MACHINES = 2;
        internal static readonly int NUM_CALLS_PER_MACHINE = 2;

        // If we go over this, we assume we're in an infinite loop.
        // Revise as necessary for test case size.
        internal static readonly int TABLE_CALL_LIMIT = 100;
    }

    interface ITablesMachinePeek
    {
        Task<SortedDictionary<PrimaryKey, DynamicTableEntity>> DumpReferenceTableAsync();

        // XXX: The following methods are here because they don't participate in
        // the alternation of table calls and annotations.  The "peek" in the
        // interface name has become a misnomer.

        Task<int> GetReferenceTableRevisionAsync();

        // Make sure it was OK that the query stream returned no keys in [fromKey, toKey).
        Task ValidateQueryStreamGapAsync(int startRevision, PrimaryKey fromKey, PrimaryKey toKey);

        // Validate the given row.
        Task ValidateQueryStreamRowAsync(int startRevision, DynamicTableEntity returnedRow);
    }
    interface ITablesMachineAnnotation
    {
        /*
        referenceCall != null means the last backend call was the linearization
        point.  referenceCall is performed and its return value is returned.

        referenceCall == null means the last backend call was not the
        linearization point, and this method just returns null.

        Either way, the tables machine is unlocked for subsequent table calls.
        */
        Task<object> AnnotateLastBackendCallAsync(
            MirrorTableCall referenceCall,
            IList<SpuriousETagChange> spuriousETagChanges);
    }

    static class TestUtils
    {
        internal static DynamicTableEntity CreateTestEntity(string rowKey, string color)
        {
            return new DynamicTableEntity
            {
                PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                RowKey = rowKey,
                ETag = ChainTable2Constants.ETAG_ANY,
                Properties = new Dictionary<string, EntityProperty>
                {
                    { "color", new EntityProperty(color) },
                },
            };
        }
        internal static MTableEntity CreateTestMTableEntity(string rowKey, string color, bool deleted = false)
        {
            var ent = new MTableEntity
            {
                PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                RowKey = rowKey,
                deleted = deleted,
            };
            if (color != null)
                ent.userProperties["color"] = new EntityProperty(color);
            return ent;
        }
    }

    /*
    Move to a separate class to work around P# restriction on nested classes in machines.
    We could defer creation of ServiceMachineCore until Initialize, but I want to just
    do the workaround and not make unrelated changes I might have to reverse later.

    Things we currently use from the machine:
    - Id: Easy enough to pass in constructor.
    - Payload: Easy enough to pass (and downcast!) in each method.
    If there get to be multiple delegates, consider defining an interface.  Hopefully the
    original restriction will be removed first anyway.
    */
    abstract class AppMachineCore
    {
        internal /*readonly*/ MachineId machineId;
        internal /*readonly*/ ITablesMachinePeek peekProxy;
        internal /*readonly*/ ITablesMachineAnnotation annotationProxy;

        internal virtual void Initialize(MachineId machineId, AppMachineInitializePayload payload)
        {
            this.machineId = machineId;
            peekProxy = payload.peekProxy;
            annotationProxy = payload.annotationProxy;
        }

        internal virtual Task HandleLinearizationPoint(IList<TableResult> successfulBatchResult)
        {
            throw new NotImplementedException();
        }

        internal class ChainTableMonitor : IChainTableMonitor
        {
            // No inner classes => more boilerplate. :(
            AppMachineCore machine;
            internal ChainTableMonitor(AppMachineCore machine)
            {
                this.machine = machine;
            }
            public async Task AnnotateLastBackendCallAsync(
                bool wasLinearizationPoint = false,
                IList<TableResult> successfulBatchResult = null,
                IList<SpuriousETagChange> spuriousETagChanges = null)
            {
                //Trace.TraceInformation("{0} calling AnnotateLastBackendCallAsync(wasLinearizationPoint: {1}, successfulBatchResult: {2}, spuriousETagChanges: {3})",
                //    PSharpRuntime.CurrentMachineId, wasLinearizationPoint, BetterComparer.ToString(successfulBatchResult), BetterComparer.ToString(spuriousETagChanges));
                if (wasLinearizationPoint)
                {
                    await machine.HandleLinearizationPoint(successfulBatchResult);
                }
                else
                {
                    await machine.annotationProxy.AnnotateLastBackendCallAsync(null, spuriousETagChanges);
                }
            }
        }

        internal abstract Task Run();
    }

    class AppMachineInitializeEvent : Event { }
    // This is a lot of boilerplate but appears to be the recommended approach
    // (https://msdn.microsoft.com/en-us/library/bb383979.aspx), modulo my
    // continuing resistance to adopting auto-implemented properties instead of fields.
    // Python's namedtuple (+ strong typing) would be the way to do this.
    class AppMachineInitializePayload
    {
        internal readonly IConfigurationService<MTableConfiguration> configService;
        internal readonly IChainTable2 oldTable;
        internal readonly IChainTable2 newTable;
        internal readonly ITablesMachinePeek peekProxy;
        internal readonly ITablesMachineAnnotation annotationProxy;
        internal AppMachineInitializePayload(
            IConfigurationService<MTableConfiguration> configService,
            IChainTable2 oldTable, IChainTable2 newTable,
            ITablesMachinePeek peekProxy, ITablesMachineAnnotation annotationProxy)
        {
            this.configService = configService;
            this.oldTable = oldTable;
            this.newTable = newTable;
            this.peekProxy = peekProxy;
            this.annotationProxy = annotationProxy;
        }
    }

    class ServiceMachineCore : AppMachineCore
    {
        /*readonly*/ MigratingTable migratingTable;
        MirrorTableCall currentReferenceCall;
        IList<TableResult> successfulBatchResult;
        Outcome<object, StorageException>? currentReferenceOutcome;

        internal override void Initialize(MachineId machineId, AppMachineInitializePayload payload)
        {
            base.Initialize(machineId, payload);
            migratingTable = new MigratingTable(payload.configService, payload.oldTable, payload.newTable,
                new ChainTableMonitor(this));
        }

        async Task RunCallAsync(TableCall originalCall, MirrorTableCall referenceCall)
        {
            // TODO: All assertions should show what the call was.
            // XXX: We currently have no way to detect incorrect interleaving of
            // backend calls and AnnotateLastOutgoingCall here.  Most incorrect
            // interleavings will cause an error on the TablesMachine, but some
            // may go undetected.
            // For now, we're not doing streaming queries at all, so we don't have
            // to worry about how to handle them.

            currentReferenceCall = referenceCall;
            object actualOutcome = await Catching<StorageException>.Task(originalCall(migratingTable));

            // Verify that successfulBatchResult was correct if specified.
            // (Ideally, we'd also catch if it isn't specified when it should
            // be, but that's less of a risk as it will likely cause ETag
            // mismatches anyway.)
            if (successfulBatchResult != null)
            {
                var successfulBatchOutcome = new Outcome<object, StorageException>(successfulBatchResult);
                PSharpRuntime.Assert(BetterComparer.Instance.Equals(successfulBatchOutcome, actualOutcome),
                    "{0} incorrect successfulBatchResult:\n{1}\nExpected:\n{2}\n", machineId,
                    BetterComparer.ToString(successfulBatchOutcome), BetterComparer.ToString(actualOutcome));
            }

            PSharpRuntime.Assert(currentReferenceOutcome != null, "The call completed without reporting a linearization point.");
            PSharpRuntime.Assert(BetterComparer.Instance.Equals(actualOutcome, currentReferenceOutcome),
                "{0} call outcome:\n{1}\nExpected:\n{2}\n", machineId,
                BetterComparer.ToString(actualOutcome), BetterComparer.ToString(currentReferenceOutcome));

            // Reset fields
            currentReferenceCall = null;
            successfulBatchResult = null;
            currentReferenceOutcome = null;
        }

        internal override async Task HandleLinearizationPoint(IList<TableResult> successfulBatchResult)
        {
            PSharpRuntime.Assert(currentReferenceOutcome == null, "The call already reported a linearization point.");
            this.successfulBatchResult = successfulBatchResult;
            currentReferenceOutcome = await Catching<StorageException>.Task(
                annotationProxy.AnnotateLastBackendCallAsync(currentReferenceCall, null));
        }

        async Task DoRandomAtomicCalls()
        {
            for (int callNum = 0; callNum < MigrationModel.NUM_CALLS_PER_MACHINE; callNum++)
            {
                TableCall originalCall;
                MirrorTableCall referenceCall;
                SortedDictionary<PrimaryKey, DynamicTableEntity> dump = await peekProxy.DumpReferenceTableAsync();

                if (PSharpRuntime.Nondeterministic())
                {
                    // Query
                    // XXX: Test the filtering?
                    var query = new TableQuery<DynamicTableEntity>();
                    query.FilterString = TableQuery.GenerateFilterCondition(
                        TableConstants.PartitionKey, QueryComparisons.Equal, MigrationModel.SINGLE_PARTITION_KEY);
                    // async/await pair needed to upcast the return value to object.
                    originalCall = async table => await table.ExecuteQueryAtomicAsync(query);
                    referenceCall = async referenceTable => await referenceTable.ExecuteQueryAtomicAsync(query);
                    Console.WriteLine("{0} starting query", machineId);
                }
                else
                {
                    // Batch write
                    int batchSize = PSharpRuntime.Nondeterministic() ? 2 : 1;
                    var batch = new TableBatchOperation();
                    var rowKeyChoices = new List<string> { "0", "1", "2", "3", "4", "5" };

                    for (int opNum = 0; opNum < batchSize; opNum++)
                    {
                        int opTypeNum = PSharpNondeterminism.Choice(7);
                        int rowKeyI = PSharpNondeterminism.Choice(rowKeyChoices.Count);
                        string rowKey = rowKeyChoices[rowKeyI];
                        rowKeyChoices.RemoveAt(rowKeyI);  // Avoid duplicate in same batch
                        var primaryKey = new PrimaryKey(MigrationModel.SINGLE_PARTITION_KEY, rowKey);
                        string eTag = null;
                        if (opTypeNum >= 1 && opTypeNum <= 3)
                        {
                            DynamicTableEntity existingEntity;
                            int etagTypeNum = PSharpNondeterminism.Choice(
                                dump.TryGetValue(primaryKey, out existingEntity) ? 3 : 2);
                            switch (etagTypeNum)
                            {
                                case 0: eTag = ChainTable2Constants.ETAG_ANY; break;
                                case 1: eTag = "wrong"; break;
                                case 2: eTag = existingEntity.ETag; break;
                            }
                        }
                        DynamicTableEntity entity = new DynamicTableEntity
                        {
                            PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                            RowKey = rowKey,
                            ETag = eTag,
                            Properties = new Dictionary<string, EntityProperty> {
                                // Give us something to see on merge.  Might help with tracing too!
                                { string.Format("{0}_c{1}_o{2}", machineId.ToString(), callNum, opNum),
                                    new EntityProperty(true) }
                            }
                        };
                        switch (opTypeNum)
                        {
                            case 0: batch.Insert(entity); break;
                            case 1: batch.Replace(entity); break;
                            case 2: batch.Merge(entity); break;
                            case 3: batch.Delete(entity); break;
                            case 4: batch.InsertOrReplace(entity); break;
                            case 5: batch.InsertOrMerge(entity); break;
                            case 6:
                                entity.ETag = ChainTable2Constants.ETAG_DELETE_IF_EXISTS;
                                batch.Delete(entity); break;
                        }
                    }

                    TableBatchOperation batchCopy = ChainTableUtils.CopyBatch<DynamicTableEntity>(batch);
                    originalCall = async table => await table.ExecuteBatchAsync(batch);
                    referenceCall = async referenceTable => await referenceTable.ExecuteMirrorBatchAsync(batchCopy, successfulBatchResult);
                    Console.WriteLine("{0} starting batch {1}", machineId, batch);
                }

                await RunCallAsync(originalCall, referenceCall);
                Console.WriteLine("{0} table call verified");
            }
        }

        async Task DoQueryStreamed()
        {
            int startRevision = await peekProxy.GetReferenceTableRevisionAsync();
            // XXX: Test the filtering?
            var query = new TableQuery<DynamicTableEntity>();
            using (IQueryStream<DynamicTableEntity> stream = await migratingTable.ExecuteQueryStreamedAsync(query))
            {
                PrimaryKey continuationKey = await stream.GetContinuationPrimaryKeyAsync();
                await peekProxy.ValidateQueryStreamGapAsync(startRevision, null, continuationKey);
                do
                {
                    DynamicTableEntity row = await stream.ReadRowAsync();
                    PrimaryKey newContinuationKey = await stream.GetContinuationPrimaryKeyAsync();
                    if (row == null)
                    {
                        PSharpRuntime.Assert(newContinuationKey == null);
                        await peekProxy.ValidateQueryStreamGapAsync(startRevision, continuationKey, null);
                    }
                    else
                    {
                        await peekProxy.ValidateQueryStreamGapAsync(startRevision, continuationKey, row.GetPrimaryKey());
                        await peekProxy.ValidateQueryStreamRowAsync(startRevision, row);
                        await peekProxy.ValidateQueryStreamGapAsync(startRevision,
                            ChainTableUtils.NextValidPrimaryKeyAfter(row.GetPrimaryKey()), newContinuationKey);
                    }
                    continuationKey = newContinuationKey;
                } while (continuationKey != null);
            }
        }

        internal override Task Run()
        {
            if (PSharpRuntime.Nondeterministic())
            {
                return DoQueryStreamed();
            }
            else
            {
                return DoRandomAtomicCalls();
            }
        }
    }

    class ServiceMachine : Machine
    {
        readonly ServiceMachineCore core;
        readonly MachineSynchronizationContext synchronizationContext;
        public ServiceMachine() {
            core = new ServiceMachineCore();
            synchronizationContext = new MachineSynchronizationContext(Id);
        }

        void DispatchPayload()
        {
            using (synchronizationContext.AsCurrent())
            {
                ((IDispatchable)Payload).Dispatch();
            }
        }

        [Start]
        [OnEventGotoState(typeof(AppMachineInitializeEvent), typeof(MainState), nameof(Initialize))]
        class WaitingForInitialization : MachineState { }

        [OnEntry(nameof(Start))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class MainState : MachineState { }

        void Initialize()
        {
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), 1);
            core.Initialize(Id, (AppMachineInitializePayload)Payload);
        }
        void Start()
        {
            using (synchronizationContext.AsCurrent())
            {
                Run();
            }
        }
        async void Run()  // intentional fire-and-forget
        {
            await core.Run();
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), -1);
        }
    }

    class MigratorMachineCore : AppMachineCore
    {
        /*readonly*/ MasterMigratingTable migratingTable;
        internal override void Initialize(MachineId machineId, AppMachineInitializePayload payload)
        {
            base.Initialize(machineId, payload);
            migratingTable = new MasterMigratingTable(payload.configService, payload.oldTable, payload.newTable,
                new ChainTableMonitor(this));
        }

        internal override async Task Run()
        {
            await migratingTable.MigrateAsync();
            // TODO: Verify that new table and reference table are equal after
            // migration, or just rely on randomly generated queries to test
            // this?
        }
    }

    class MigratorMachine : Machine
    {
        readonly MigratorMachineCore core;
        readonly MachineSynchronizationContext synchronizationContext;
        public MigratorMachine()
        {
            core = new MigratorMachineCore();
            synchronizationContext = new MachineSynchronizationContext(Id);
        }

        void DispatchPayload()
        {
            using (synchronizationContext.AsCurrent())
            {
                ((IDispatchable)Payload).Dispatch();
            }
        }

        [Start]
        [OnEventGotoState(typeof(AppMachineInitializeEvent), typeof(MainState), nameof(Initialize))]
        class WaitingForInitialization : MachineState { }

        [OnEntry(nameof(Start))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class MainState : MachineState { }

        void Initialize()
        {
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), 1);
            core.Initialize(Id, (AppMachineInitializePayload)Payload);
        }
        void Start()
        {
            using (synchronizationContext.AsCurrent())
            {
                Run();
            }
        }
        async void Run()  // intentional fire-and-forget
        {
            await core.Run();
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), -1);
        }
    }

    class ConfigurationServicePSharpProxy<TConfig> : IConfigurationService<TConfig>
    {
        readonly InMemoryConfigurationService<TConfig> mirror;
        readonly IConfigurationService<TConfig> originalProxy;

        class Subscriber : IConfigurationSubscriber<TConfig>
        {
            readonly ConfigurationServicePSharpProxy<TConfig> outer;
            internal Subscriber(ConfigurationServicePSharpProxy<TConfig> outer)
            {
                this.outer = outer;
            }
            public Task ApplyConfigurationAsync(TConfig newConfig)
            {
                return outer.mirror.PushConfigurationAsync(newConfig);
            }
        }

        internal ConfigurationServicePSharpProxy(MachineId callerMachineId, MachineId hostMachineId,
            IConfigurationService<TConfig> original, string originalDebugName)
        {
            TConfig initialConfig;
            originalProxy = PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, original,
                originalDebugName, () => new GenericDispatchableEvent());

            IConfigurationSubscriber<TConfig> subscriberReverseProxy =
                PSharpRealProxy.MakeTransparentProxy(hostMachineId, callerMachineId,
                    (IConfigurationSubscriber<TConfig>)new Subscriber(this),
                    string.Format("<{0} subscriber>", originalDebugName),
                    () => new GenericDispatchableEvent());
            // XXX Implement IDisposable.  Would need to proxy the dispose as well.
            original.Subscribe(subscriberReverseProxy, out initialConfig);

            mirror = new InMemoryConfigurationService<TConfig>(initialConfig);
        }
        public Task PushConfigurationAsync(TConfig newConfig)
        {
            return originalProxy.PushConfigurationAsync(newConfig);
        }

        public IDisposable Subscribe(IConfigurationSubscriber<TConfig> subscriber, out TConfig currentConfig)
        {
            return mirror.Subscribe(subscriber, out currentConfig);
        }
    }

    class ChainTable2PSharpProxy : AbstractChainTable2
    {
        readonly MachineId callerMachineId, hostMachineId;
        readonly string debugName;
        readonly IChainTable2 plainEventProxy;
        readonly IChainTable2 tableCallEventProxy;

        internal ChainTable2PSharpProxy(MachineId callerMachineId, MachineId hostMachineId,
            IChainTable2 original, string debugName)
        {
            this.callerMachineId = callerMachineId;
            this.hostMachineId = hostMachineId;
            this.debugName = debugName;
            plainEventProxy = PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, original,
                debugName, () => new GenericDispatchableEvent());
            tableCallEventProxy = PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, original,
                debugName, () => new TableCallEvent());
        }

        public override Task<TableResult> ExecuteAsync(TableOperation operation, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            //Trace.TraceInformation("{0} calling {1}.ExecuteAsync({2})", callerMachineId, debugName, BetterComparer.ToString(operation));
            return tableCallEventProxy.ExecuteAsync(operation, requestOptions, operationContext);
        }

        public override Task<IList<TableResult>> ExecuteBatchAsync(TableBatchOperation batch, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            //Trace.TraceInformation("{0} calling {1}.ExecuteBatchAsync({2})", callerMachineId, debugName, BetterComparer.ToString(batch));
            return tableCallEventProxy.ExecuteBatchAsync(batch, requestOptions, operationContext);
        }

        public override Task<IList<TElement>> ExecuteQueryAtomicAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            //Trace.TraceInformation("{0} calling {1}.ExecuteQueryAtomicAsync({2})", callerMachineId, debugName, BetterComparer.ToString(query));
            return tableCallEventProxy.ExecuteQueryAtomicAsync(query, requestOptions, operationContext);
        }

        class QueryStreamPSharpProxy<TElement> : IQueryStream<TElement>
            where TElement : ITableEntity, new()
        {
            readonly IQueryStream<TElement> plainProxy;
            internal QueryStreamPSharpProxy(IQueryStream<TElement> plainProxy)
            {
                this.plainProxy = plainProxy;
            }

            // Hack.  This would take extra work to get working through
            // PSharpProxy (even if we would make it fire-and-forget since we
            // can't block locally, we need a way to call the void method on the
            // remote side) and we know the query streams that we proxy don't
            // have anything important in Dispose.
            public void Dispose() { }

            public Task<PrimaryKey> GetContinuationPrimaryKeyAsync()
            {
                return plainProxy.GetContinuationPrimaryKeyAsync();
            }

            public Task<TElement> ReadRowAsync()
            {
                return plainProxy.ReadRowAsync();
            }
        }

        public override async Task<IQueryStream<TElement>> ExecuteQueryStreamedAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            //Trace.TraceInformation("{0} calling {1}.ExecuteQueryStreamedAsync({2})", callerMachineId, debugName, BetterComparer.ToString(query));
            // Philosophically, maybe this proxy-making belongs on the host
            // side, but that would require a second custom wrapper because we
            // still need the custom wrapper on the caller side to do the
            // different event types.
            IQueryStream<TElement> remoteStream = await plainEventProxy.ExecuteQueryStreamedAsync(
                query, requestOptions, operationContext);
            return new QueryStreamPSharpProxy<TElement>(
                PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, remoteStream,
                string.Format("<{0} QueryStream>", debugName), () => new GenericDispatchableEvent()));
        }
    }

    class TablePeekEvent : Event { }
    class TableCallEvent : Event { }
    class TableCallAnnotationEvent : Event { }
    class TablesMachineInitializedEvent : Event { }

    class InMemoryTableWithHistory : AbstractMirrorChainTable2
    {
        InMemoryTable table = new InMemoryTable();
        internal List<SortedDictionary<PrimaryKey, DynamicTableEntity>> dumps;

        internal InMemoryTableWithHistory()
        {
            dumps = new List<SortedDictionary<PrimaryKey, DynamicTableEntity>> { table.Dump() };
        }

        public override Task<IList<TableResult>> ExecuteMirrorBatchAsync(TableBatchOperation originalBatch, IList<TableResult> originalResponse, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            Task<IList<TableResult>> ret = table.ExecuteMirrorBatchAsync(originalBatch, originalResponse, requestOptions, operationContext);
            // Only on a successful write.  Exceptions will bypass this.
            dumps.Add(table.Dump());
            return ret;
        }

        public override Task<IList<TElement>> ExecuteQueryAtomicAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return table.ExecuteQueryAtomicAsync(query, requestOptions, operationContext);
        }

        public override Task<IQueryStream<TElement>> ExecuteQueryStreamedAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return table.ExecuteQueryStreamedAsync(query, requestOptions, operationContext);
        }
    }

    class TablesMachine : Machine, ITablesMachinePeek, ITablesMachineAnnotation
    {
        readonly MachineSynchronizationContext synchronizationContext;
        public TablesMachine()
        {
            synchronizationContext = new MachineSynchronizationContext(Id);
        }

        InMemoryConfigurationService<MTableConfiguration> configService;
        InMemoryTable oldTable, newTable;
        InMemoryTableWithHistory referenceTable;
        int numTableCalls = 0;

        [Start]
        [OnEntry(nameof(StartInitialization))]
        [OnEventGotoState(typeof(TablesMachineInitializedEvent), typeof(Ready))]
        [DeferEvents(typeof(TablePeekEvent), typeof(TableCallEvent))]
        // XXX: Use Push or something to avoid having to redeclare SynchronizationContextWorkEvent in every state?
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class Initializing : MachineState { }

        [OnEventDoAction(typeof(TablePeekEvent), nameof(DispatchPayload))]
        [OnEventGotoState(typeof(TableCallEvent), typeof(WaitingForAnnotation), nameof(DispatchTableCall))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class Ready : MachineState { }

        [DeferEvents(typeof(TablePeekEvent), typeof(TableCallEvent))]
        [OnEventGotoState(typeof(TableCallAnnotationEvent), typeof(Ready), nameof(DispatchPayload))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class WaitingForAnnotation : MachineState { }

        void StartInitialization()
        {
            using (synchronizationContext.AsCurrent())
            {
                Initialize();
            }
        }

        Task<IList<TableResult>> ExecuteExportedMirrorBatchAsync(TableBatchOperation batch, IList<TableResult> originalResponse)
        {
            var exportedBatch = new TableBatchOperation();
            var exportedOriginalResponse = new List<TableResult>();
            for (int i = 0; i < batch.Count; i++)
            {
                TableOperation op = batch[i];
                var mtableEntity = (MTableEntity)op.GetEntity();
                if (MigratingTable.RowKeyIsInternal(mtableEntity.RowKey))
                    continue;
                exportedOriginalResponse.Add(originalResponse[i]);
                Debug.Assert(op.GetOperationType() == TableOperationType.InsertOrReplace);
                DynamicTableEntity exported = mtableEntity.Export<DynamicTableEntity>();
                if (mtableEntity.deleted)
                {
                    exported.ETag = ChainTable2Constants.ETAG_DELETE_IF_EXISTS;
                    exportedBatch.Delete(exported);
                }
                else
                {
                    exported.ETag = null;
                    exportedBatch.InsertOrReplace(exported);
                }
            }
            return referenceTable.ExecuteMirrorBatchAsync(exportedBatch, exportedOriginalResponse);
        }

        void InitializeAppMachine(MachineId appMachineId)
        {
            Send(appMachineId, new AppMachineInitializeEvent(), new AppMachineInitializePayload(
                new ConfigurationServicePSharpProxy<MTableConfiguration>(appMachineId, Id, configService, "configService"),
                new ChainTable2PSharpProxy(appMachineId, Id, oldTable, "oldTable"),
                new ChainTable2PSharpProxy(appMachineId, Id, newTable, "newTable"),
                this.MakeTransparentProxy((ITablesMachinePeek)this, "tablesMachinePeek",
                    appMachineId, () => new TablePeekEvent()),
                this.MakeTransparentProxy((ITablesMachineAnnotation)this, "tablesMachineAnnotation",
                    appMachineId, () => new TableCallAnnotationEvent())));
        }

        async void Initialize()
        {
            // TODO: Figure out how to verify the transition from
            // "old table only" to "in migration" to "new table only".
            // (I don't think this is the biggest risk, but I'm still
            // interested in verifying it.)
            // I assume once we do that, the possibility of insertions while
            // we're in "old table only" will model the ability to start
            // from a nonempty table.

            configService = new InMemoryConfigurationService<MTableConfiguration>(
                MasterMigratingTable.INITIAL_CONFIGURATION);
            oldTable = new InMemoryTable();
            newTable = new InMemoryTable();
            referenceTable = new InMemoryTableWithHistory();

#if false
            // Second partition from the example in:
            // https://microsoft.sharepoint.com/teams/toolsforeng/Shared%20Documents/ContentRepository/LiveMigration/Migration_slides.pptx
            MTableEntity eMeta = new MTableEntity {
                PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                RowKey = MigratingTable.ROW_KEY_PARTITION_META,
                partitionState = MTablePartitionState.SWITCHED,
            };
            MTableEntity e0 = TestUtils.CreateTestMTableEntity("0", "orange");
            MTableEntity e1old = TestUtils.CreateTestMTableEntity("1", "red");
            MTableEntity e2new = TestUtils.CreateTestMTableEntity("2", "green");
            MTableEntity e3old = TestUtils.CreateTestMTableEntity("3", "blue");
            MTableEntity e3new = TestUtils.CreateTestMTableEntity("3", "azure");
            MTableEntity e4old = TestUtils.CreateTestMTableEntity("4", "yellow");
            MTableEntity e4new = TestUtils.CreateTestMTableEntity("4", null, true);
            var oldBatch = new TableBatchOperation();
            oldBatch.InsertOrReplace(eMeta);
            oldBatch.InsertOrReplace(e0);
            oldBatch.InsertOrReplace(e1old);
            oldBatch.InsertOrReplace(e3old);
            oldBatch.InsertOrReplace(e4old);
            IList<TableResult> oldTableResult = await oldTable.ExecuteBatchAsync(oldBatch);
            await ExecuteExportedMirrorBatchAsync(oldBatch, oldTableResult);
            var newBatch = new TableBatchOperation();
            newBatch.InsertOrReplace(e0);
            newBatch.InsertOrReplace(e2new);
            newBatch.InsertOrReplace(e3new);
            newBatch.InsertOrReplace(e4new);
            IList<TableResult> newTableResult = await newTable.ExecuteBatchAsync(newBatch);
            // Allow rows to overwrite rather than composing the virtual ETags manually.
            // InsertOrReplace doesn't use the ETag, so we don't care that the ETag was mutated by the original batch.
            await ExecuteExportedMirrorBatchAsync(newBatch, newTableResult);
#endif

            // Start with the old table now.
            var batch = new TableBatchOperation();
            batch.InsertOrReplace(TestUtils.CreateTestEntity("0", "orange"));
            batch.InsertOrReplace(TestUtils.CreateTestEntity("1", "red"));
            batch.InsertOrReplace(TestUtils.CreateTestEntity("3", "blue"));
            batch.InsertOrReplace(TestUtils.CreateTestEntity("4", "yellow"));
            IList<TableResult> oldTableResult = await oldTable.ExecuteBatchAsync(batch);
            // InsertOrReplace doesn't use the ETag, so we don't care that the ETag was mutated by the original batch.
            await referenceTable.ExecuteMirrorBatchAsync(batch, oldTableResult);

            //CreateMonitor(typeof(RunningServiceMachinesMonitor));

            for (int i = 0; i < MigrationModel.NUM_SERVICE_MACHINES; i++)
            {
                InitializeAppMachine(CreateMachine(typeof(ServiceMachine)));
            }

            InitializeAppMachine(CreateMachine(typeof(MigratorMachine)));

            Send(Id, new TablesMachineInitializedEvent());
        }

        void DispatchTableCall()
        {
            numTableCalls++;
            // Crude liveness check, since a monitor did not work (see below).
            PSharpRuntime.Assert(numTableCalls <= MigrationModel.TABLE_CALL_LIMIT,
                "A service machine may be in an infinite loop.");
            DispatchPayload();
        }

        void DispatchPayload()
        {
            using (synchronizationContext.AsCurrent())
            {
                ((IDispatchable)Payload).Dispatch();
            }
        }

        // These two should do the same thing.  Which do we prefer?
        /*
        Task<object> ITablesMachineAnnotation.AnnotateLastOutgoingCallAsync(MirrorTableCall referenceCall)
        {
            if (referenceCall == null)
                return Task.FromResult((object)null);
            return referenceCall(referenceTable);
        }
        */
        async Task<object> ITablesMachineAnnotation.AnnotateLastBackendCallAsync(
            MirrorTableCall referenceCall, IList<SpuriousETagChange> spuriousETagChanges)
        {
            if (spuriousETagChanges == null)
                spuriousETagChanges = new List<SpuriousETagChange>();

            if (referenceCall == null)
            {
                if (spuriousETagChanges.Count > 0)
                {
                    var batch = new TableBatchOperation();
                    var originalResponse = new List<TableResult>();
                    foreach (SpuriousETagChange change in spuriousETagChanges)
                    {
                        batch.Merge(new DynamicTableEntity
                        {
                            PartitionKey = change.partitionKey,
                            RowKey = change.rowKey,
                            ETag = ChainTable2Constants.ETAG_ANY
                        });
                        originalResponse.Add(new TableResult { Etag = change.newETag });
                    }
                    try
                    {
                        await referenceTable.ExecuteMirrorBatchAsync(batch, originalResponse, null, null);
                    }
                    catch (StorageException ex)
                    {
                        // Make sure this doesn't get swallowed by a generic StorageException catch block.
                        throw new InvalidOperationException("Invalid spurious ETag change annotation.", ex);
                    }
                }
                return null;
            }
            else
            {
                if (spuriousETagChanges.Count > 0)
                    throw new ArgumentException("spuriousETagChanges currently not allowed with a reference call");
                return await referenceCall(referenceTable);
            }
        }

        Task<SortedDictionary<PrimaryKey, DynamicTableEntity>> ITablesMachinePeek.DumpReferenceTableAsync()
        {
            return Task.FromResult(referenceTable.dumps.Last());
        }

        Task<int> ITablesMachinePeek.GetReferenceTableRevisionAsync() {
            return Task.FromResult(referenceTable.dumps.Count - 1);
        }

        Task ITablesMachinePeek.ValidateQueryStreamGapAsync(int startRevision, PrimaryKey fromKey, PrimaryKey toKey)
        {
            Func<PrimaryKey, bool> inRangePred =
                k => (fromKey == null || k.CompareTo(fromKey) >= 0) && (toKey == null || k.CompareTo(toKey) < 0);
            IEnumerable<PrimaryKey> missedKeys =
                (from dump in referenceTable.dumps.Skip(startRevision) select dump.Keys.Where(inRangePred)).IntersectAll();
            Assert(missedKeys.Count() == 0);
            return Task.CompletedTask;  // This is like Task.FromResult(void).
        }

        Task ITablesMachinePeek.ValidateQueryStreamRowAsync(int startRevision, DynamicTableEntity returnedRow)
        {
            Assert(referenceTable.dumps.Skip(startRevision).Any(
                dump => BetterComparer.Instance.Equals(returnedRow, dump.GetValueOrDefault(returnedRow.GetPrimaryKey()))));
            return Task.CompletedTask;  // This is like Task.FromResult(void).
        }
    }

#if false
    // Enabling P# liveness checking immediately caused a false positive because
    // the state caching does not consider the data in the table or the progress
    // of the ServiceMachines through their async methods.  Rather than try to
    // fix that, just switch to the table call limit for now.

    // The number of running service machines has changed.  Payload: change as an int.
    class ServiceMachineCountChangeEvent : Event { }
    class ServiceMachinesSomeEvent : Event { }
    class ServiceMachinesNoneEvent : Event { }

    class RunningServiceMachinesMonitor : Monitor
    {
        int numRunning = 0;

        [Start]
        [OnEventDoAction(typeof(ServiceMachineCountChangeEvent), nameof(ProcessCountChange))]
        [OnEventGotoState(typeof(ServiceMachinesNoneEvent), typeof(None))]
        [OnEventGotoState(typeof(ServiceMachinesSomeEvent), typeof(Some))]
        class None : MonitorState { }

        [Hot]
        [OnEventDoAction(typeof(ServiceMachineCountChangeEvent), nameof(ProcessCountChange))]
        [OnEventGotoState(typeof(ServiceMachinesNoneEvent), typeof(None))]
        [OnEventGotoState(typeof(ServiceMachinesSomeEvent), typeof(Some))]
        class Some : MonitorState { }

        void ProcessCountChange()
        {
            numRunning += (int)Payload;
            if (numRunning == 0)
                Raise(new ServiceMachinesNoneEvent());
            else
                Raise(new ServiceMachinesSomeEvent());
        }
    }
#endif

    static class Program
    {
        [Test]
        public static void PSharpEntryPoint()
        {
            PSharpRuntime.CreateMachine(typeof(TablesMachine));
        }
        public static void Main(string[] args)
        {
            PSharpEntryPoint();
            Console.ReadLine();
        }
    }
}
