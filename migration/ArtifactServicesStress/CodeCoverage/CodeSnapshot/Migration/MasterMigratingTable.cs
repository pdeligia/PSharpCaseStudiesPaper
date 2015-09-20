using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.ChainTableInterface;
using System.Net;
using Microsoft.WindowsAzure.Storage.Table.Protocol;

namespace Migration
{
    public class MasterMigratingTable : MigratingTable
    {
        public static MTableConfiguration INITIAL_CONFIGURATION =
            new MTableConfiguration(TableClientState.USE_OLD_HIDE_METADATA);

        new IConfigurationService<MTableConfiguration> configService;

        public MasterMigratingTable(IConfigurationService<MTableConfiguration> configService,
            IChainTable2 oldTable, IChainTable2 newTable,
            MTableOptionalBug? bugToEnable)
            : base(configService, oldTable, newTable, bugToEnable)
        {
            this.configService = configService;
        }

        public MasterMigratingTable(IConfigurationService<MTableConfiguration> configService,
            IChainTable2 oldTable, IChainTable2 newTable, IChainTableMonitor monitor, MTableOptionalBug? bugToEnable)
            : base(configService, oldTable, newTable, monitor, bugToEnable)
        {
            this.configService = configService;
        }

        Task CopyAsync(TableRequestOptions requestOptions, OperationContext operationContext)
        {
            string previousPartitionKey = null;
            return WalkTableInParallel(oldTable, requestOptions, operationContext, async (oldEntity) =>
            {
                if (RowKeyIsInternal(oldEntity.RowKey))
                    return null;

                if (oldEntity.PartitionKey != previousPartitionKey)
                {
                    previousPartitionKey = oldEntity.PartitionKey;
                    await EnsurePartitionSwitchedAsync(oldEntity.PartitionKey, requestOptions, operationContext);
                    if (IsBugEnabled(MTableOptionalBug.InsertBehindMigrator))
                    {
                        Console.WriteLine("Copied Partition: {0}", previousPartitionKey);
                        // More reasonable formulation of this bug.  If we're going
                        // to allow CopyAsync while some clients are still writing
                        // to the old table, give the developer credit for realizing
                        // that the stream might be stale by the time the partition
                        // is switched.
                        return await oldTable.ExecuteQueryStreamedAsync(
                            new TableQuery<MTableEntity>
                            {
                                FilterString = TableQuery.GenerateFilterCondition(
                                    TableConstants.PartitionKey,
                                    QueryComparisons.GreaterThanOrEqual,
                                    oldEntity.PartitionKey)
                            }, requestOptions, operationContext);
                    }

                    if (previousPartitionKey != null)
                    {
                        Console.WriteLine("Copied Partition: {0}", previousPartitionKey);
                    }
                }
                previousPartitionKey = oldEntity.PartitionKey;

                // Can oldEntity be stale by the time we EnsurePartitionSwitchedAsync?
                // Currently no, because we don't start the copy until all clients have
                // entered PREFER_NEW state, meaning that all writes go to the new
                // table.  When we implement the Kstart/Kdone optimization (or similar),
                // then clients will be able to write to the old table in parallel with
                // our scan and we'll need to rescan after EnsurePartitionSwitchedAsync.


                // Should we also stream from the new table and copy only the
                // entities not already copied?
                await TryCopyEntityToNewTableAsync(oldEntity, requestOptions, operationContext);

                return null;
            });
        }

        Task WalkTableInParallel(IChainTable2 table, TableRequestOptions requestOptions, OperationContext operationContext,
            Func<MTableEntity, Task<IQueryStream<MTableEntity>>> rowCallbackAsync)
        {
            return Task.WhenAll(Enumerable.Range(0, 16).Select(hexNumber => Task.Run(async () =>
            {
                var query = new TableQuery<MTableEntity>();

                if (hexNumber == 0)
                {
                    query.FilterString = TableQuery.GenerateFilterCondition(
                        "PartitionKey",
                        QueryComparisons.LessThanOrEqual,
                        (hexNumber + 1).ToString("x"));
                }
                else if (hexNumber == 15)
                {
                    query.FilterString = TableQuery.GenerateFilterCondition(
                        "PartitionKey",
                        QueryComparisons.GreaterThanOrEqual,
                        hexNumber.ToString("x"));
                }
                else
                {
                    query.FilterString = TableQuery.CombineFilters(
                        TableQuery.GenerateFilterCondition(
                            "PartitionKey",
                            QueryComparisons.GreaterThanOrEqual,
                            hexNumber.ToString("x")),
                        TableOperators.And,
                        TableQuery.GenerateFilterCondition(
                            "PartitionKey",
                            QueryComparisons.LessThanOrEqual,
                            (hexNumber + 1).ToString("x")));
                }

                try
                {
                    IQueryStream<MTableEntity> tableStream = await table.ExecuteQueryStreamedAsync(
                        query, requestOptions, operationContext);

                    MTableEntity entity;
                    while ((entity = await tableStream.ReadRowAsync()) != null)
                    {
                        IQueryStream<MTableEntity> newTableStream = await rowCallbackAsync(entity);
                        if (newTableStream != null)
                        {
                            tableStream = newTableStream;
                        }
                    }
                }
                catch (Exception e)
                {
                    while (e is AggregateException)
                    {
                        e = ((AggregateException) (e)).InnerException;
                    }

                    if (!(e is StorageException && ((StorageException) e).RequestInformation.HttpStatusCode == 404))
                    {
                        throw;
                    }
                }

            })));
        }

        async Task CleanupAsync(TableRequestOptions requestOptions, OperationContext operationContext)
        {
            string previousPartitionKey = null;
            await WalkTableInParallel(newTable, requestOptions, operationContext, async (newEntity) =>
            {
                if (newEntity.PartitionKey != previousPartitionKey)
                {
                    if (previousPartitionKey != null)
                    {
                        Console.WriteLine("Cleaned Partition: {0}", previousPartitionKey);
                    }
                    previousPartitionKey = newEntity.PartitionKey;
                }

                // XXX: Consider factoring out this "query and retry" pattern
                // into a separate method.
                Attempt:
                TableOperation cleanupOp = newEntity.deleted ? TableOperation.Delete(newEntity)
                    : TableOperation.Replace(newEntity.Export<DynamicTableEntity>());
                TableResult cleanupResult;
                StorageException ex;
                try
                {
                    cleanupResult = await newTable.ExecuteAsync(cleanupOp, requestOptions, operationContext);
                    ex = null;
                }
                catch (StorageException exToHandle)
                {
                    cleanupResult = null;
                    ex = exToHandle;
                }
                if (ex != null)
                {
                    if (ex.GetHttpStatusCode() == HttpStatusCode.NotFound)
                    {
                        // Someone else deleted it concurrently.  Nothing to do.
                        //await monitor.AnnotateLastBackendCallAsync();
                        return (IQueryStream <MTableEntity>)null;
                    }
                    else if (ex.GetHttpStatusCode() == HttpStatusCode.PreconditionFailed)
                    {
                        //await monitor.AnnotateLastBackendCallAsync();

                        // Unfortunately we can't assume that anyone who concurrently modifies
                        // the row while the table is in state USE_NEW_HIDE_METADATA will
                        // clean it up, because of InsertOrMerge.  (Consider redesign?)

                        // Re-retrieve row.
                        TableResult retrieveResult = await newTable.ExecuteAsync(
                            TableOperation.Retrieve<MTableEntity>(newEntity.PartitionKey, newEntity.RowKey),
                            requestOptions, operationContext);
                        //await monitor.AnnotateLastBackendCallAsync();
                        if ((HttpStatusCode) retrieveResult.HttpStatusCode == HttpStatusCode.NotFound)
                            return null;
                        else
                        {
                            newEntity = (MTableEntity) retrieveResult.Result;
                            goto Attempt;
                        }
                    }
                    else
                    {
                        throw ChainTableUtils.GenerateInternalException(ex);
                    }
                }
                else
                {
                    await monitor.AnnotateLastBackendCallAsync(
                        spuriousETagChanges:
                            cleanupResult.Etag == null ? null : new List<SpuriousETagChange>
                                {
                                    new SpuriousETagChange(newEntity.PartitionKey, newEntity.RowKey, cleanupResult.Etag)
                                });
                }

                return (IQueryStream<MTableEntity>)null;
            });

            await WalkTableInParallel(oldTable, requestOptions, operationContext, async (oldEntity) =>
            {
                await oldTable.ExecuteAsync(TableOperation.Delete(oldEntity), requestOptions, operationContext);
                await monitor.AnnotateLastBackendCallAsync();

                return (IQueryStream<MTableEntity>)null;
            });
        }

        // In theory, if the migrator dies, you should be able to call this
        // again (using the same configuration service!) to resume.
        // XXX: Remember how far we got through a copy or cleanup pass.
        public async Task MigrateAsync(TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            MTableConfiguration config;
            base.configService.Subscribe(FixedSubscriber<MTableConfiguration>.Instance, out config).Dispose();
            // XXX: Ideally we'd lock out other masters somehow.

            StateSwitch:
            switch (config.state)
            {
                case TableClientState.USE_OLD_HIDE_METADATA:
                    if (IsBugEnabled(MTableOptionalBug.MigrateSkipPreferOld))
                        goto case TableClientState.PREFER_OLD;

                    config = new MTableConfiguration(TableClientState.PREFER_OLD);
                    await configService.PushConfigurationAsync(config);
                    goto StateSwitch;

                case TableClientState.PREFER_OLD:
                    if (IsBugEnabled(MTableOptionalBug.InsertBehindMigrator))
                        goto case TableClientState.PREFER_NEW;

                    config = new MTableConfiguration(TableClientState.PREFER_NEW);
                    await configService.PushConfigurationAsync(config);
                    goto StateSwitch;

                case TableClientState.PREFER_NEW:
                    await CopyAsync(requestOptions, operationContext);
                    if (IsBugEnabled(MTableOptionalBug.MigrateSkipUseNewWithTombstones))
                        goto case TableClientState.USE_NEW_WITH_TOMBSTONES;

                    config = new MTableConfiguration(TableClientState.USE_NEW_WITH_TOMBSTONES);
                    await configService.PushConfigurationAsync(config);
                    goto StateSwitch;

                case TableClientState.USE_NEW_WITH_TOMBSTONES:
                    config = new MTableConfiguration(TableClientState.USE_NEW_HIDE_METADATA);
                    await configService.PushConfigurationAsync(config);
                    goto StateSwitch;

                case TableClientState.USE_NEW_HIDE_METADATA:
                    await CleanupAsync(requestOptions, operationContext);
                    break;
            }
        }
    }
}
