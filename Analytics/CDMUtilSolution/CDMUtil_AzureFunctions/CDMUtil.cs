using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using CDMUtil.Context.ADLS;
using CDMUtil.Context.ObjectDefinitions;
using CDMUtil.Manifest;
using System;
using CDMUtil.SQL;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Azure.EventGrid.Models;
using System.Linq;
using CDMUtil.Spark;
using Microsoft.Azure.WebJobs.Extensions.ServiceBus;
using Azure.Messaging.ServiceBus;

namespace CDMUtil
{
    public static class CDMUtilWriter
    {
        [FunctionName("getManifestDefinition")]
        public static async Task<IActionResult> getManifestDefinition(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
        ILogger log, ExecutionContext context)
        {
            log.LogInformation("getManifestDefinition request started");

            string tableList = req.Headers["TableList"];

            var path = System.IO.Path.Combine(context.FunctionDirectory, "..\\Manifest\\Artifacts.json");

            var mds = await ManifestWriter.getManifestDefinition(path, tableList);

            return new OkObjectResult(JsonConvert.SerializeObject(mds));

        }
        [FunctionName("manifestToModelJson")]
        public static async Task<IActionResult> manifestToModelJson(
          [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            //get data from 
            string tenantId = req.Headers["TenantId"];
            string storageAccount = req.Headers["StorageAccount"];
            string rootFolder = req.Headers["RootFolder"];
            string localFolder = req.Headers["ManifestLocation"];
            string manifestName = req.Headers["ManifestName"];

            AdlsContext adlsContext = new AdlsContext()
            {
                StorageAccount = storageAccount,
                FileSytemName = rootFolder,
                MSIAuth = true,
                TenantId = tenantId
            };

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");

            ManifestWriter manifestHandler = new ManifestWriter(adlsContext, localFolder, log);

            bool created = await manifestHandler.manifestToModelJson(adlsContext, manifestName, localFolder);

            return new OkObjectResult("{\"Status\":" + created + "}");
        }
        [FunctionName("createManifest")]
        public static async Task<IActionResult> createManifest(
          [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            //get data from 
            string tenantId = req.Headers["TenantId"];
            string storageAccount = req.Headers["StorageAccount"];
            string rootFolder = req.Headers["RootFolder"];
            string localFolder = req.Headers["LocalFolder"];
            string createModelJson = req.Headers["CreateModelJson"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            EntityList entityList = JsonConvert.DeserializeObject<EntityList>(requestBody);

            AdlsContext adlsContext = new AdlsContext()
            {
                StorageAccount = storageAccount,
                FileSytemName = rootFolder,
                MSIAuth = true,
                TenantId = tenantId
            };

            ManifestWriter manifestHandler = new ManifestWriter(adlsContext, localFolder, log);
            bool createModel = false;
            if (createModelJson != null && createModelJson.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                createModel = true;
            }

            bool ManifestCreated = await manifestHandler.createManifest(entityList, log, createModel);

            //Folder structure Tables/AccountReceivable/Group
            var subFolders = localFolder.Split('/');
            string localFolderPath = "";

            for (int i = 0; i < subFolders.Length - 1; i++)
            {
                var currentFolder = subFolders[i];
                var nextFolder = subFolders[i + 1];
                localFolderPath = $"{localFolderPath}/{currentFolder}";

                ManifestWriter SubManifestHandler = new ManifestWriter(adlsContext, localFolderPath, log);
                await SubManifestHandler.createSubManifest(currentFolder, nextFolder);
            }

            var status = new ManifestStatus() { ManifestName = entityList.manifestName, IsManifestCreated = ManifestCreated };

            return new OkObjectResult(JsonConvert.SerializeObject(status));
        }

    }
    public static class CDMUtilReader

    {
        [FunctionName("manifestToSQL")]
        public static async Task<IActionResult> manifestToSQL(
          [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("HTTP trigger manifestToSQL...");

            //get configurations data 
            AppConfigurations c = GetAppConfigurations(req, context);

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();
            await ManifestReader.manifestToSQLMetadata(c, metadataList, log, c.rootFolder);

            SQLStatements statements;

            if (!String.IsNullOrEmpty(c.synapseOptions.targetSparkEndpoint))
            {
                statements = SparkHandler.executeSpark(c, metadataList, log);
            }
            else
            {
                statements = SQLHandler.executeSQL(c, metadataList, log);
            }

            return new OkObjectResult(JsonConvert.SerializeObject(statements));
        }

        [FunctionName("manifestToSQLDDL")]
        public static async Task<IActionResult> manifestToSQLDDL(
          [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            //get configurations data 
            AppConfigurations c = GetAppConfigurations(req, context);

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();
            await ManifestReader.manifestToSQLMetadata(c, metadataList, log, c.rootFolder);

            // convert metadata to DDL
            log.Log(LogLevel.Information, "Converting metadata to DDL");
            List<SQLStatement> statementsList;

            if (!String.IsNullOrEmpty(c.synapseOptions.targetSparkEndpoint))
            {
                statementsList = SparkHandler.metadataToSparkStmt(metadataList, c, log);
            }
            else
            {
                statementsList = await SQLHandler.sqlMetadataToDDL(metadataList, c, log);
            }


            return new OkObjectResult(JsonConvert.SerializeObject(statementsList));
        }
        [FunctionName("getManifestDetails")]
        public static async Task<IActionResult> getManifestDetails(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
        ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request- manifestToMetadata");

            //get configurations data 
            AppConfigurations c = GetAppConfigurations(req, context);

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");

            ManifestDefinitions manifestDefinitions = await ManifestReader.getManifestDefinitions(c, log);

            List<ManifestDefinition> manifests = manifestDefinitions.Manifests;
            dynamic adlsConfig = manifestDefinitions.Config;

            // log.LogInformation(JsonConvert.SerializeObject(manifestDefinitions));
            List<SQLMetadata> metadataList = new List<SQLMetadata>();
            List<SQLStatement> statementsList = new List<SQLStatement>();

            foreach (ManifestDefinition manifest in manifests)
            {
                string manifestURL = $"https://{adlsConfig.config.hostname}{adlsConfig.config.root}{manifest.ManifestLocation}/{manifest.ManifestName}.manifest.cdm.json";
                List<string> tableNames = manifest.Tables.Select(x => x.TableName).ToList();
                string tables = String.Join(",", tableNames);
                c.rootFolder = manifest.ManifestLocation;
                c.manifestName = $"{manifest.ManifestName}.manifest.cdm.json";
                c.tableList = tableNames;

                await ManifestReader.manifestToSQLMetadata(c, metadataList, log, c.rootFolder);

                log.Log(LogLevel.Information, "Converting metadata to DDL");

                if (!String.IsNullOrEmpty(c.synapseOptions.targetSparkEndpoint))
                {
                    statementsList = SparkHandler.metadataToSparkStmt(metadataList, c, log);
                }
                else
                {
                    statementsList = await SQLHandler.sqlMetadataToDDL(metadataList, c, log);
                }
            }

            return new OkObjectResult(JsonConvert.SerializeObject(
                new { CDMMetadata = metadataList, SQLStatements = statementsList }));
        }
        [FunctionName("getMetadata")]
        public static async Task<IActionResult> getMetadata(
         [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
         ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request- manifestToMetadata");

            //get configurations data 
            AppConfigurations c = GetAppConfigurations(req, context);

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();
            await ManifestReader.manifestToSQLMetadata(c, metadataList, log, c.rootFolder);

            return new OkObjectResult(JsonConvert.SerializeObject(metadataList));
        }
        [FunctionName("EventGrid_CDMToSynapseView")]
        public static void CDMToSynapseView([EventGridTrigger] EventGridEvent eventGridEvent, ILogger log, ExecutionContext context)
        {

            dynamic eventData = eventGridEvent.Data;
            string ManifestURL = eventData.url;

            log.LogInformation(ManifestURL);

            if (!ManifestURL.EndsWith(".cdm.json"))
            {
                log.LogWarning("Invalid manifestURL");
                return;
            }
            //get configurations data 
            AppConfigurations c = GetAppConfigurations(null, context, eventGridEvent);

            log.LogInformation(eventGridEvent.Data.ToString());
            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();

            ManifestReader.manifestToSQLMetadata(c, metadataList, log, c.rootFolder);

            //sometimes the JSON file is dropped before the folder path exists, wait for the folder to exist before attempting to create the table
            // Applies only to Tables and ChangeFeed folder
            if (ManifestURL.Contains("/Entities/") == false)
            {
                ManifestReader.WaitForFolderPathsToExist(c, metadataList, log).Wait();
            }

            if (!String.IsNullOrEmpty(c.synapseOptions.targetSparkEndpoint))
            {
                SparkHandler.executeSpark(c, metadataList, log);
            }
            else
            {
                SQLHandler.executeSQL(c, metadataList, log);
            }

        }

        public static string getConfigurationValue(HttpRequest req, string token, string url = null)
        {
            string ConfigValue = null;

            if (req != null && !String.IsNullOrEmpty(req.Headers[token]))
            {
                ConfigValue = req.Headers[token];
            }
            else if (!String.IsNullOrEmpty(url))
            {
                var uri = new Uri(url);
                var storageAccount = uri.Host.Split('.')[0];
                var pathSegments = uri.AbsolutePath.Split('/').Skip(1); // because of the leading /, the first entry will always be blank and we can disregard it
                var n = pathSegments.Count();
                while (n >= 0 && ConfigValue == null)
                    ConfigValue = System.Environment.GetEnvironmentVariable($"{storageAccount}:{String.Join(":", pathSegments.Take(n--))}{(n > 0 ? ":" : "")}{token}");

            }
            if (ConfigValue == null)
            {
                ConfigValue = System.Environment.GetEnvironmentVariable(token);
            }

            return ConfigValue;
        }
        public static AppConfigurations GetAppConfigurations(HttpRequest req, ExecutionContext context, EventGridEvent eventGridEvent = null)
        {

            string ManifestURL;

            if (eventGridEvent != null)
            {
                dynamic eventData = eventGridEvent.Data;

                ManifestURL = eventData.url;
            }
            else
            {
                ManifestURL = getConfigurationValue(req, "ManifestURL");
            }
            if (ManifestURL.ToLower().EndsWith("cdm.json") == false && ManifestURL.ToLower().EndsWith("model.json") == false)
            {
                throw new Exception($"Invalid manifest URL:{ManifestURL}");
            }
            string AccessKey = getConfigurationValue(req, "AccessKey", ManifestURL);

            string tenantId = getConfigurationValue(req, "TenantId", ManifestURL);
            string connectionString = getConfigurationValue(req, "SQLEndpoint", ManifestURL);
            string DDLType = getConfigurationValue(req, "DDLType", ManifestURL);

            string targetSparkConnection = getConfigurationValue(req, "TargetSparkConnection", ManifestURL);

            AppConfigurations AppConfiguration = new AppConfigurations(tenantId, ManifestURL, AccessKey, connectionString, DDLType, targetSparkConnection);

            string AXDBConnectionString = getConfigurationValue(req, "AXDBConnectionString", ManifestURL);


            if (AppConfiguration.tableList == null)
            {
                string TableNames = getConfigurationValue(req, "TableNames", ManifestURL);
                AppConfiguration.tableList = String.IsNullOrEmpty(TableNames) ? new List<string>() { "*" } : new List<string>(TableNames.Split(','));
            }

            if (AXDBConnectionString != null)
                AppConfiguration.AXDBConnectionString = AXDBConnectionString;

            string schema = getConfigurationValue(req, "Schema", ManifestURL);
            if (schema != null)
                AppConfiguration.synapseOptions.schema = schema;

            string fileFormat = getConfigurationValue(req, "FileFormat", ManifestURL);
            if (fileFormat != null)
                AppConfiguration.synapseOptions.fileFormatName = fileFormat;

            string ParserVersion = getConfigurationValue(req, "ParserVersion", ManifestURL);
            if (ParserVersion != null)
                AppConfiguration.synapseOptions.parserVersion = ParserVersion;

            string TranslateEnum = getConfigurationValue(req, "TranslateEnum", ManifestURL);
            if (TranslateEnum != null)
                AppConfiguration.synapseOptions.TranslateEnum = bool.Parse(TranslateEnum);

            string DefaultStringLength = getConfigurationValue(req, "DefaultStringLength", ManifestURL);

            if (DefaultStringLength != null)
            {
                AppConfiguration.synapseOptions.DefaultStringLength = Int16.Parse(DefaultStringLength);
            }

            AppConfiguration.SourceColumnProperties = Path.Combine(context.FunctionAppDirectory, "SourceColumnProperties.json");
            AppConfiguration.ReplaceViewSyntax = Path.Combine(context.FunctionAppDirectory, "ReplaceViewSyntax.json");


            string ProcessEntities = getConfigurationValue(req, "ProcessEntities", ManifestURL);

            if (ProcessEntities != null)
            {
                AppConfiguration.ProcessEntities = bool.Parse(ProcessEntities);
                AppConfiguration.ProcessEntitiesFilePath = Path.Combine(context.FunctionAppDirectory, "EntityList.json");

            }
            string CreateStats = getConfigurationValue(req, "CreateStats", ManifestURL);

            if (CreateStats != null)
            {
                AppConfiguration.synapseOptions.createStats = bool.Parse(CreateStats);
            }

            string ProcessSubTableSuperTables = getConfigurationValue(req, "ProcessSubTableSuperTables", ManifestURL);

            if (ProcessSubTableSuperTables != null)
            {
                AppConfiguration.ProcessSubTableSuperTables = bool.Parse(ProcessSubTableSuperTables);
                AppConfiguration.ProcessSubTableSuperTablesFilePath = Path.Combine(context.FunctionAppDirectory, "SubTableSuperTableList.json");

            }
            string ServicePrincipalBasedAuthentication = getConfigurationValue(req, "ServicePrincipalBasedAuthentication", ManifestURL);

            if (ServicePrincipalBasedAuthentication != null)
            {
                AppConfiguration.synapseOptions.servicePrincipalBasedAuthentication = bool.Parse(ServicePrincipalBasedAuthentication);
                if (AppConfiguration.synapseOptions.servicePrincipalBasedAuthentication)
                {
                    AppConfiguration.synapseOptions.servicePrincipalTenantId = tenantId;
                    string servicePrincipalAppId = getConfigurationValue(req, "ServicePrincipalAppId", ManifestURL);
                    if (servicePrincipalAppId != null)
                        AppConfiguration.synapseOptions.servicePrincipalAppId = servicePrincipalAppId;
                    string servicePrincipalSecret = getConfigurationValue(req, "ServicePrincipalSecret", ManifestURL);
                    if (servicePrincipalSecret != null)
                        AppConfiguration.synapseOptions.servicePrincipalSecret = servicePrincipalSecret;
                }
            }

            return AppConfiguration;
        }

        public static AppConfigurations GetAppConfigurationsServiceBusTrigger(ExecutionContext context, ILogger log, string url)
        {
            HttpRequest req = null;
            string ManifestURL;

            if (url != null)
            {
                ManifestURL = url;
            }
            else
            {
                ManifestURL = getConfigurationValue(req, "ManifestURL");
            }
            if (ManifestURL.ToLower().EndsWith("cdm.json") == false && ManifestURL.ToLower().EndsWith("model.json") == false)
            {
                throw new Exception($"Invalid manifest URL:{ManifestURL}");
            }
            string AccessKey = getConfigurationValue(req, "AccessKey", ManifestURL);

            string tenantId = getConfigurationValue(req, "TenantId", ManifestURL);
            string connectionString = getConfigurationValue(req, "SQLEndPoint", ManifestURL);
            string DDLType = getConfigurationValue(req, "DDLType", ManifestURL);

            string targetSparkConnection = getConfigurationValue(req, "TargetSparkConnection", ManifestURL);

            log.LogInformation($"TenantId={tenantId}");
            log.LogInformation($"SQLEndpoint={connectionString}");

            AppConfigurations AppConfiguration = new AppConfigurations(tenantId, ManifestURL, AccessKey, connectionString, DDLType, targetSparkConnection);

            string AXDBConnectionString = getConfigurationValue(req, "AXDBConnectionString", ManifestURL);


            if (AppConfiguration.tableList == null)
            {
                string TableNames = getConfigurationValue(req, "TableNames", ManifestURL);
                AppConfiguration.tableList = String.IsNullOrEmpty(TableNames) ? new List<string>() { "*" } : new List<string>(TableNames.Split(','));
            }

            if (AXDBConnectionString != null)
                AppConfiguration.AXDBConnectionString = AXDBConnectionString;

            string schema = getConfigurationValue(req, "Schema", ManifestURL);
            if (schema != null)
                AppConfiguration.synapseOptions.schema = schema;

            string fileFormat = getConfigurationValue(req, "FileFormat", ManifestURL);
            if (fileFormat != null)
                AppConfiguration.synapseOptions.fileFormatName = fileFormat;

            string ParserVersion = getConfigurationValue(req, "ParserVersion", ManifestURL);
            if (ParserVersion != null)
                AppConfiguration.synapseOptions.parserVersion = ParserVersion;

            string TranslateEnum = getConfigurationValue(req, "TranslateEnum", ManifestURL);
            if (TranslateEnum != null)
                AppConfiguration.synapseOptions.TranslateEnum = bool.Parse(TranslateEnum);

            string DefaultStringLength = getConfigurationValue(req, "DefaultStringLength", ManifestURL);

            if (DefaultStringLength != null)
            {
                AppConfiguration.synapseOptions.DefaultStringLength = Int16.Parse(DefaultStringLength);
            }

            //AppConfiguration.SourceColumnProperties = Path.Combine(context.FunctionAppDirectory, "SourceColumnProperties.json");
            //AppConfiguration.ReplaceViewSyntax = Path.Combine(context.FunctionAppDirectory, "ReplaceViewSyntax.json");


            string ProcessEntities = getConfigurationValue(req, "ProcessEntities", ManifestURL);

            if (ProcessEntities != null)
            {
                AppConfiguration.ProcessEntities = bool.Parse(ProcessEntities);
                //AppConfiguration.ProcessEntitiesFilePath = Path.Combine(context.FunctionAppDirectory, "EntityList.json");

            }
            string CreateStats = getConfigurationValue(req, "CreateStats", ManifestURL);

            if (CreateStats != null)
            {
                AppConfiguration.synapseOptions.createStats = bool.Parse(CreateStats);
            }

            string ProcessSubTableSuperTables = getConfigurationValue(req, "ProcessSubTableSuperTables", ManifestURL);

            if (ProcessSubTableSuperTables != null)
            {
                AppConfiguration.ProcessSubTableSuperTables = bool.Parse(ProcessSubTableSuperTables);
                //AppConfiguration.ProcessSubTableSuperTablesFilePath = Path.Combine(context.FunctionAppDirectory, "SubTableSuperTableList.json");

            }
            string ServicePrincipalBasedAuthentication = getConfigurationValue(req, "ServicePrincipalBasedAuthentication", ManifestURL);

            if (ServicePrincipalBasedAuthentication != null)
            {
                AppConfiguration.synapseOptions.servicePrincipalBasedAuthentication = bool.Parse(ServicePrincipalBasedAuthentication);
                if (AppConfiguration.synapseOptions.servicePrincipalBasedAuthentication)
                {
                    AppConfiguration.synapseOptions.servicePrincipalTenantId = tenantId;
                    string servicePrincipalAppId = getConfigurationValue(req, "ServicePrincipalAppId", ManifestURL);
                    if (servicePrincipalAppId != null)
                        AppConfiguration.synapseOptions.servicePrincipalAppId = servicePrincipalAppId;
                    string servicePrincipalSecret = getConfigurationValue(req, "ServicePrincipalSecret", ManifestURL);
                    if (servicePrincipalSecret != null)
                        AppConfiguration.synapseOptions.servicePrincipalSecret = servicePrincipalSecret;
                }
            }

            return AppConfiguration;
        }
        [FunctionName("ServiceBusQueueTriggerPL")]
        public static void Run(
            [ServiceBusTrigger("sb-queue-cdmutil-fin-costmgt-predev", Connection = "AzureWebJobsServiceBus")]
            EventGridEvent serviceBusQueueItem,
            Int32 deliveryCount,
            DateTime enqueuedTimeUtc,
            string messageId,
            ILogger log)
        {
            log.LogInformation($"C# ServiceBus queue trigger function processed message: {serviceBusQueueItem}");
            log.LogInformation($"EnqueuedTimeUtc={enqueuedTimeUtc}");
            log.LogInformation($"DeliveryCount={deliveryCount}");
            log.LogInformation($"MessageId={messageId}");

            dynamic eventData = serviceBusQueueItem.Data;
            string ManifestURL = eventData.url;

            log.LogInformation($"ManifestURL={ManifestURL}");

            if (!ManifestURL.EndsWith(".cdm.json"))
            {
                log.LogWarning("Invalid manifestURL");
                return;
            }
            //get configurations data 
            AppConfigurations c = GetAppConfigurationsServiceBusTrigger(null, log, ManifestURL);

            log.LogInformation($"config1={JsonConvert.SerializeObject(c, Formatting.Indented)}");

            log.LogInformation(serviceBusQueueItem.ToString());
            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();

            ManifestReader.manifestToSQLMetadata(c, metadataList, log, c.rootFolder);

            //sometimes the JSON file is dropped before the folder path exists, wait for the folder to exist before attempting to create the table
            // Applies only to Tables and ChangeFeed folder
            if (ManifestURL.Contains("/Entities/") == false)
            {
                ManifestReader.WaitForFolderPathsToExist(c, metadataList, log).Wait();
                log.Log(LogLevel.Information, "Reading Manifest metadata");
            }

            if (!String.IsNullOrEmpty(c.synapseOptions.targetSparkEndpoint))
            {
                SparkHandler.executeSpark(c, metadataList, log);
            }
            else
            {
                SQLHandler.executeSQL(c, metadataList, log);
                log.Log(LogLevel.Information, "CDMUtil trigger executeSQL");
            }
            log.Log(LogLevel.Information, "CDMUtil trigger complete");
        }
    }
}
