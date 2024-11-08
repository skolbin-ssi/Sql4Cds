﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlTypes;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
#if NETCOREAPP
using Microsoft.PowerPlatform.Dataverse.Client;
#else
using Microsoft.Xrm.Tooling.Connector;
#endif

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// A base class for execution plan nodes that implement a DML operation
    /// </summary>
    abstract class BaseDmlNode : BaseNode, IDmlQueryExecutionPlanNode
    {
        /// <summary>
        /// Temporarily applies global settings to improve the performance of parallel operations
        /// </summary>
        class ParallelConnectionSettings : IDisposable
        {
            private readonly int _connectionLimit;
            private readonly int _threadPoolThreads;
            private readonly int _iocpThreads;
            private readonly bool _expect100Continue;
            private readonly bool _useNagleAlgorithm;

            public ParallelConnectionSettings()
            {
                // Store the current settings
                _connectionLimit = System.Net.ServicePointManager.DefaultConnectionLimit;
                ThreadPool.GetMinThreads(out _threadPoolThreads, out _iocpThreads);
                _expect100Continue = System.Net.ServicePointManager.Expect100Continue;
                _useNagleAlgorithm = System.Net.ServicePointManager.UseNagleAlgorithm;

                // Apply the required settings
                System.Net.ServicePointManager.DefaultConnectionLimit = 65000;
                ThreadPool.SetMinThreads(100, 100);
                System.Net.ServicePointManager.Expect100Continue = false;
                System.Net.ServicePointManager.UseNagleAlgorithm = false;
            }

            public void Dispose()
            {
                // Restore the original settings
                System.Net.ServicePointManager.DefaultConnectionLimit = _connectionLimit;
                ThreadPool.SetMinThreads(_threadPoolThreads, _iocpThreads);
                System.Net.ServicePointManager.Expect100Continue = _expect100Continue;
                System.Net.ServicePointManager.UseNagleAlgorithm = _useNagleAlgorithm;
            }
        }

        /// <summary>
        /// The SQL string that the query was converted from
        /// </summary>
        [Browsable(false)]
        public string Sql { get; set; }

        /// <summary>
        /// The position of the SQL query within the entire query text
        /// </summary>
        [Browsable(false)]
        public int Index { get; set; }

        /// <summary>
        /// The length of the SQL query within the entire query text
        /// </summary>
        [Browsable(false)]
        public int Length { get; set; }

        /// <summary>
        /// The number of the first line of the statement
        /// </summary>
        [Browsable(false)]
        public int LineNumber { get; set; }

        [Browsable(false)]
        public IExecutionPlanNodeInternal Source { get; set; }

        /// <summary>
        /// The instance that this node will be executed against
        /// </summary>
        [Category("Data Source")]
        [Description("The data source this query is executed against")]
        public virtual string DataSource { get; set; }

        /// <summary>
        /// The maximum degree of parallelism to apply to this operation
        /// </summary>
        [Description("The maximum number of operations that will be performed in parallel")]
        public abstract int MaxDOP { get; set; }

        /// <summary>
        /// The number of requests that will be submitted in a single batch
        /// </summary>
        [Description("The number of requests that will be submitted in a single batch")]
        public abstract int BatchSize { get; set; }

        /// <summary>
        /// Indicates if custom plugins should be skipped
        /// </summary>
        [DisplayName("Bypass Plugin Execution")]
        [Description("Indicates if custom plugins should be skipped")]
        public abstract bool BypassCustomPluginExecution { get; set; }

        /// <summary>
        /// Indicates if the operation should be attempted on all records or should fail on the first error
        /// </summary>
        [DisplayName("Continue On Error")]
        [Description("Indicates if the operation should be attempted on all records or should fail on the first error")]
        public abstract bool ContinueOnError { get; set; }

        /// <summary>
        /// Changes system settings to optimise for parallel connections
        /// </summary>
        /// <returns>An object to dispose of to reset the settings to their original values</returns>
        protected IDisposable UseParallelConnections() => new ParallelConnectionSettings();

        /// <summary>
        /// Executes the DML query and returns an appropriate log message
        /// </summary>
        /// <param name="context">The context in which the node is being executed</param>
        /// <param name="recordsAffected">The number of records that were affected by the query</param>
        /// <param name="message">A progress message to display</param>
        public abstract void Execute(NodeExecutionContext context, out int recordsAffected, out string message);

        /// <summary>
        /// Indicates if some errors returned by the server can be silently ignored
        /// </summary>
        protected virtual bool IgnoresSomeErrors => false;

        protected void AddRequiredColumns(IList<string> requiredColumns, List<AttributeAccessor> accessors)
        {
            foreach (var accessor in accessors)
            {
                foreach (var col in accessor.SourceAttributes)
                {
                    if (!requiredColumns.Contains(col))
                        requiredColumns.Add(col);
                }
            }
        }

        /// <summary>
        /// Attempts to fold this node into its source to simplify the query
        /// </summary>
        /// <param name="context">The context in which the node is being built</param>
        /// <param name="hints">Any hints that can control the folding of this node</param>
        /// <returns>The node that should be used in place of this node</returns>
        public virtual IRootExecutionPlanNodeInternal[] FoldQuery(NodeCompilationContext context, IList<OptimizerHint> hints)
        {
            context.ResetGlobalCalculations();

            if (Source is IDataExecutionPlanNodeInternal dataNode)
                Source = dataNode.FoldQuery(context, hints);
            else if (Source is IDataReaderExecutionPlanNode dataSetNode)
                Source = dataSetNode.FoldQuery(context, hints).Single();

            MaxDOP = GetMaxDOP(context, hints);
            BatchSize = GetBatchSize(context, hints);
            BypassCustomPluginExecution = GetBypassPluginExecution(context, hints);
            ContinueOnError = GetContinueOnError(context, hints);

            if (Source is IDataExecutionPlanNodeInternal source)
                Source = context.InsertGlobalCalculations(this, source);

            return new[] { this };
        }

        private int GetMaxDOP(NodeCompilationContext context, IList<OptimizerHint> queryHints)
        {
            if (DataSource == null)
                return 1;

            if (!context.Session.DataSources.TryGetValue(DataSource, out var dataSource))
                throw new NotSupportedQueryFragmentException("Unknown datasource");

            return ParallelismHelper.GetMaxDOP(dataSource, context, queryHints);
        }

        private int GetBatchSize(NodeCompilationContext context, IList<OptimizerHint> queryHints)
        {
            if (queryHints == null)
                return context.Options.BatchSize;

            var batchSizeHint = queryHints
                .OfType<UseHintList>()
                .SelectMany(hint => hint.Hints)
                .Where(hint => hint.Value.StartsWith("BATCH_SIZE_", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (batchSizeHint != null)
            {
                if (!Int32.TryParse(batchSizeHint.Value.Substring(11), out var value) || value < 1)
                    throw new NotSupportedQueryFragmentException(Sql4CdsError.InvalidHint(batchSizeHint)) { Suggestion = "BATCH_SIZE requires a positive integer value" };

                return value;
            }

            return context.Options.BatchSize;
        }

        private bool GetBypassPluginExecution(NodeCompilationContext context, IList<OptimizerHint> queryHints)
        {
            if (queryHints == null)
                return context.Options.BypassCustomPlugins;

            var bypassPluginExecution = queryHints
                .OfType<UseHintList>()
                .Where(hint => hint.Hints.Any(s => s.Value.Equals("BYPASS_CUSTOM_PLUGIN_EXECUTION", StringComparison.OrdinalIgnoreCase)))
                .Any();

            return bypassPluginExecution || context.Options.BypassCustomPlugins;
        }

        private bool GetContinueOnError(NodeCompilationContext context, IList<OptimizerHint> queryHints)
        {
            if (queryHints == null)
                return false;

            var continueOnError = queryHints
                .OfType<UseHintList>()
                .Where(hint => hint.Hints.Any(s => s.Value.Equals("CONTINUE_ON_ERROR", StringComparison.OrdinalIgnoreCase)))
                .Any();

            return continueOnError;
        }

        protected void FoldIdsToConstantScan(NodeCompilationContext context, IList<OptimizerHint> hints, string logicalName, List<AttributeAccessor> accessors)
        {
            if (hints != null && hints.OfType<UseHintList>().Any(hint => hint.Hints.Any(s => s.Value.Equals("NO_DIRECT_DML", StringComparison.OrdinalIgnoreCase))))
                return;

            // Can't do DML operations on base activitypointer table, need to read the record to
            // find the concrete activity type.
            if (logicalName == "activitypointer")
                return;

            // Work out the fields that we should use as the primary key for these records.
            var dataSource = context.Session.DataSources[DataSource];
            var targetMetadata = dataSource.Metadata[logicalName];
            var keyAttributes = EntityReader.GetPrimaryKeyFields(targetMetadata, out _);

            var requiredColumns = accessors.SelectMany(a => a.SourceAttributes).Distinct().ToArray();

            // Skip any ComputeScalar node that is being used to generate additional values,
            // unless they reference additional values in the data source
            var compute = Source as ComputeScalarNode;

            if (compute != null)
            {
                if (compute.Columns.Any(c => c.Value.GetColumns().Except(keyAttributes).Any()))
                    return;

                // Ignore any columns being created by the ComputeScalar node
                foreach (var col in compute.Columns)
                    requiredColumns = requiredColumns.Except(new[] { col.Key }).ToArray();
            }

            if ((compute?.Source ?? Source) is FetchXmlScan fetch)
            {
                var folded = fetch.FoldDmlSource(context, hints, logicalName, requiredColumns, keyAttributes);

                if (compute != null)
                    compute.Source = folded;
                else
                    Source = folded;
            }
            else if (Source is SqlNode sql)
            {
                Source = sql.FoldDmlSource(context, hints, logicalName, requiredColumns, keyAttributes);
            }
        }

        public override IEnumerable<IExecutionPlanNode> GetSources()
        {
            yield return Source;
        }

        /// <summary>
        /// Gets the records to perform the DML operation on
        /// </summary>
        /// <param name="context">The context in which the node is being executed</param>
        /// <param name="schema">The schema of the data source</param>
        /// <returns>The entities to perform the DML operation on</returns>
        protected List<Entity> GetDmlSourceEntities(NodeExecutionContext context, out INodeSchema schema)
        {
            List<Entity> entities;

            if (Source is IDataExecutionPlanNodeInternal dataSource)
            {
                schema = dataSource.GetSchema(context);
                entities = dataSource.Execute(context).ToList();
            }
            else if (Source is IDataReaderExecutionPlanNode dataSetSource)
            {
                var dataReader = dataSetSource.Execute(context, CommandBehavior.Default);

                if (Source is SqlNode sql)
                {
                    schema = sql.GetSchema(context);
                }
                else
                {
                    var schemaTable = dataReader.GetSchemaTable();
                    schema = SchemaConverter.ConvertSchema(schemaTable, context.PrimaryDataSource);
                }

                entities = new List<Entity>();

                while (dataReader.Read())
                {
                    var entity = new Entity();
                    var colIndex = 0;

                    foreach (var col in schema.Schema)
                    {
                        var value = dataReader.GetProviderSpecificValue(colIndex++);

                        if (value is DateTime dt)
                        {
                            if (col.Value.Type.IsSameAs(DataTypeHelpers.Date))
                                value = new SqlDate(dt);
                            else
                                value = new SqlDateTime2(dt);
                        }
                        else if (value is DateTimeOffset dto)
                        {
                            value = new SqlDateTimeOffset(dto);
                        }
                        else if (value is TimeSpan ts)
                        {
                            value = new SqlTime(ts);
                        }
                        else if (value is DBNull)
                        {
                            value = SqlTypeConverter.GetNullValue(col.Value.Type.ToNetType(out _));
                        }

                        entity[col.Key] = value;
                    }

                    entities.Add(entity);
                }
            }
            else
            {
                throw new QueryExecutionException("Unexpected data source") { Node = this };
            }

            return entities;
        }

        /// <summary>
        /// Provides values to include in log messages
        /// </summary>
        protected class OperationNames
        {
            /// <summary>
            /// The name of the operation to include at the start of a log message, e.g. "Updating"
            /// </summary>
            public string InProgressUppercase { get; set; }

            /// <summary>
            /// The name of the operation to include in the middle of a log message, e.g. "updating"
            /// </summary>
            public string InProgressLowercase { get; set; }

            /// <summary>
            /// The completed name of the operation to include in the middle of a log message, e.g. "updated"
            /// </summary>
            public string CompletedLowercase { get; set; }
        }

        /// <summary>
        /// Executes the DML operations required for a set of input records
        /// </summary>
        /// <param name="dataSource">The data source to get the data from</param>
        /// <param name="options"><see cref="IQueryExecutionOptions"/> to indicate how the query can be executed</param>
        /// <param name="entities">The data source entities</param>
        /// <param name="meta">The metadata of the entity that will be affected</param>
        /// <param name="requestGenerator">A function to generate a DML request from a data source entity</param>
        /// <param name="operationNames">The constant strings to use in log messages</param>
        /// <param name="context">The context in which the node is being executed</param>
        /// <param name="recordsAffected">The number of records affected by the operation</param>
        /// <param name="message">A human-readable message to show the number of records affected</param>
        /// <param name="responseHandler">An optional parameter to handle the response messages from the server</param>
        protected void ExecuteDmlOperation(DataSource dataSource, IQueryExecutionOptions options, List<Entity> entities, EntityMetadata meta, Func<Entity,OrganizationRequest> requestGenerator, OperationNames operationNames, NodeExecutionContext context, out int recordsAffected, out string message, Action<OrganizationResponse> responseHandler = null)
        {
            var inProgressCount = 0;
            var count = 0;
            var errorCount = 0;
            var threadCount = 0;

#if NETCOREAPP
            var svc = dataSource.Connection as ServiceClient;
#else
            var svc = dataSource.Connection as CrmServiceClient;
#endif

            var maxDop = MaxDOP;

            if (!ParallelismHelper.CanParallelise(dataSource.Connection))
                maxDop = 1;

            if (maxDop == 1)
                svc = null;

            var useAffinityCookie = maxDop == 1 || entities.Count < 100;

            try
            {
                OrganizationServiceFault fault = null;

                using (UseParallelConnections())
                {
                    Parallel.ForEach(entities,
                        new ParallelOptions { MaxDegreeOfParallelism = maxDop },
                        () =>
                        {
                            var service = svc?.Clone() ?? dataSource.Connection;

#if NETCOREAPP
                            if (!useAffinityCookie && service is ServiceClient crmService)
                                crmService.EnableAffinityCookie = false;
#else
                            if (!useAffinityCookie && service is CrmServiceClient crmService)
                                crmService.EnableAffinityCookie = false;
#endif
                            Interlocked.Increment(ref threadCount);

                            return new { Service = service, EMR = default(ExecuteMultipleRequest) };
                        },
                        (entity, loopState, index, threadLocalState) =>
                        {
                            if (options.CancellationToken.IsCancellationRequested)
                            {
                                loopState.Stop();
                                return threadLocalState;
                            }

                            var request = requestGenerator(entity);

                            if (BypassCustomPluginExecution)
                                request.Parameters["BypassCustomPluginExecution"] = true;

                            if (BatchSize == 1)
                            {
                                var newCount = Interlocked.Increment(ref inProgressCount);
                                var progress = (double)newCount / entities.Count;

                                if (threadCount < 2)
                                    options.Progress(progress, $"{operationNames.InProgressUppercase} {newCount:N0} of {entities.Count:N0} {GetDisplayName(0, meta)} ({progress:P0})...");
                                else
                                    options.Progress(progress, $"{operationNames.InProgressUppercase} {newCount - threadCount + 1:N0}-{newCount:N0} of {entities.Count:N0} {GetDisplayName(0, meta)} ({progress:P0}, {threadCount:N0} threads)...");

                                while (true)
                                {
                                    try
                                    {
                                        var response = dataSource.Execute(threadLocalState.Service, request);
                                        Interlocked.Increment(ref count);

                                        responseHandler?.Invoke(response);
                                        break;
                                    }
                                    catch (FaultException<OrganizationServiceFault> ex)
                                    {
                                        if (ex.Detail.ErrorCode == 429 || // Virtual/elastic tables
                                            ex.Detail.ErrorCode == -2147015902 || // Number of requests exceeded the limit of 6000 over time window of 300 seconds.
                                            ex.Detail.ErrorCode == -2147015903 || // Combined execution time of incoming requests exceeded limit of 1,200,000 milliseconds over time window of 300 seconds. Decrease number of concurrent requests or reduce the duration of requests and try again later.
                                            ex.Detail.ErrorCode == -2147015898) // Number of concurrent requests exceeded the limit of 52.
                                        {
                                            // In case throttling isn't handled by normal retry logic in the service client
                                            var retryAfterSeconds = 2;

                                            if (ex.Detail.ErrorDetails.TryGetValue("Retry-After", out var retryAfter) && (retryAfter is int || retryAfter is string s && Int32.TryParse(s, out _)))
                                                retryAfterSeconds = Convert.ToInt32(retryAfter);

                                            Thread.Sleep(retryAfterSeconds * 1000);
                                            continue;
                                        }

                                        if (FilterErrors(context, request, ex.Detail))
                                        {
                                            if (ContinueOnError)
                                                fault = fault ?? ex.Detail;
                                            else
                                                throw;
                                        }

                                        Interlocked.Increment(ref errorCount);
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                if (threadLocalState.EMR == null)
                                {
                                    threadLocalState = new
                                    {
                                        threadLocalState.Service,
                                        EMR = new ExecuteMultipleRequest
                                        {
                                            Requests = new OrganizationRequestCollection(),
                                            Settings = new ExecuteMultipleSettings
                                            {
                                                ContinueOnError = IgnoresSomeErrors,
                                                ReturnResponses = responseHandler != null
                                            }
                                        }
                                    };
                                }

                                threadLocalState.EMR.Requests.Add(request);

                                if (threadLocalState.EMR.Requests.Count == BatchSize)
                                {
                                    ProcessBatch(threadLocalState.EMR, threadCount, ref count, ref inProgressCount, ref errorCount, entities, operationNames, meta, options, dataSource, threadLocalState.Service, context, responseHandler, ref fault);

                                    threadLocalState = new { threadLocalState.Service, EMR = default(ExecuteMultipleRequest) };
                                }
                            }

                            return threadLocalState;
                        },
                        (threadLocalState) =>
                        {
                            if (threadLocalState.EMR != null)
                                ProcessBatch(threadLocalState.EMR, threadCount, ref count, ref inProgressCount, ref errorCount, entities, operationNames, meta, options, dataSource, threadLocalState.Service, context, responseHandler, ref fault);

                            Interlocked.Decrement(ref threadCount);

                            if (threadLocalState.Service != dataSource.Connection && threadLocalState.Service is IDisposable disposableClient)
                                disposableClient.Dispose();
                        });
                }

                if (fault != null)
                    throw new FaultException<OrganizationServiceFault>(fault, new FaultReason(fault.Message));
            }
            catch (Exception ex)
            {
                var originalEx = ex;

                if (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
                    ex = agg.InnerException;

                if (count > 0)
                    context.Log(new Sql4CdsError(1, 0, $"{count:N0} {GetDisplayName(count, meta)} {operationNames.CompletedLowercase}"));

                if (ex == originalEx)
                    throw;
                else
                    throw ex;
            }

            recordsAffected = count;
            message = $"({count:N0} {GetDisplayName(count, meta)} {operationNames.CompletedLowercase})";
            context.ParameterValues["@@ROWCOUNT"] = (SqlInt32)count;
        }

        protected class BulkApiErrorDetail
        {
            public int RequestIndex { get; set; }
            public Guid Id { get; set; }
            public int StatusCode { get; set; }
        }

        private void ProcessBatch(ExecuteMultipleRequest req, int threadCount, ref int count, ref int inProgressCount, ref int errorCount, List<Entity> entities, OperationNames operationNames, EntityMetadata meta, IQueryExecutionOptions options, DataSource dataSource, IOrganizationService org, NodeExecutionContext context, Action<OrganizationResponse> responseHandler, ref OrganizationServiceFault fault)
        {
            var newCount = Interlocked.Add(ref inProgressCount, req.Requests.Count);
            var progress = (double)newCount / entities.Count;
            var threadCountMessage = threadCount < 2 ? "" : $" ({threadCount:N0} threads)";
            options.Progress(progress, $"{operationNames.InProgressUppercase} {GetDisplayName(0, meta)} {count + errorCount + 1:N0} - {newCount:N0} of {entities.Count:N0}{threadCountMessage}...");
            var resp = ExecuteMultiple(dataSource, org, meta, req);

            if (responseHandler != null)
            {
                foreach (var item in resp.Responses)
                {
                    if (item.Response != null)
                        responseHandler(item.Response);
                }
            }

            var errorResponses = resp.Responses
                .Where(r => r.Fault != null)
                .ToList();

            Interlocked.Add(ref count, req.Requests.Count - errorResponses.Count);
            Interlocked.Add(ref errorCount, errorResponses.Count);

            var error = errorResponses.FirstOrDefault(item => FilterErrors(context, req.Requests[item.RequestIndex], item.Fault));

            if (error != null)
            {
                fault = fault ?? error.Fault;

                if (!ContinueOnError)
                    throw new FaultException<OrganizationServiceFault>(fault, new FaultReason(fault.Message));
            }
        }

        protected virtual bool FilterErrors(NodeExecutionContext context, OrganizationRequest request, OrganizationServiceFault fault)
        {
            return true;
        }

        protected virtual ExecuteMultipleResponse ExecuteMultiple(DataSource dataSource, IOrganizationService org, EntityMetadata meta, ExecuteMultipleRequest req)
        {
            return (ExecuteMultipleResponse)dataSource.Execute(org, req);
        }

        public abstract object Clone();
    }
}
